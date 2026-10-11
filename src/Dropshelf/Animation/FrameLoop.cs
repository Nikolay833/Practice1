using System;
using System.Diagnostics;
using System.Windows.Media;

namespace Dropshelf.Animation;

/// <summary>
/// Runs a per-frame callback from <see cref="CompositionTarget.Rendering"/> only while there is something to animate:
/// the callback gets the elapsed seconds and returns false once everything has settled, which unsubscribes from
/// rendering again so an idle app costs no frames.
/// </summary>
internal sealed class FrameLoop
{
    private readonly Func<double, bool> _tick;
    private long _last;
    private bool _running;

    public FrameLoop(Func<double, bool> tick)
    {
        _tick = tick;
    }

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _last = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (now - _last) / (double)Stopwatch.Frequency;
        _last = now;

        bool keepGoing;
        try
        {
            keepGoing = _tick(dt);
        }
        catch
        {
            Stop(); // never leave a throwing callback subscribed to every frame
            throw;
        }

        if (!keepGoing)
        {
            Stop();
        }
    }
}
