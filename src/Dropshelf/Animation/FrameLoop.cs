using System;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;

namespace Dropshelf.Animation;

/// <summary>
/// Runs a per-frame callback only while there is something to animate: the callback gets the elapsed seconds and returns
/// false once everything has settled, which stops the loop again so an idle app costs no frames.
/// <para>
/// By default it follows <see cref="CompositionTarget.Rendering"/> (the display's refresh rate), which is right for short
/// transitions. Animations that can run for minutes (the equalizer while music plays, spinners, pulses) pass a frame-rate
/// cap instead: they then tick from a dispatcher timer, so they never keep WPF's render loop spinning at 60-240 Hz and the
/// (layered) pill window only repaints when they actually changed something.
/// </para>
/// </summary>
internal sealed class FrameLoop
{
    private readonly Func<double, bool> _tick;
    private readonly DispatcherTimer? _timer;
    private long _last;
    private bool _running;

    /// <param name="tick">Called with the elapsed seconds; returns false when the animation has settled.</param>
    /// <param name="maxFps">0 = every rendered frame; otherwise the loop ticks at most this often.</param>
    public FrameLoop(Func<double, bool> tick, int maxFps = 0)
    {
        _tick = tick;
        if (maxFps > 0)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / maxFps) };
            _timer.Tick += (_, _) => OnFrame();
        }
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
        if (_timer != null)
        {
            _timer.Start();
        }
        else
        {
            CompositionTarget.Rendering += OnRendering;
        }
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        if (_timer != null)
        {
            _timer.Stop();
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e) => OnFrame();

    private void OnFrame()
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
            Stop(); // never leave a throwing callback ticking
            throw;
        }

        if (!keepGoing)
        {
            Stop();
        }
    }
}
