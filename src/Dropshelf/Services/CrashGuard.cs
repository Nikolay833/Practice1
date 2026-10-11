using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Dropshelf.Services;

/// <summary>
/// Keeps the app running: a fatal error is logged and Dropshelf starts a fresh copy of itself (<c>--restarted</c>) before
/// exiting. A crash-loop guard allows at most 3 relaunches within 2 minutes (timestamps in restarts.json); after that it
/// tells the user and stays down instead of flapping forever.
/// </summary>
internal static class CrashGuard
{
    private const int MaxRelaunches = 3;
    private static readonly TimeSpan LoopWindow = TimeSpan.FromMinutes(2);
    private static int _handling;

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

        // An ordinary exception inside one event handler leaves the app usable (this is what it always did): keep running.
        args.Handled = !fatal;
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
            var now = DateTime.UtcNow;
            var recent = (JsonFile.Load<List<DateTime>>(AppPaths.RestartsFile) ?? [])
                .Where(t => now - t < LoopWindow && t <= now)
                .ToList();

            if (recent.Count >= MaxRelaunches)
            {
                Log.Error($"Crash loop: {recent.Count} relaunches within {LoopWindow.TotalMinutes:0} minutes; staying down");
                System.Windows.Forms.MessageBox.Show(
                    "Dropshelf stopped because it kept crashing. Start it again from the Start Menu; details are in dropshelf.log.",
                    "Dropshelf",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
                Environment.Exit(1);
                return;
            }

            recent.Add(now);
            JsonFile.Save(AppPaths.RestartsFile, recent);

            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, Installer.RestartedArgument) { UseShellExecute = false });
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
}
