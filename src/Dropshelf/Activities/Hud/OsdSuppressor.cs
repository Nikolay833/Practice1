using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Dropshelf.Interop;
using Dropshelf.Services;

namespace Dropshelf.Activities.Hud;

/// <summary>
/// Keeps the popups that the island replaces off the screen: Windows' volume / brightness / media box and a laptop maker's
/// on-screen display (Lenovo's FnHotkey Caps Lock / Num Lock popup, HP's and ASUS's OSD...).
/// <para>
/// <b>Instantly:</b> a WinEvent hook (<c>EVENT_OBJECT_SHOW</c> and <c>EVENT_OBJECT_UNCLOAKED</c>, out of context, so nothing is
/// injected anywhere) tells us the moment any window appears; a matching popup is hidden in the same instant, before it has
/// really been drawn. Windows 11 keeps its flyout window "shown" and only cloaks it between uses, so once hidden it stays
/// hidden; it is given back when the island goes away (fullscreen app, setting off, quit).
/// </para>
/// <para>
/// <b>Which windows:</b> Windows' own box is a small <c>XamlExplorerHostIslandWindow</c> of explorer.exe in the bottom-centre of
/// a monitor (Windows 11), or a <c>NativeHWNDHost</c> with a <c>DirectUIHWND</c> child (Windows 10). A maker's popup is a small
/// window of a process whose name says it is one ("...OSD...", "...Hotkey..."); it is only hidden right after something the
/// island shows (Caps Lock, volume, brightness), so popups the island has no answer for (keyboard light, Fn lock) stay.
/// </para>
/// <para>
/// <b>Fallback:</b> right after each volume / brightness / Caps Lock change, a short burst of window scans (every 16 ms for
/// about a second) catches anything the events missed. The first windows seen around such changes are written to the log,
/// so a popup that still gets through can be identified.
/// </para>
/// </summary>
internal sealed class OsdSuppressor : IDisposable
{
    private const int TickMs = 16, Ticks = 70;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_SHOWNA = 8;
    private const int MaxLoggedCandidates = 40;

    private readonly DispatcherTimer _timer;
    private readonly WinEventDelegate _callback;     // kept alive while the hook exists
    private readonly Dictionary<uint, bool> _vendorOsd = new();   // pid -> a maker's on-screen display?
    private readonly HashSet<IntPtr> _hiddenShell = new();
    private readonly HashSet<IntPtr> _osdHosts = new();      // explorer windows found to carry the volume / brightness popup
    private readonly HashSet<IntPtr> _loggedWindows = new();
    private readonly List<IntPtr> _hooks = new();
    private readonly List<(IntPtr Hwnd, long At)> _recent = new();   // windows shown in the last moment, for a late burst
    private int _ticksLeft;
    private uint _shellPid;
    private long _burstUntil;
    private bool _enabled;
    private bool _islandVisible = true;
    private int _logged;

    public OsdSuppressor()
    {
        _callback = OnWinEvent;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _timer.Tick += (_, _) =>
        {
            Scan();
            if (--_ticksLeft <= 0)
            {
                _timer.Stop();
            }
        };
    }

    /// <summary>Decides whether the island will show a HUD (it is not slid away for a fullscreen app).</summary>
    public Func<IPillActivity, bool>? Gate { get; set; }

    /// <summary>
    /// Asked when a maker's popup appears outside a burst: did Caps Lock just change? (It reads the key state right away, so
    /// the island's Caps Lock pill and the hiding happen at the same moment.)
    /// </summary>
    public Func<bool>? CapsLockJustChanged { get; set; }

