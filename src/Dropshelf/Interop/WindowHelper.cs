using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Dropshelf.Interop;

/// <summary>Pixel-accurate information about a monitor.</summary>
internal readonly record struct MonitorInfo(int Left, int Top, int Width, int Height, int WorkLeft, int WorkTop, int WorkWidth, int WorkHeight, double Scale)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public int WorkRight => WorkLeft + WorkWidth;
    public int WorkBottom => WorkTop + WorkHeight;

    public static MonitorInfo FromHandle(IntPtr monitor)
    {
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref mi))
        {
            // Last-resort fallback: WPF's idea of the primary screen.
            return new MonitorInfo(0, 0, (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight,
                0, 0, (int)SystemParameters.WorkArea.Width, (int)SystemParameters.WorkArea.Height, 1.0);
        }

        double scale = 1.0;
        try
        {
            if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                scale = dpiX / 96.0;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Pre Windows 8.1: fall through with 100 %.
        }

        return new MonitorInfo(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Width, mi.rcMonitor.Height,
            mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Width, mi.rcWork.Height, scale);
    }

    public static MonitorInfo Primary() =>
        FromHandle(NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = 0, Y = 0 }, NativeMethods.MONITOR_DEFAULTTOPRIMARY));

    public static MonitorInfo FromPoint(int x, int y) =>
        FromHandle(NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y }, NativeMethods.MONITOR_DEFAULTTONEAREST));

    public static MonitorInfo UnderCursor()
    {
        NativeMethods.GetCursorPos(out var p);
        return FromPoint(p.X, p.Y);
    }
}

/// <summary>Window styling and placement helpers built on Win32.</summary>
internal static class WindowHelper
{
    /// <summary>Adds extended styles (e.g. tool window / no-activate) to an existing window.</summary>
    public static void AddExStyle(IntPtr hwnd, int style)
    {
        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex | style);
    }

    /// <summary>Turns WS_EX_TRANSPARENT on or off so mouse input passes through (or is received by) the window.</summary>
    public static void SetClickThrough(IntPtr hwnd, bool clickThrough)
    {
        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        int updated = clickThrough ? ex | NativeMethods.WS_EX_TRANSPARENT : ex & ~NativeMethods.WS_EX_TRANSPARENT;
        if (updated != ex)
        {
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, updated);
        }
    }

    /// <summary>Hides a window from Alt+Tab and (optionally) stops it from taking focus when clicked.</summary>
    public static void MakeToolWindow(IntPtr hwnd, bool noActivate)
    {
        int style = NativeMethods.WS_EX_TOOLWINDOW;
        if (noActivate)
        {
            style |= NativeMethods.WS_EX_NOACTIVATE;
        }

        AddExStyle(hwnd, style);
    }

    /// <summary>Moves/resizes a window using physical pixels and keeps it topmost without activating it.</summary>
    public static void SetBoundsTopmost(IntPtr hwnd, int x, int y, int width, int height)
    {
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, Math.Max(1, width), Math.Max(1, height),
            NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Places <paramref name="window"/> at a pixel position using a size given in DIPs at the given scale.</summary>
    public static void Place(Window window, int xPx, int yPx, double widthDip, double heightDip, double scale)
    {
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        SetBoundsTopmost(hwnd, xPx, yPx, (int)Math.Round(widthDip * scale), (int)Math.Round(heightDip * scale));
    }

    /// <summary>Centers a window (size in DIPs) on the work area of <paramref name="monitor"/>.</summary>
    public static void CenterOnMonitor(Window window, MonitorInfo monitor, double widthDip, double heightDip)
    {
        int w = (int)Math.Round(widthDip * monitor.Scale);
        int h = (int)Math.Round(heightDip * monitor.Scale);
        int x = monitor.WorkLeft + (monitor.WorkWidth - w) / 2;
        int y = monitor.WorkTop + (monitor.WorkHeight - h) / 2;
        Place(window, x, y, widthDip, heightDip, monitor.Scale);
    }

    /// <summary>
    /// Brings a window to the foreground even when Windows' foreground lock would normally refuse
    /// (AttachThreadInput trick). Returns whether the window is foreground afterwards.
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == hwnd)
        {
            return true;
        }

        uint fgThread = fg == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(fg, out _);
        uint thisThread = NativeMethods.GetCurrentThreadId();
        bool attached = false;
        try
        {
            if (fgThread != 0 && fgThread != thisThread)
            {
                attached = NativeMethods.AttachThreadInput(thisThread, fgThread, true);
            }

            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(thisThread, fgThread, false);
            }
        }

        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    /// <summary>True when <paramref name="hwnd"/> belongs to this process.</summary>
    public static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>Is the physical-pixel point inside the window's bounds?</summary>
    public static bool ContainsCursor(IntPtr hwnd, int margin = 0)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r) || !NativeMethods.GetCursorPos(out var p))
        {
            return false;
        }

        return p.X >= r.Left - margin && p.X < r.Right + margin && p.Y >= r.Top - margin && p.Y < r.Bottom + margin;
    }
}

/// <summary>Acrylic blur and DWM tweaks for borderless windows. Every call fails soft.</summary>
internal static class GlassHelper
{
    /// <summary>
    /// Enables acrylic blur-behind with the given AABBGGRR tint. Returns false when the OS rejected it
    /// (older Windows, remote session, transparency disabled...) so the caller can use an opaque fallback.
    /// </summary>
    public static bool TryEnableAcrylic(IntPtr hwnd, uint tintAabbggrr)
    {
        IntPtr data = IntPtr.Zero;
        try
        {
            var accent = new NativeMethods.AccentPolicy
            {
                AccentState = NativeMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2,
                GradientColor = tintAabbggrr,
            };

            int size = Marshal.SizeOf<NativeMethods.AccentPolicy>();
            data = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(accent, data, false);

            var attr = new NativeMethods.WindowCompositionAttributeData
            {
                Attribute = NativeMethods.WCA_ACCENT_POLICY,
                Data = data,
                SizeOfData = size,
            };

            return NativeMethods.SetWindowCompositionAttribute(hwnd, ref attr) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(data);
            }
        }
    }

    /// <summary>Lets per-pixel alpha of a non-layered window show the desktop (needed for acrylic and the translucent fallback).</summary>
    public static void ExtendFrameIntoClient(IntPtr hwnd)
    {
        try
        {
            var m = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref m);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Ignore: window just stays opaque.
        }
    }

    /// <summary>Windows 11 rounded corners for a non-layered window. No-op elsewhere.</summary>
    public static void TryRoundCorners(IntPtr hwnd)
    {
        try
        {
            int pref = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Ignore.
        }
    }
}
