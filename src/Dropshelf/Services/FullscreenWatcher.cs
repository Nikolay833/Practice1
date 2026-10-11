using System;
using System.Text;
using System.Windows.Threading;
using Dropshelf.Interop;

namespace Dropshelf.Services;

/// <summary>
/// Tells whether a fullscreen app (game, video, presentation) is in front on the monitor the pill lives on. Polls once a
/// second, only while "Hide in fullscreen apps" is on. A window counts when it covers the whole primary monitor and
/// either Windows' own <c>SHQueryUserNotificationState</c> says busy / D3D fullscreen / presentation mode, or the window has
/// no title bar (a maximized normal window, even with an auto-hidden taskbar, has one). Desktop and shell windows
/// (Progman, WorkerW, anything of explorer.exe) and Dropshelf's own windows never count.
/// </summary>
internal sealed class FullscreenWatcher : IDisposable
{
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _timer;
    private uint _shellPid;

    public FullscreenWatcher(SettingsService settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Update();
        ApplySettings();
    }

    public bool IsFullscreen { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="IsFullscreen"/> changes.</summary>
    public event Action<bool>? Changed;

    public void ApplySettings()
    {
        if (_settings.Current.HideInFullscreen)
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
                Update();
            }
        }
        else
        {
            _timer.Stop();
            Set(false);
        }
    }

    private void Update()
    {
        bool value;
        try
        {
            value = Detect();
        }
        catch (Exception ex)
        {
            Log.Error("Fullscreen detection failed", ex);
            value = false;
        }

        Set(value);
    }

    private void Set(bool value)
    {
        if (IsFullscreen != value)
        {
            IsFullscreen = value;
            Changed?.Invoke(value);
        }
    }

    private bool Detect()
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || WindowHelper.IsOwnWindow(fg) || !NativeMethods.IsWindowVisible(fg) || NativeMethods.IsIconic(fg))
        {
            return false;
        }

        if (_shellPid == 0)
        {
            IntPtr shell = NativeMethods.GetShellWindow();
            if (shell != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(shell, out _shellPid);
            }
        }

        NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
        if (_shellPid != 0 && pid == _shellPid)
        {
            return false; // desktop, taskbar, Start, task view...
        }

        var cls = new StringBuilder(64);
        NativeMethods.GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return false;
        }

        MonitorInfo monitor = MonitorInfo.Primary();
        if (!NativeMethods.GetWindowRect(fg, out var r)
            || r.Left > monitor.Left || r.Top > monitor.Top || r.Right < monitor.Right || r.Bottom < monitor.Bottom)
        {
            return false; // does not cover the pill's monitor
        }

        bool shellSaysBusy = false;
        if (NativeMethods.SHQueryUserNotificationState(out int state) == 0)
        {
            shellSaysBusy = state is NativeMethods.QUNS_BUSY or NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN or NativeMethods.QUNS_PRESENTATION_MODE;
        }

        bool hasTitleBar = (NativeMethods.GetWindowLong(fg, NativeMethods.GWL_STYLE) & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION;
        return shellSaysBusy || !hasTitleBar;
    }

    public void Dispose() => _timer.Stop();
}
