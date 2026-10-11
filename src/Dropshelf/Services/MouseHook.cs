using System;
using System.Runtime.InteropServices;
using Dropshelf.Interop;

namespace Dropshelf.Services;

/// <summary>
/// Global low-level mouse hook (WH_MOUSE_LL). Must be created on a thread with a message loop (the UI thread).
/// The callback does almost no work: it forwards left-button and move events to listeners.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    // Keep a reference so the delegate is never garbage collected while the hook is installed.
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private IntPtr _hook;

    public MouseHook()
    {
        _proc = HookCallback;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    public event Action<int, int>? LeftDown;
    public event Action<int, int>? LeftUp;
    public event Action<int, int>? Move;

    public void Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Error($"SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()})");
        }
    }

    public void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                int msg = wParam.ToInt32();
                if (msg is NativeMethods.WM_MOUSEMOVE or NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP)
                {
                    var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    switch (msg)
                    {
                        case NativeMethods.WM_MOUSEMOVE: Move?.Invoke(data.pt.X, data.pt.Y); break;
                        case NativeMethods.WM_LBUTTONDOWN: LeftDown?.Invoke(data.pt.X, data.pt.Y); break;
                        case NativeMethods.WM_LBUTTONUP: LeftUp?.Invoke(data.pt.X, data.pt.Y); break;
                    }
                }
            }
            catch (Exception ex)
            {
                // Never let an exception escape into the native hook chain.
                Log.Error("Mouse hook callback failed", ex);
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
