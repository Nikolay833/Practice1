using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dropshelf.Interop;

namespace Dropshelf.Services;

/// <summary>
/// Detects a left-right "shake" while the left mouse button is held (i.e. while dragging something).
/// A shake is <see cref="RequiredReversals"/> horizontal direction reversals of at least
/// <see cref="MinTravelPx"/> each within <see cref="WindowMs"/> ms, after the cursor has moved
/// more than <see cref="DragThresholdPx"/> from the press point.
/// </summary>
internal sealed class ShakeDetector : IDisposable
{
    private const int DragThresholdPx = 8;
    private const int MinTravelPx = 40;
    private const int RequiredReversals = 4;
    private const int WindowMs = 600;

    private readonly MouseHook _hook;
    private readonly Queue<long> _reversals = new();

    private bool _pressed;
    private bool _ignorePress;
    private bool _dragging;
    private bool _fired;
    private int _pressX;
    private int _pressY;
    private int _direction;   // -1 left, 0 unknown, +1 right
    private int _extremeX;    // furthest x reached in the current direction

    public ShakeDetector(MouseHook hook)
    {
        _hook = hook;
        _hook.LeftDown += OnDown;
        _hook.LeftUp += OnUp;
        _hook.Move += OnMove;
    }

    /// <summary>Raised from inside the hook callback; handlers must defer real work (e.g. Dispatcher.BeginInvoke).</summary>
    public event Action? Shaken;

    private void OnDown(int x, int y)
    {
        Reset();
        _pressed = true;
        _pressX = x;
        _pressY = y;
        _extremeX = x;

        // A press that starts inside one of our own windows (tile drag, text selection, slider...) never counts.
        IntPtr hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        _ignorePress = WindowHelper.IsOwnWindow(hwnd);
    }

    private void OnUp(int x, int y) => Reset();

    private void Reset()
    {
        _pressed = false;
        _ignorePress = false;
        _dragging = false;
        _fired = false;
        _direction = 0;
        _reversals.Clear();
    }

    private void OnMove(int x, int y)
    {
        if (!_pressed || _ignorePress || _fired || DragState.IsActive)
        {
            return;
        }

        if (!_dragging)
        {
            if (Math.Abs(x - _pressX) <= DragThresholdPx && Math.Abs(y - _pressY) <= DragThresholdPx)
            {
                return;
            }

            _dragging = true;
            _extremeX = x;
            _direction = 0;
            return;
        }

        if (_direction == 0)
        {
            if (Math.Abs(x - _extremeX) >= 4)
            {
                _direction = Math.Sign(x - _extremeX);
                _extremeX = x;
            }

            return;
        }

        if (Math.Sign(x - _extremeX) == _direction)
        {
            // Still travelling the same way: extend the leg.
            _extremeX = x;
            return;
        }

        // Moving against the current direction; it only counts once the reverse travel is long enough.
        if (Math.Abs(x - _extremeX) < MinTravelPx)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        _direction = -_direction;
        _extremeX = x;
        _reversals.Enqueue(now);

        long cutoff = now - (long)(WindowMs / 1000.0 * Stopwatch.Frequency);
        while (_reversals.Count > 0 && _reversals.Peek() < cutoff)
        {
            _reversals.Dequeue();
        }

        if (_reversals.Count >= RequiredReversals)
        {
            _fired = true;
            _reversals.Clear();
            Shaken?.Invoke();
        }
    }

    public void Dispose()
    {
        _hook.LeftDown -= OnDown;
        _hook.LeftUp -= OnUp;
        _hook.Move -= OnMove;
    }
}
