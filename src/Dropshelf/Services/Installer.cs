using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

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

    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Dropshelf");

    public static string InstalledExe { get; } = Path.Combine(InstallDir, "Dropshelf.exe");

    /// <summary>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Dropshelf.lnk</summary>
    public static string StartMenuShortcut { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Dropshelf.lnk");

    /// <summary>Named event another launch sets to ask this instance to quit (per user, so one user cannot close another's).</summary>
    public static string QuitEventName { get; } = "Dropshelf.Quit." + UserId();

    /// <summary>True when the process was started from the installed location.</summary>
    public static bool IsRunningInstalled =>
        ProcessPath() is { } exe && string.Equals(exe, InstalledExe, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>The exe the "launch at startup" registry value should point at, or null when nothing should be registered (dev runs).</summary>
    public static string? AutostartTarget()
    {
        if (IsRunningInstalled)
        {
            return InstalledExe;
        }

        if (File.Exists(InstalledExe))
        {
            return InstalledExe; // running some other copy, but the installed one is what should start at sign-in
        }

        return IsSingleFileExe && !Debugger.IsAttached ? Environment.ProcessPath : null;
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

            // Ask a running instance (the installed copy, usually) to quit so its exe can be overwritten.
            bool signalled = false;
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out EventWaitHandle? quit))
            {
                using (quit)
                {
                    quit.Set();
                    signalled = true;
                }
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
                return false;
            }
            finally
            {
                mutex.ReleaseMutex(); // the relaunched copy needs it
            }

            CreateStartMenuShortcut();

            var psi = new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = InstallDir };
            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            Process.Start(psi);
            Log.Info($"Installed to {InstalledExe} and relaunched from there");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or InvalidOperationException or ApplicationException)
        {
            Log.Error("Self-install failed; running from the current location", ex);
            return false;
        }
    }

    /// <summary>The old process may still be releasing the file for a moment after it released the mutex, so retry briefly.</summary>
    private static void CopyOverInstalled(string source)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(source, InstalledExe, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(200);
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
    /// Starts a hidden <c>cmd</c> that waits for this process to exit, then deletes the installed exe (and, optionally, the
    /// data folder). Call right before shutting down. The delay uses <c>ping</c> because <c>timeout</c> fails without a console.
    /// </summary>
    public static void ScheduleCleanup(bool deleteData)
    {
        try
        {
            string command = $"ping 127.0.0.1 -n 4 >nul & del /f /q \"{InstalledExe}\" & rd \"{InstallDir}\"";
            if (deleteData)
            {
                command += $" & rd /s /q \"{AppPaths.Root}\"";
            }

            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c {command}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("Could not schedule the uninstall cleanup", ex);
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private static string? ProcessPath()
    {
        try
        {
            return Environment.ProcessPath is { Length: > 0 } p ? Path.GetFullPath(p) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

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
