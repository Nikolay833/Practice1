using System;
using System.Windows.Interop;
using Dropshelf.Interop;

namespace Dropshelf.Services;

/// <summary>
/// Hidden message-only window used for WM_HOTKEY and WM_CLIPBOARDUPDATE. Not tied to any visible WPF window,
/// so hotkeys and clipboard history keep working while all windows are hidden.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private static readonly IntPtr HwndMessage = new(-3);
    private readonly HwndSource _source;

    public MessageWindow()
    {
        var parameters = new HwndSourceParameters("DropshelfMessageWindow")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public IntPtr Handle => _source.Handle;

    public event Action<int>? HotkeyPressed;
    public event Action? ClipboardUpdated;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_HOTKEY:
                HotkeyPressed?.Invoke(wParam.ToInt32());
                handled = true;
                break;
            case NativeMethods.WM_CLIPBOARDUPDATE:
                ClipboardUpdated?.Invoke();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
