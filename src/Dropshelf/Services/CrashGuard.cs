using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Dropshelf.Services;

/// <summary>
/// Keeps the app running: a fatal error is logged and Dropshelf starts a fresh copy of itself (<c>--restarted</c>) before
/// exiting. A crash-loop guard allows at most 3 relaunches within 30 minutes (timestamps in restarts.json); the history is
/// cleared once the app has run for 10 minutes without crashing. After that limit, or whenever the guard cannot be sure
/// (restarts.json unreadable or not writable, no exe to start), it tells the user and stays down instead of flapping forever.
/// </summary>
internal static class CrashGuard
{
    private const int MaxRelaunches = 3;
    private static readonly TimeSpan LoopWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan HealthyAfter = TimeSpan.FromMinutes(10);
    private static readonly object Gate = new();
    private static int _handling;
    private static Timer? _healthyTimer;

    /// <summary>True once start-up has finished. Before that an ordinary exception must not be swallowed: it would leave a half-started process behind.</summary>
    public static bool StartupComplete { get; private set; }

    public static void Install(Application app)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);
            RelaunchAndExit();
        };

        app.DispatcherUnhandledException += OnDispatcherException;

        // Since .NET 4.5 an unobserved task exception no longer ends the process, so there is nothing to recover from:
        // log it and carry on (relaunching would only throw away a healthy app).
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>Call at the end of start-up. Also starts the timer that forgets earlier relaunches after 10 healthy minutes.</summary>
    public static void MarkStartupComplete()
    {
        StartupComplete = true;
        _healthyTimer = new Timer(_ => ResetHistory(), null, HealthyAfter, Timeout.InfiniteTimeSpan);
    }

    private static void ResetHistory()
    {
        if (Volatile.Read(ref _handling) == 1)
        {
            return; // going down right now
        }

        lock (Gate)
        {
            JsonFile.Delete(AppPaths.RestartsFile);
        }

        Log.Info("Healthy for 10 minutes; relaunch history cleared");
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        Log.Error("Unhandled UI exception", args.Exception);
        bool fatal = IsFatal(args.Exception);
        if (fatal && Installer.CanRelaunch)
        {
            args.Handled = true;
            RelaunchAndExit();
            return;
        }

        // An ordinary exception inside one event handler leaves a running app usable: keep running. During start-up it does
        // not (no tray icon, mutex still held), so it stays unhandled and the process ends.
        args.Handled = StartupComplete && !fatal;
    }

    private static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or AccessViolationException or System.Runtime.InteropServices.SEHException
            or InvalidProgramException or BadImageFormatException or InsufficientExecutionStackException;

    /// <summary>Starts a new copy of the app and ends this process, unless the crash-loop guard says to stay down.</summary>
    public static void RelaunchAndExit()
    {
        if (!Installer.CanRelaunch)
        {
            return; // debugger attached or dotnet host: let the normal crash behaviour happen
        }

        if (Interlocked.Exchange(ref _handling, 1) == 1)
        {
            return; // already on the way out (a second exception while handling the first)
        }

        try
        {
            string? stayDownReason = null;
            string? target = RelaunchTarget();
            if (target == null)
            {
                stayDownReason = "No executable to relaunch (neither the installed exe nor this one exists)";
            }
            else
            {
                lock (Gate)
                {
                    var now = DateTime.UtcNow;
                    List<DateTime>? history = File.Exists(AppPaths.RestartsFile)
                        ? JsonFile.Load<List<DateTime>>(AppPaths.RestartsFile)
                        : [];
                    if (history == null)
                    {
                        stayDownReason = "restarts.json is unreadable, so relaunches cannot be counted";
                    }
                    else
                    {
                        var recent = history.Where(t => now - t < LoopWindow).ToList();
                        if (recent.Count >= MaxRelaunches)
                        {
                            stayDownReason = $"Crash loop: {recent.Count} relaunches within {LoopWindow.TotalMinutes:0} minutes";
                        }
                        else
                        {
                            recent.Add(now);
                            if (!JsonFile.Save(AppPaths.RestartsFile, recent))
                            {
                                stayDownReason = "restarts.json could not be written, so relaunches cannot be counted";
                            }
                        }
                    }
                }
            }

            if (stayDownReason != null)
            {
                Log.Error($"{stayDownReason}; staying down");
                System.Windows.Forms.MessageBox.Show(
                    "Dropshelf stopped and could not restart itself safely. Start it again from the Start Menu; details are in dropshelf.log.",
                    "Dropshelf",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
                return;
            }

            Process.Start(new ProcessStartInfo(target!, Installer.RestartedArgument) { UseShellExecute = false });
            Log.Info("Relaunching after a fatal error");
        }
        catch (Exception ex)
        {
            Log.Error("Relaunch failed", ex);
        }
        finally
        {
            Environment.Exit(1);
        }
    }

    /// <summary>The installed exe if there is one (it may be newer than this process), else this exe, else null.</summary>
    private static string? RelaunchTarget()
    {
        if (File.Exists(Installer.InstalledExe))
        {
            return Installer.InstalledExe;
        }

        return Environment.ProcessPath is { Length: > 0 } exe && File.Exists(exe) ? exe : null;
    }
}
