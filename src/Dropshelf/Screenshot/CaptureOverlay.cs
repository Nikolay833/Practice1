using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Dropshelf.Interop;
using Dropshelf.Services;

namespace Dropshelf.Screenshot;

/// <summary>
/// Opens one <see cref="CaptureWindow"/> per monitor and completes with the chosen rectangle in physical screen pixels, or null
/// when cancelled. The overlay windows are closed (and the compositor given a moment to repaint) before the task completes, so
/// the capture never contains the dimming.
/// </summary>
internal sealed class CaptureOverlay
{
    private readonly List<CaptureWindow> _windows = new();
    private readonly TaskCompletionSource<Rectangle?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True while an overlay is open (a second hotkey press does nothing then).</summary>
    public static bool IsOpen { get; private set; }

    public static async Task<Rectangle?> PickAsync()
    {
        if (IsOpen)
        {
            return null;
        }

        IsOpen = true;
        var overlay = new CaptureOverlay();
        try
        {
            overlay.Open();
            Rectangle? picked = await overlay._result.Task;
            overlay.CloseAll();
            if (picked != null)
            {
                NativeMethods.DwmFlush();
                await Task.Delay(140); // DWM needs a frame or two to drop the closed windows
            }

            return picked;
        }
        finally
        {
            overlay.CloseAll();
            IsOpen = false;
        }
    }

    private void Open()
    {
        foreach (Screen screen in Screen.AllScreens)
        {
            Rectangle b = screen.Bounds;
            MonitorInfo monitor = MonitorInfo.FromPoint(b.Left + b.Width / 2, b.Top + b.Height / 2);
            var window = new CaptureWindow(monitor);
            window.RegionPicked += (w, rect) => Finish(w.ToScreenPixels(rect), w);
            window.FullScreenPicked += w => Finish(MonitorBounds(w.Monitor), w);
            window.FullScreenKey += FullScreenUnderCursor;
            window.ConfirmRequested += w => Finish(w.ToScreenPixels(w.Selection), w);
            window.Cancelled += () => _result.TrySetResult(null);
            _windows.Add(window);
        }

        if (_windows.Count == 0)
        {
            _result.TrySetResult(null);
            return;
        }

        foreach (CaptureWindow w in _windows)
        {
            w.Show();
            w.ApplyBounds();
        }

        // Keyboard focus goes to the overlay under the pointer.
        MonitorInfo under = MonitorInfo.UnderCursor();
        CaptureWindow first = _windows.Find(w => w.Monitor.Left == under.Left && w.Monitor.Top == under.Top) ?? _windows[0];
        first.Activate();
        first.Focus();
        WindowHelper.ForceForeground(new System.Windows.Interop.WindowInteropHelper(first).Handle);
    }

    private void FullScreenUnderCursor()
    {
        MonitorInfo under = MonitorInfo.UnderCursor();
        _result.TrySetResult(MonitorBounds(under));
    }

    private void Finish(Rectangle pixels, CaptureWindow source)
    {
        Rectangle bounds = MonitorBounds(source.Monitor);
        pixels.Intersect(bounds);
        _result.TrySetResult(pixels.Width >= 1 && pixels.Height >= 1 ? pixels : null);
    }

    private static Rectangle MonitorBounds(MonitorInfo m) => new(m.Left, m.Top, m.Width, m.Height);

    private void CloseAll()
    {
        foreach (CaptureWindow w in _windows)
        {
            try
            {
                w.Close();
            }
            catch (InvalidOperationException ex)
            {
                Log.Error("Closing a capture window failed", ex);
            }
        }

        _windows.Clear();
    }
}