    /// <summary>The "Hide the Windows popups" setting: installs or removes the hook.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            if (value)
            {
                InstallHooks();
            }
            else
            {
                RemoveHooks();
                RestoreShell();
            }
        }
    }

    /// <summary>The island is on screen (false while a fullscreen app hides it): Windows' own popup is given back meanwhile.</summary>
    public void SetIslandVisible(bool visible)
    {
        _islandVisible = visible;
        if (!visible)
        {
            RestoreShell();
        }
    }

    /// <summary>Something the island shows just changed: hide whatever popup comes with it (scan burst + maker popups).</summary>
    public void Suppress(IPillActivity hud)
    {
        if (!_enabled || (Gate != null && !Gate(hud)))
        {
            return;
        }

        _burstUntil = Environment.TickCount64 + 1500;
        _ticksLeft = Ticks;
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }

        // The popup may have appeared a moment before we heard of the change: look at what was just shown, with the burst's rules.
        long now = Environment.TickCount64;
        foreach ((IntPtr hwnd, long at) in _recent.ToArray())
        {
            if (now - at < 700 && NativeMethods.IsWindow(hwnd) && NativeMethods.IsWindowVisible(hwnd))
            {
                Consider(hwnd, fromEvent: true);
            }
        }

        _recent.Clear();
        Scan();
    }

    // ---- Events -------------------------------------------------------------------------------------

    private void InstallHooks()
    {
        foreach (uint evt in new[] { EVENT_OBJECT_SHOW, EVENT_OBJECT_UNCLOAKED })
        {
            IntPtr hook = SetWinEventHook(evt, evt, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (hook != IntPtr.Zero)
            {
                _hooks.Add(hook);
            }
            else
            {
                Log.Info($"Popup hook {evt:X} could not be installed");
            }
        }
    }

    private void RemoveHooks()
    {
        foreach (IntPtr hook in _hooks)
        {
            UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero || !_enabled || !_islandVisible)
        {
            return;
        }

        try
        {
            if (_recent.Count >= 32)
            {
                _recent.RemoveAt(0);
            }

            _recent.Add((hwnd, Environment.TickCount64));

            // A volume key held right now: the popup is on its way, even before the volume change reaches us.
            if (VolumeKeyDown())
            {
                _burstUntil = Math.Max(_burstUntil, Environment.TickCount64 + 1500);
            }

            Consider(hwnd, fromEvent: true);
        }
        catch (Exception ex)
        {
            Log.Error("Hiding a popup failed", ex);
        }
    }

    // ---- Scanning -----------------------------------------------------------------------------------

    private void Scan()
    {
        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (NativeMethods.IsWindowVisible(hwnd))
                {
                    Consider(hwnd, fromEvent: false);
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("Scanning for popups failed", ex);
            _timer.Stop();
        }
    }

    /// <summary>Hides <paramref name="hwnd"/> if it is one of the popups the island replaces.</summary>
    private void Consider(IntPtr hwnd, bool fromEvent)
    {
        // Windows 11 shows the flyout's inner content window first (a DesktopWindowContentBridge, while the flyout still sits
        // just below the bottom edge before it slides up): judge, and hide, the top-level window it belongs to.
        IntPtr root = NativeMethods.GetAncestor(hwnd, 2 /* GA_ROOT */);
        string childClass = string.Empty;
        if (root != IntPtr.Zero && root != hwnd)
        {
            childClass = ClassOf(hwnd);
            hwnd = root;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId)
        {
            return;
        }

        bool inBurst = Environment.TickCount64 < _burstUntil;
        if (pid == ShellPid())
        {
            string cls = ClassOf(hwnd);
            bool xamlHost = cls.StartsWith("XamlExplorerHostIslandWindow", StringComparison.Ordinal)
                            || cls == "Windows.UI.Composition.DesktopWindowContentBridge";

            // A window already known to carry the popup: hide it the moment it shows, whatever else is going on.
            bool known = _osdHosts.Contains(hwnd);

            // On some PCs (Windows 11 24H2) the popup is drawn inside a transparent explorer window that covers the whole
            // screen; it is recognised when it appears right after a volume / brightness change (and is not Alt+Tab, Task
            // View, Snap and the like, which have a title), then remembered.
            bool fullScreenHost = xamlHost && inBurst && IsFullScreenHost(hwnd);
            if (known || (xamlHost && IsShellFlyout(hwnd, strict: !inBurst)) || fullScreenHost)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
                _hiddenShell.Add(hwnd);
                if (fullScreenHost && _osdHosts.Add(hwnd))
                {
                    Log.Info($"Popup host found: '{cls}' '{TitleOf(hwnd)}'");
                }

                return;
            }

            if (cls == "NativeHWNDHost" && !NativeMethods.IsIconic(hwnd) && Size(hwnd, out _, out _)
                && NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "DirectUIHWND", null) != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE); // Windows 10: hiding it outright can stop it coming back
                return;
            }

            if (inBurst && cls != "Shell_TrayWnd")
            {
                LogCandidate(hwnd, pid, childClass.Length > 0 ? $"{cls} > {childClass}" : cls);
            }

            return;
        }

        if (IsVendorOsd(pid))
        {
            // A maker's popup: hide it when it belongs to something the island shows right now.
            bool related = inBurst || (CapsLockJustChanged?.Invoke() ?? false);
            if (related && Size(hwnd, out _, out _))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
            }

            return;
        }

        if (inBurst && fromEvent)
        {
            LogCandidate(hwnd, pid, ClassOf(hwnd));
        }
    }

    private static bool VolumeKeyDown() =>
        ((NativeMethods.GetAsyncKeyState(0xAD) | NativeMethods.GetAsyncKeyState(0xAE) | NativeMethods.GetAsyncKeyState(0xAF)) & 0x8000) != 0;

    /// <summary>
    /// A transparent explorer XAML window that covers the whole monitor and has no title (Alt+Tab, Task View and Snap Assist
    /// have one), which is how Windows 11 24H2 hosts the volume and brightness popup on some PCs.
    /// </summary>
    private static bool IsFullScreenHost(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r))
        {
            return false;
        }

        MonitorInfo monitor = MonitorInfo.FromHandle(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST));
        bool covers = r.Width >= monitor.Width * 0.9 && r.Height >= monitor.Height * 0.9;
        return covers && TitleOf(hwnd).Length == 0;
    }

    private static string TitleOf(IntPtr hwnd)
    {
        var title = new StringBuilder(128);
        return GetWindowText(hwnd, title, title.Capacity) > 0 ? title.ToString() : string.Empty;
    }

    /// <summary>
    /// Windows 11's flyout: a small explorer window, horizontally centred near the bottom of a monitor. Outside a burst (no
    /// change of ours seen yet) only a really flyout-shaped one counts, so taskbar previews or Alt+Tab are never touched.
    /// </summary>
    private static bool IsShellFlyout(IntPtr hwnd, bool strict)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r) || r.Width < 40 || r.Height < 20)
        {
            return false;
        }

        MonitorInfo monitor = MonitorInfo.FromHandle(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST));

        double centre = (r.Left + r.Right) / 2.0;
        double monitorCentre = monitor.Left + monitor.Width / 2.0;
        double w = r.Width / monitor.Scale, h = r.Height / monitor.Scale;
        bool centred = Math.Abs(centre - monitorCentre) < monitor.Width * 0.1;
        // Either the small box itself, or the strip it slides up in: as wide as the screen, short, at (or below) the bottom edge.
        bool strip = r.Width >= monitor.Width * 0.9 && h <= 160 && r.Top > monitor.Top + monitor.Height * 0.75;
        return strict
            ? strip || (centred && w <= 420 && h <= 140 && r.Top > monitor.Top + monitor.Height * 0.7)
            : strip || (centred && h <= 420 && r.Top > monitor.Top + monitor.Height * 0.5);
    }

    /// <summary>Small enough to be a popup (40-700 x 20-320 DIPs).</summary>
    private static bool Size(IntPtr hwnd, out NativeMethods.RECT r, out MonitorInfo monitor)
    {
        monitor = default;
        if (!NativeMethods.GetWindowRect(hwnd, out r))
        {
            return false;
        }

        monitor = MonitorInfo.FromHandle(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST));
        double w = r.Width / monitor.Scale, h = r.Height / monitor.Scale;
        return w >= 40 && h >= 20 && w <= 700 && h <= 320;
    }

    private uint ShellPid()
    {
        if (_shellPid == 0)
        {
            IntPtr shell = NativeMethods.GetShellWindow();
            if (shell != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(shell, out _shellPid);
            }
        }

        return _shellPid;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        return NativeMethods.GetClassName(hwnd, name, name.Capacity) == 0 ? string.Empty : name.ToString();
    }

    /// <summary>Is <paramref name="pid"/> a laptop maker's on-screen display (by its process name)? Cached per process.</summary>
    private bool IsVendorOsd(uint pid)
    {
        if (_vendorOsd.TryGetValue(pid, out bool known))
        {
            return known;
        }

        bool osd = false;
        string name = ProcessName(pid);
        foreach (string part in new[] { "osd", "hotkey", "capslk", "numlk" })
        {
            osd |= name.Contains(part, StringComparison.OrdinalIgnoreCase);
        }

        if (_vendorOsd.Count > 512)
        {
            _vendorOsd.Clear();
        }

        _vendorOsd[pid] = osd;
        return osd;
    }

    private static string ProcessName(uint pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Gives back Windows' flyout (only while it is cloaked, so nothing stale ever appears on screen).</summary>
    private void RestoreShell()
    {
        foreach (IntPtr hwnd in _hiddenShell)
        {
            // A full-screen popup host is transparent, so showing it again shows nothing until Windows draws a popup in it.
            bool cloaked = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int value, sizeof(int)) == 0 && value != 0;
            if (NativeMethods.IsWindow(hwnd) && !NativeMethods.IsWindowVisible(hwnd) && (cloaked || _osdHosts.Contains(hwnd)))
            {
                NativeMethods.ShowWindow(hwnd, SW_SHOWNA);
            }
        }

        _hiddenShell.Clear();
    }

    /// <summary>Notes a window that appeared right after a volume / brightness / Caps Lock change, to identify stray popups.</summary>
    private void LogCandidate(IntPtr hwnd, uint pid, string cls)
    {
        if (_logged >= MaxLoggedCandidates || !_loggedWindows.Add(hwnd) || !NativeMethods.GetWindowRect(hwnd, out var r))
        {
            return;
        }

        _logged++;
        Log.Info($"Popup candidate after a change: class '{cls}', title '{TitleOf(hwnd)}', process '{ProcessName(pid)}', " +
                 $"{r.Width}x{r.Height} at {r.Left},{r.Top}");
    }

    public void Dispose()
    {
        _timer.Stop();
        RemoveHooks();
        RestoreShell();
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
