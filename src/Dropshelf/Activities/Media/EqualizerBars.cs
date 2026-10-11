using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Dropshelf.Animation;
using Dropshelf.Services;

namespace Dropshelf.Activities.Media;

/// <summary>
/// The Dynamic Island's music waveform: five rounded bars that grow from their middle while music plays, tinted with a colour
/// from the artwork (white without one). Each bar is a spring chasing a target height; the targets are re-rolled about 11
/// times a second, and the loop ticks at 30 fps at most (see <see cref="FrameLoop"/>). With a working Core Audio peak meter the bar heights follow the real output level
/// (each bar with its own random spread); without one, or when the meter reads silence for a few seconds while the player
/// says "playing", the targets are pseudo-random. Frames are only produced while visible and playing (or settling).
/// </summary>
internal sealed class EqualizerBars : Canvas
{
    private const int BarCount = 5;
    private const double BarWidth = 3, BarGap = 2, BarHeight = 16, RetargetSeconds = 0.09, SilenceFallbackSeconds = 3;

    private static readonly Random Rng = new();
    private static readonly double[] StaticHeights = { 0.4, 0.75, 1.0, 0.65, 0.35 };

    private readonly AudioEndpointService? _audio;
    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly ScaleTransform[] _scales = new ScaleTransform[BarCount];
    private readonly Spring[] _springs = new Spring[BarCount];
    private readonly FrameLoop _loop;
    private bool _playing;
    private double _retargetClock;
    private double _silentFor;

    public EqualizerBars(AudioEndpointService? audio)
    {
        _audio = audio;
        Width = BarCount * BarWidth + (BarCount - 1) * BarGap;
        Height = BarHeight;
        IsHitTestVisible = false;
        _loop = new FrameLoop(OnFrame, maxFps: 30); // runs for as long as music plays: never at full refresh rate

        for (int i = 0; i < BarCount; i++)
        {
            _springs[i] = new Spring(0.15, 320, 0.55);
            _scales[i] = new ScaleTransform(1, 0.15);
            var bar = new Rectangle
            {
                Width = BarWidth,
                Height = BarHeight,
                RadiusX = BarWidth / 2,
                RadiusY = BarWidth / 2,
                RenderTransform = _scales[i],
                RenderTransformOrigin = new Point(0.5, 0.5),
                Fill = Brushes.White,
            };
            SetLeft(bar, i * (BarWidth + BarGap));
            SetTop(bar, 0);
            _bars[i] = bar;
            Children.Add(bar);
        }

        IsVisibleChanged += (_, _) => Refresh();
    }

    /// <summary>Colours the bars (a colour taken from the artwork); null = white.</summary>
    public void SetTint(Color? tint)
    {
        Brush brush = Brushes.White;
        if (tint is { } color)
        {
            var solid = new SolidColorBrush(color);
            solid.Freeze();
            brush = solid;
        }

        foreach (Rectangle bar in _bars)
        {
            bar.Fill = brush;
        }
    }

    public bool Playing
    {
        get => _playing;
        set
        {
            if (_playing == value)
            {
                return;
            }

            _playing = value;
            Refresh();
        }
    }

    public void Refresh()
    {
        if (AnimationSettings.IsOff)
        {
            _loop.Stop();
            for (int i = 0; i < BarCount; i++)
            {
                _springs[i].Snap(_playing ? StaticHeights[i] : 0.15);
                _scales[i].ScaleY = _springs[i].Value;
            }

            return;
        }

        if (!IsVisible)
        {
            _loop.Stop();
            return;
        }

        if (!_playing)
        {
            for (int i = 0; i < BarCount; i++)
            {
                _springs[i].Target = 0.15;
            }
        }
        else
        {
            _retargetClock = RetargetSeconds; // roll new targets on the first frame
        }

        _loop.Start();
    }

    private bool OnFrame(double dt)
    {
        if (_playing)
        {
            _retargetClock += dt;
            if (_retargetClock >= RetargetSeconds)
            {
                _retargetClock = 0;
                Retarget();
            }
        }

        bool settled = true;
        for (int i = 0; i < BarCount; i++)
        {
            Spring s = _springs[i];
            s.Step(dt);
            _scales[i].ScaleY = Math.Clamp(s.Value, 0.1, 1);
            settled &= s.SettleIfClose(0.003, 0.05);
        }

        return _playing || !settled;
    }

    private void Retarget()
    {
        bool real = false;
        double level = 0;
        if (_audio is { HasMeter: true })
        {
            level = _audio.PeakLevel();
            if (level > 0.002)
            {
                _silentFor = 0;
            }
            else
            {
                _silentFor += RetargetSeconds;
            }

            real = _silentFor < SilenceFallbackSeconds;
        }

        // Loudness is perceived roughly logarithmically; the root keeps quiet music visible.
        double drive = real ? Math.Pow(level, 0.55) : 0;
        for (int i = 0; i < BarCount; i++)
        {
            double noise = Rng.NextDouble();
            AnimationSettings.Configure(_springs[i], 320, 0.55);
            _springs[i].Target = real
                ? 0.15 + 0.85 * Math.Clamp(drive * (0.5 + 0.7 * noise), 0, 1)
                : 0.2 + 0.8 * noise;
        }
    }
}
