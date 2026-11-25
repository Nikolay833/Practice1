using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;
using Dropshelf.Interop;

namespace Dropshelf.Services;

/// <summary>
/// "Type into the hidden terminal": while running, every key you press goes to Dropshelf instead of the window in front,
/// whatever that window is, so no focus or foreground change is needed. Modifier keys pass through (Windows keeps their
/// state), and so do Alt and Windows-key shortcuts (Alt+Tab, Win+D), so the system stays usable. It runs a low-level
/// keyboard hook on its own thread only while it is on; off, it costs nothing.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly NativeMethods.LowLevelMouseProc _proc; // same signature; kept alive while hooked
    private readonly bool[] _swallowed = new bool[256];      // hook thread only: keys whose press we took
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;

    public KeyboardHook() => _proc = HookCallback;

    /// <summary>A key for the terminal: the key, the text it types (null for keys that type nothing), and the modifiers.</summary>
    public event Action<Key, string?, bool, bool, bool>? KeyPressed;

    public bool IsRunning => _thread != null;

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        Array.Clear(_swallowed);
        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Dropshelf.KeyboardHook" };
        _thread.Start();
        ready.Wait(2000);
    }

    public void Stop()
    {
        Thread? thread = _thread;
        if (thread == null)
        {
            return;
        }

        _thread = null;
        if (_threadId != 0)
        {
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        }

        thread.Join(1000);
        _threadId = 0;
    }

    public void Dispose() => Stop();

    private void Run(ManualResetEventSlim ready)
    {
        try
        {
            NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);
            _threadId = NativeMethods.GetCurrentThreadId();
            _hook = NativeMethods.SetWindowsHookEx(WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Log.Error($"Keyboard hook could not be set (error {Marshal.GetLastWin32Error()})");
                return;
            }

            ready.Set();
            while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
        }
        catch (Exception ex)
        {
            Log.Error("Keyboard hook thread failed", ex);
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }

            ready.Set();
        }
    }

    private static bool Down(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsModifier(uint vk) =>
        vk is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C or 0x14 or 0x90 or 0x91;

    // Runs on the hook thread; must stay quick (Windows drops a hook that takes too long).
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        try
        {
            int msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            uint vk = data.vkCode & 0xFF;
            if ((data.flags & LLKHF_INJECTED) != 0 || IsModifier(vk))
            {
                return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                if (_swallowed[vk])
                {
                    _swallowed[vk] = false;
                    return (IntPtr)1; // its press went to the terminal, so the release does too
                }

                return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            if (msg is not (WM_KEYDOWN or WM_SYSKEYDOWN))
            {
                return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            bool shift = Down(0x10), ctrl = Down(0x11), alt = Down(0x12), win = Down(0x5B) || Down(0x5C);
            if (win || (alt && !ctrl))
            {
                return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam); // Alt+Tab, Win+D and friends still work
            }

            // The character this key types with the current layout, Shift and Caps Lock (AltGr = Ctrl+Alt included).
            string? text = null;
            if (!ctrl || alt)
            {
                var state = new byte[256];
                if (shift)
                {
                    state[0x10] = 0x80;
                }

                if (ctrl)
                {
                    state[0x11] = 0x80;
                }

                if (alt)
                {
                    state[0x12] = 0x80;
                }

                if ((NativeMethods.GetKeyState(0x14) & 1) != 0)
                {
                    state[0x14] = 0x01;
                }

                IntPtr fg = NativeMethods.GetForegroundWindow();
                uint thread = fg == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(fg, out _);
                var buffer = new StringBuilder(8);
                int n = ToUnicodeEx(vk, data.scanCode, state, buffer, buffer.Capacity, 0x4, GetKeyboardLayout(thread));
                if (n > 0)
                {
                    string typed = buffer.ToString(0, n);
                    if (typed[0] >= ' ')
                    {
                        text = typed;
                    }
                }
            }

            Key key = KeyInterop.KeyFromVirtualKey((int)vk);
            _swallowed[vk] = true;
            _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => KeyPressed?.Invoke(key, text, ctrl, shift, alt)));
            return (IntPtr)1;
        }
        catch (Exception ex)
        {
            Log.Error("Keyboard hook callback failed", ex);
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }
}
