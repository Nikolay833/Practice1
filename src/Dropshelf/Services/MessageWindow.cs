using System;
using System.Runtime.InteropServices;
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

    /// <summary>The display brightness changed (a power-setting notification, delivered the moment it happens).</summary>
    public event Action? BrightnessChanged;

    // GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS and GUID_DEVICE_POWER_POLICY_VIDEO_BRIGHTNESS.
    private static readonly Guid[] BrightnessSettings =
    {
        new("8ffee2c6-2d01-46be-adb9-398addc5b4ff"),
        new("aded5e82-b909-4619-9949-f5d71dac0bcb"),
    };

    private readonly System.Collections.Generic.List<IntPtr> _powerNotifications = new();
    private readonly System.Collections.Generic.HashSet<Guid> _primed = new(); // each setting reports its value once on registration

    /// <summary>Asks Windows for brightness change notifications (the first one, sent right away, is ignored).</summary>
    public void WatchBrightness()
    {
        foreach (Guid setting in BrightnessSettings)
        {
            Guid id = setting;
            IntPtr handle = RegisterPowerSettingNotification(Handle, ref id, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
            if (handle != IntPtr.Zero)
            {
                _powerNotifications.Add(handle);
            }
        }
    }

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
            case 0x0218 when wParam.ToInt64() == 0x8013 && lParam != IntPtr.Zero: // WM_POWERBROADCAST, PBT_POWERSETTINGCHANGE
                Guid changed = Marshal.PtrToStructure<Guid>(lParam);
                if (Array.IndexOf(BrightnessSettings, changed) >= 0 && !_primed.Add(changed))
                {
                    BrightnessChanged?.Invoke();
                }

                break;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (IntPtr handle in _powerNotifications)
        {
            UnregisterPowerSettingNotification(handle);
        }

        _powerNotifications.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
