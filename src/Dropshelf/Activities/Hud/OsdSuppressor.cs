using System;
using System.Text;
using System.Windows.Threading;
using Dropshelf.Interop;
using Dropshelf.Services;

namespace Dropshelf.Activities.Hud;

/// <summary>
/// Best-effort removal of the native Windows volume / brightness popup, which Dropshelf replaces with its own HUD. Right
/// after a volume or brightness change the popup is looked up and hidden, polling for about a second because it is created
/// and shown a moment after the change. Two variants are handled:
/// <list type="bullet">
/// <item>Windows 10 / early Windows 11: a small topmost <c>NativeHWNDHost</c> window with a <c>DirectUIHWND</c> child in
/// explorer.exe; it is minimized (hiding it outright can stop it from coming back).</item>
/// <item>Windows 11 22H2+: a small topmost <c>XamlExplorerHostIslandWindow</c> in explorer.exe, centred near the bottom of a
/// monitor; it is hidden with SW_HIDE.</item>
/// </list>
/// Nothing is changed when the windows are not found, so then both popups simply show. Disabling the setting stops the
/// hunting; the shell shows the popup again on its own at the next change.
/// </summary>
internal sealed class OsdSuppressor
{
    private const int TickMs = 25, Ticks = 40;

    private readonly DispatcherTimer _timer;
    private int _ticksLeft;
    private uint _shellPid;

    public OsdSuppressor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _timer.Tick += (_, _) =>
        {
            TryHide();
            if (--_ticksLeft <= 0)
            {
                _timer.Stop();
            }
        };
    }

    /// <summary>
    /// Decides whether the HUD of an activity will really appear (not while the shelf is open or a fullscreen app hides the
    /// pill). The native popup is only hidden when it does, so the user never ends up with no feedback at all.
    /// </summary>
    public Func<IPillActivity, bool>? Gate { get; set; }

    /// <summary>Starts (or extends) a burst of hide attempts, unless <paramref name="hud"/> will not be shown.</summary>
    public void Suppress(IPillActivity hud)
    {
        if (Gate != null && !Gate(hud))
        {
            return;
        }

        _ticksLeft = Ticks;
        if (!_timer.IsEnabled)
        {
            _shellPid = 0;
            _timer.Start();
        }
    }

    private void TryHide()
    {
        try
        {
            if (_shellPid == 0)
            {
                IntPtr shell = NativeMethods.GetShellWindow();
                if (shell == IntPtr.Zero)
                {
                    return;
                }

                NativeMethods.GetWindowThreadProcessId(shell, out _shellPid);
            }

            NativeMethods.EnumWindows(Visit, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("Hiding the Windows volume popup failed", ex);
            _timer.Stop();
        }
    }

    private bool Visit(IntPtr hwnd, IntPtr lParam)
    {
        if (!NativeMethods.IsWindowVisible(hwnd))
        {
            return true;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != _shellPid)
        {
            return true;
        }

        var name = new StringBuilder(64);
        if (NativeMethods.GetClassName(hwnd, name, name.Capacity) == 0)
        {
            return true;
        }

        string cls = name.ToString();
        if (cls == "NativeHWNDHost")
        {
            if (!NativeMethods.IsIconic(hwnd) && LooksLikeFlyout(hwnd, requireCentred: false)
                && NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "DirectUIHWND", null) != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
            }
        }
        else if (cls == "XamlExplorerHostIslandWindow")
        {
            if (LooksLikeFlyout(hwnd, requireCentred: true))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
            }
        }

        return true;
    }

    /// <summary>The popups are small and topmost; the Windows 11 one also sits horizontally centred in the lower part of a monitor.</summary>
    private static bool LooksLikeFlyout(IntPtr hwnd, bool requireCentred)
    {
        if ((NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) == 0
            || !NativeMethods.GetWindowRect(hwnd, out var r))
        {
            return false;
        }

        MonitorInfo monitor = MonitorInfo.FromHandle(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST));
        double w = r.Width / monitor.Scale, h = r.Height / monitor.Scale;
        if (w < 40 || h < 20 || w > 600 || h > 260)
        {
            return false;
        }

        if (!requireCentred)
        {
            return true;
        }

        double centre = (r.Left + r.Right) / 2.0;
        double monitorCentre = monitor.Left + monitor.Width / 2.0;
        return Math.Abs(centre - monitorCentre) < monitor.Width * 0.08 && r.Top > monitor.Top + monitor.Height * 0.5 && h <= 160;
    }
}
