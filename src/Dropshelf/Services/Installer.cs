using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Dropshelf.Interop;

namespace Dropshelf.Services;

/// <summary>
/// Per-user "install": the single-file exe copies itself to %LOCALAPPDATA%\Programs\Dropshelf, relaunches from there and
/// keeps a Start Menu shortcut. Launching a newer exe from anywhere else asks the running (installed) instance to quit,
/// overwrites the installed copy and starts it, so updating is "download the new exe and double-click it".
/// Nothing here needs admin rights, and every step fails soft: if anything goes wrong the app just keeps running from
/// where it was started.
/// </summary>
internal static class Installer
{
    public const string InstanceMutexName = @"Local\Dropshelf.SingleInstance";
    public const string NoInstallArgument = "--no-install";
    public const string AutostartArgument = "--autostart";
    public const string RestartedArgument = "--restarted";

    private const int QuitWaitMs = 5000;
    private const int ProcessExitWaitMs = 10000;
    private const int CopyRetryMs = 10000;

    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Dropshelf");

    public static string InstalledExe { get; } = Path.Combine(InstallDir, "Dropshelf.exe");

    /// <summary>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Dropshelf.lnk</summary>
    public static string StartMenuShortcut { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Dropshelf.lnk");

    /// <summary>Named event another launch sets to ask this instance to quit (per user, so one user cannot close another's).</summary>
    public static string QuitEventName { get; } = "Dropshelf.Quit." + UserId();

    /// <summary>Set when an update could not be copied over the installed exe, so the app can tell the user once the tray icon exists.</summary>
    public static string? UpdateFailedNotice { get; private set; }

    /// <summary>True when the process was started from the installed location (same file, however the path is spelled).</summary>
    public static bool IsRunningInstalled => SamePath(Environment.ProcessPath, InstalledExe);

    /// <summary>
    /// True for a real single-file publish started directly (not <c>dotnet Dropshelf.dll</c>, not a debug build's loose
    /// apphost that needs its DLLs next to it): only then is copying the one exe enough.
    /// </summary>
    public static bool IsSingleFileExe
    {
        get
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Single-file bundles report an empty Assembly.Location; that is exactly what we test for.
#pragma warning disable IL3000
            return string.IsNullOrEmpty(typeof(Installer).Assembly.Location);
#pragma warning restore IL3000
        }
    }

    /// <summary>Can the app start a fresh copy of itself (crash relaunch)? Not under a debugger or the dotnet host.</summary>
    public static bool CanRelaunch =>
        !Debugger.IsAttached && Environment.ProcessPath is { Length: > 0 } exe
        && !string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The exe the "launch at startup" registry value should point at, or null when nothing should be registered (dev runs,
    /// or a copy that is not installed: a temp or Downloads path would vanish).
    /// </summary>
    public static string? AutostartTarget()
    {
        if (IsRunningInstalled || File.Exists(InstalledExe))
        {
            return InstalledExe; // running some other copy, but the installed one is what should start at sign-in
        }

        string? exe = ResolvePath(Environment.ProcessPath);
        return exe != null && !Debugger.IsAttached && AppPaths.IsInside(exe, InstallDir) ? exe : null;
    }

    /// <summary>
    /// First-run step: when started from somewhere other than the install folder, installs (or updates) there and relaunches.
    /// Returns true if the installed copy was started, in which case the caller must exit. False means keep running here.
    /// </summary>
    public static bool TryInstallAndRelaunch(string[] args)
    {
        if (args.Contains(NoInstallArgument, StringComparer.OrdinalIgnoreCase)
            || Debugger.IsAttached || !IsSingleFileExe || IsRunningInstalled)
        {
            return false;
        }

        try
        {
            string source = Environment.ProcessPath!;

            // Never downgrade: an older exe leaves the installed one alone and just makes sure it is running.
            if (File.Exists(InstalledExe) && IsOlderThan(source, InstalledExe))
            {
                Log.Info("This exe is older than the installed one; leaving the installed copy as it is");
                if (!IsInstanceRunning())
                {
                    LaunchInstalled(args);
                }

                return true;
            }

            // Ask a running instance (the installed copy, usually) to quit so its exe can be overwritten, then wait for it
            // to be really gone: it is found by its path, so no PID has to be handed over.
            List<Process> running = RunningInstalledProcesses();
            bool signalled = false;
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out EventWaitHandle? quit))
            {
                using (quit)
                {
                    quit.Set();
                    signalled = true;
                }
            }

            if (!WaitForExit(running, signalled ? ProcessExitWaitMs : 0))
            {
                Log.Error("Install skipped: the installed Dropshelf is still running and did not quit in time");
                return false;
            }

            using var mutex = new Mutex(false, InstanceMutexName);
            bool owned;
            try
            {
                owned = mutex.WaitOne(signalled ? QuitWaitMs : 0);
            }
            catch (AbandonedMutexException)
            {
                owned = true; // the previous instance died holding it; we own it now
            }

            if (!owned)
            {
                Log.Error("Install skipped: another Dropshelf is still running and did not quit in time");
                return false;
            }

            try
            {
                Directory.CreateDirectory(InstallDir);
                CopyOverInstalled(source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Could not copy Dropshelf to the install folder; running from the current location", ex);
                UpdateFailedNotice = "Couldn't update Dropshelf, running this copy instead.";
                return false;
            }
            finally
            {
                mutex.ReleaseMutex(); // the relaunched copy needs it
            }

            CreateStartMenuShortcut();
            LaunchInstalled(args);
            Log.Info($"Installed to {InstalledExe} and relaunched from there");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
                                       or InvalidOperationException or ApplicationException)
        {
            Log.Error("Self-install failed; running from the current location", ex);
            return false;
        }
    }

