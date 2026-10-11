using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Dropshelf.Interop;

namespace Dropshelf.Services;

/// <summary>
/// Global low-level mouse hook (WH_MOUSE_LL). Create it on the UI thread. The hook itself lives on a dedicated
/// background thread with its own message loop: Windows silently removes a low-level hook whose thread does not
/// answer within LowLevelHooksTimeout, which a busy UI thread easily would cause.
/// The callback does almost no work: it queues left-button and move events, which are then raised on the UI thread
/// through the dispatcher, so listeners never need their own locking. Moves are only forwarded while the left button is
/// held (the only time a listener needs them) and at most every <see cref="MoveIntervalMs"/> ms, so ordinary mouse movement,
/// even from a 1000 Hz mouse, never wakes the UI thread.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    private const int MaxQueued = 2048; // moves are dropped beyond this while the UI thread is stalled
    private const uint MoveIntervalMs = 8; // forward at most ~125 moves a second

    private readonly record struct MouseEvent(int Msg, int X, int Y, int Delta = 0);

    /// <summary>Screen rectangle (physical pixels) where the wheel is captured; immutable so the hook thread can read it without locking.</summary>
    private sealed record WheelRegion(int Left, int Top, int Right, int Bottom);

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly ConcurrentQueue<MouseEvent> _queue = new();

    // Keep references so the delegates are never garbage collected while the hook is installed.
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private readonly Action _drain;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private int _queued;
    private int _drainPending;
    private volatile bool _installed;
    private volatile WheelRegion? _wheelRegion;
    private bool _leftDown;          // hook thread only
    private uint _lastMoveTime;      // hook thread only: event time of the last forwarded move

    public MouseHook()
    {
        _proc = HookCallback;
        _drain = Drain;
    }

    public bool IsInstalled => _installed;

    /// <summary>The events below are raised on the UI thread.</summary>
    public event Action<int, int>? LeftDown;
    public event Action<int, int>? LeftUp;
    public event Action<int, int>? Move;

    /// <summary>Wheel turned inside the region set by <see cref="SetWheelRegion"/>: x, y and the raw wheel delta (120 per notch, positive = away from the user).</summary>
    public event Action<int, int, int>? Wheel;

    /// <summary>
    /// Captures the mouse wheel over a screen rectangle: wheel events there are delivered to <see cref="Wheel"/> and are
    /// not passed on to the window underneath. Pass <c>null</c> to stop capturing. Requires the hook to be installed.
    /// </summary>
    public void SetWheelRegion(NativeMethods.RECT? region) =>
        _wheelRegion = region is { } r ? new WheelRegion(r.Left, r.Top, r.Right, r.Bottom) : null;

    public void Install()
    {
        if (_thread != null)
        {
            return;
        }

        var ready = new ManualResetEventSlim(false); // not disposed: the thread may still signal it after a timeout
        var thread = new Thread(() => RunHookThread(ready))
        {
            IsBackground = true,
            Name = "Dropshelf.MouseHook",
        };
        thread.Start();

        // Only covers SetWindowsHookEx on the new thread; it returns almost immediately.
        if (!ready.Wait(2000) || !_installed)
        {
            Log.Error("Mouse hook thread did not start");
            StopThread(thread);
            return;
        }

        _thread = thread;
    }

    public void Uninstall()
    {
        var thread = _thread;
        if (thread == null)
        {
            return;
        }

        _thread = null;
        StopThread(thread);
    }

    private void StopThread(Thread thread)
    {
        _installed = false;
        uint id = _threadId;
        if (id != 0 && thread.IsAlive && !NativeMethods.PostThreadMessage(id, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero))
        {
            Log.Error($"PostThreadMessage(WM_QUIT) failed (error {Marshal.GetLastWin32Error()})");
        }

        // The hook thread unhooks itself once its message loop ends.
        if (!thread.Join(1000))
        {
            Log.Error("Mouse hook thread did not stop in time");
        }

        _threadId = 0;
        while (_queue.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _queued, 0);
    }

    private void RunHookThread(ManualResetEventSlim ready)
    {
        try
        {
            // Touch the message queue so PostThreadMessage works from now on.
            NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);
            _threadId = NativeMethods.GetCurrentThreadId();

            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Log.Error($"SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()})");
                return;
            }

            _installed = true;
            ready.Set();

            // The low-level hook callback is invoked from inside GetMessage on this thread.
            while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
        }
        catch (Exception ex)
        {
            Log.Error("Mouse hook thread failed", ex);
        }
        finally
        {
            _installed = false;
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }

            ready.Set(); // also releases Install() when the hook could not be set
        }
    }

    // Runs on the hook thread; must stay cheap.
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                int msg = wParam.ToInt32();
                if (msg == NativeMethods.WM_MOUSEWHEEL && _wheelRegion is { } region)
                {
                    var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    if (data.pt.X >= region.Left && data.pt.X < region.Right && data.pt.Y >= region.Top && data.pt.Y < region.Bottom)
                    {
                        int delta = (short)((data.mouseData >> 16) & 0xFFFF);
                        _queue.Enqueue(new MouseEvent(msg, data.pt.X, data.pt.Y, delta));
                        Interlocked.Increment(ref _queued);
                        if (Interlocked.Exchange(ref _drainPending, 1) == 0)
                        {
                            _dispatcher.BeginInvoke(DispatcherPriority.Input, _drain);
                        }

                        return (IntPtr)1; // swallow: the window under the pill must not scroll as well
                    }
                }
                else if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP
                         || (msg == NativeMethods.WM_MOUSEMOVE && _leftDown && Volatile.Read(ref _queued) < MaxQueued && Move != null))
                {
                    // Button events are always kept (their order matters); moves only while the button is held, throttled.
                    var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    bool forward = true;
                    if (msg == NativeMethods.WM_MOUSEMOVE)
                    {
                        forward = unchecked(data.time - _lastMoveTime) >= MoveIntervalMs;
                    }
                    else
                    {
                        _leftDown = msg == NativeMethods.WM_LBUTTONDOWN;
                    }

                    if (forward)
                    {
                        if (msg == NativeMethods.WM_MOUSEMOVE)
                        {
                            _lastMoveTime = data.time;
                        }

                        _queue.Enqueue(new MouseEvent(msg, data.pt.X, data.pt.Y));
                        Interlocked.Increment(ref _queued);
                        if (Interlocked.Exchange(ref _drainPending, 1) == 0)
                        {
                            _dispatcher.BeginInvoke(DispatcherPriority.Input, _drain);
                        }
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

    // Runs on the UI thread.
    private void Drain()
    {
        Interlocked.Exchange(ref _drainPending, 0);
        while (_queue.TryDequeue(out var e))
        {
            Interlocked.Decrement(ref _queued);
            if (!_installed)
            {
                continue; // uninstalled while events were in flight
            }

            try
            {
                switch (e.Msg)
                {
                    case NativeMethods.WM_MOUSEMOVE: Move?.Invoke(e.X, e.Y); break;
                    case NativeMethods.WM_LBUTTONDOWN: LeftDown?.Invoke(e.X, e.Y); break;
                    case NativeMethods.WM_LBUTTONUP: LeftUp?.Invoke(e.X, e.Y); break;
                    case NativeMethods.WM_MOUSEWHEEL: Wheel?.Invoke(e.X, e.Y, e.Delta); break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Mouse event handler failed", ex);
            }
        }
    }

    public void Dispose() => Uninstall();
}