    private static void LaunchInstalled(string[] args)
    {
        var psi = new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = InstallDir };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        Process.Start(psi);
    }

    /// <summary>True while any Dropshelf holds the single-instance mutex.</summary>
    private static bool IsInstanceRunning()
    {
        if (Mutex.TryOpenExisting(InstanceMutexName, out Mutex? existing))
        {
            existing.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>File version (major.minor.build.revision, from the exe's version resource) or null when it cannot be read.</summary>
    private static Version? FileVersionOf(string path)
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True only when both versions are known and <paramref name="source"/> is strictly lower; equal or unknown means "replace".</summary>
    private static bool IsOlderThan(string source, string installed) =>
        FileVersionOf(source) is { } s && FileVersionOf(installed) is { } i && s < i;

    /// <summary>Other running Dropshelf processes that were started from the installed exe.</summary>
    private static List<Process> RunningInstalledProcesses()
    {
        var found = new List<Process>();
        foreach (Process p in Process.GetProcessesByName("Dropshelf"))
        {
            try
            {
                if (p.Id != Environment.ProcessId && SamePath(p.MainModule?.FileName, InstalledExe))
                {
                    found.Add(p);
                    continue;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // Not ours to inspect (other user / elevated) or already gone.
            }

            p.Dispose();
        }

        return found;
    }

    /// <summary>Waits up to <paramref name="timeoutMs"/> in total for the processes to exit and disposes them. True when all are gone.</summary>
    private static bool WaitForExit(List<Process> processes, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        bool allGone = true;
        foreach (Process p in processes)
        {
            using (p)
            {
                try
                {
                    if (!p.WaitForExit((int)Math.Max(0, deadline - Environment.TickCount64)))
                    {
                        allGone = false;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Already gone.
                }
            }
        }

        return allGone;
    }

    /// <summary>
    /// Copies to <c>Dropshelf.exe.new</c> and renames it over the installed exe, so a half-written exe is never left behind.
    /// The old process may still be releasing the file for a moment, so both steps are retried for about 10 s.
    /// </summary>
    private static void CopyOverInstalled(string source)
    {
        string tmp = InstalledExe + ".new";
        long deadline = Environment.TickCount64 + CopyRetryMs;
        while (true)
        {
            try
            {
                File.Copy(source, tmp, overwrite: true);
                File.Move(tmp, InstalledExe, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(250);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try
                {
                    File.Delete(tmp);
                }
                catch (Exception delEx) when (delEx is IOException or UnauthorizedAccessException)
                {
                    // Best effort; the next update overwrites it.
                }

                throw;
            }
        }
    }

    /// <summary>Creates the Start Menu shortcut (via the WScript.Shell COM object) if the installed exe has none yet. Best-effort.</summary>
    public static void EnsureStartMenuShortcut()
    {
        if (IsRunningInstalled && !File.Exists(StartMenuShortcut))
        {
            CreateStartMenuShortcut();
        }
    }

    private static void CreateStartMenuShortcut()
    {
        object? shell = null;
        object? link = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                return;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell == null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(StartMenuShortcut)!);
            dynamic dynShell = shell;
            link = dynShell.CreateShortcut(StartMenuShortcut);
            dynamic dynLink = link!;
            dynLink.TargetPath = InstalledExe;
            dynLink.WorkingDirectory = InstallDir;
            dynLink.Description = "Dropshelf";
            dynLink.IconLocation = InstalledExe + ",0";
            dynLink.Save();
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                       or InvalidOperationException or NotSupportedException)
        {
            Log.Error("Could not create the Start Menu shortcut", ex);
        }
        finally
        {
            if (link != null && Marshal.IsComObject(link))
            {
                Marshal.FinalReleaseComObject(link);
            }

            if (shell != null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    // ---- Uninstall --------------------------------------------------------------------------------

    /// <summary>Removes the autostart entry and the Start Menu shortcut. The exe and data are removed after exit by <see cref="ScheduleCleanup"/>.</summary>
    public static void RemoveIntegration()
    {
        StartupRegistration.Set(false);
        try
        {
            if (File.Exists(StartMenuShortcut))
            {
                File.Delete(StartMenuShortcut);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not delete the Start Menu shortcut", ex);
        }
    }

    /// <summary>
    /// Starts a hidden <c>cmd</c> that waits (up to about 30 s) for this process to exit, then deletes the installed exe, the
    /// install folder if it is empty and, optionally, the data folder. Call right before shutting down. The paths travel in
    /// environment variables (<c>cmd</c> expands <c>%VARS%</c> even inside quotes, so they are never put into the command text)
    /// and the delay uses <c>ping</c> because <c>timeout</c> fails without a console.
    /// </summary>
    public static void ScheduleCleanup(bool deleteData)
    {
        try
        {
            bool wipeData = deleteData && IsSafeDataRoot(AppPaths.Root);
            if (deleteData && !wipeData)
            {
                Log.Error($"Not deleting the data folder: '{AppPaths.Root}' is not %LOCALAPPDATA%\\Dropshelf");
            }

            string cleanup = "del /f /q \"%DS_EXE%\" & rd \"%DS_DIR%\"" + (wipeData ? " & rd /s /q \"%DS_DATA%\"" : string.Empty);
            string command = "for /l %i in (1,1,30) do ((tasklist /FI \"PID eq %DS_PID%\" | find \"%DS_PID%\" >nul || (" + cleanup
                             + " & exit)) & ping 127.0.0.1 -n 2 >nul) & " + cleanup;

            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c " + command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.Environment["DS_EXE"] = InstalledExe;
            psi.Environment["DS_DIR"] = InstallDir;
            psi.Environment["DS_PID"] = Environment.ProcessId.ToString();
            if (wipeData)
            {
                psi.Environment["DS_DATA"] = AppPaths.Root;
            }

            Process.Start(psi);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Error("Could not schedule the uninstall cleanup", ex);
        }
    }

    /// <summary>Only a fully qualified <c>%LOCALAPPDATA%\Dropshelf</c> may be wiped recursively.</summary>
    private static bool IsSafeDataRoot(string root)
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData) || !Path.IsPathFullyQualified(root))
            {
                return false;
            }

            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localAppData));
            return string.Equals(Path.GetFileName(full), "Dropshelf", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    /// <summary>Full path with short (8.3) segments expanded, or null for an empty or invalid path.</summary>
    private static string? ResolvePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(path);
            var buffer = new StringBuilder(1024);
            uint length = NativeMethods.GetLongPathName(full, buffer, (uint)buffer.Capacity);
            return length > 0 && length < buffer.Capacity ? buffer.ToString() : full; // 0 = file missing, else too long: keep the full path
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>True when both paths name the same file once fully resolved (case-insensitive).</summary>
    private static bool SamePath(string? a, string? b) =>
        ResolvePath(a) is { } x && ResolvePath(b) is { } y && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

    private static string UserId()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return Environment.UserName;
        }
    }
}
