using System;
using System.Windows;
using System.Windows.Media;
using Dropshelf.Animation;

namespace Dropshelf.Activities.Power;

/// <summary>
/// An iOS-style battery drawn in code: a thin outline with a rounded fill and, while charging, a bolt in the middle.
/// <see cref="Play"/> animates it: the fill glides to the level (from empty when the charger is plugged in), the bolt pops in
/// with a little overshoot, a soft highlight sweeps across the fill once, and a low battery breathes. Frames are only produced
/// while something moves (about 1.5 s per change).
/// </summary>
internal sealed class BatteryGlyph : FrameworkElement
{
    private const double BodyW = 24, BodyH = 12.5, Inset = 2;

    private static readonly Geometry BoltShape = Freeze(Geometry.Parse("M4.4,0 L0,5.9 L3.1,5.9 L2.3,10.4 L6.9,4.2 L3.8,4.2 Z"));

    private readonly Spring _fill = new(0, 300, 0.95);
    private readonly Spring _bolt = new(0, 460, 0.48);
    private readonly FrameLoop _loop;
    private Color _tone = Colors.White;
    private bool _pulse;
    private double _clock;
    private double _opacity = 1;

    public BatteryGlyph()
    {
        Width = 27;
        Height = 13;
        _loop = new FrameLoop(OnFrame);
        SnapsToDevicePixels = true;
    }

    /// <summary>Called every frame with the fill level shown (0..1), so a percentage can count along.</summary>
    public Action<double>? LevelChanged { get; set; }

    /// <summary>
    /// Shows <paramref name="level"/> (0..1) in <paramref name="tone"/>. <paramref name="fromEmpty"/> fills it up from zero (the
    /// charger was just plugged in); <paramref name="pulse"/> makes the fill breathe (low battery).
    /// </summary>
    public void Play(double level, Color tone, bool charging, bool fromEmpty, bool pulse)
    {
        _tone = tone;
        _pulse = pulse;
        _clock = 0;
        if (fromEmpty)
        {
            _fill.Snap(0);
        }

        _fill.Target = Math.Clamp(level, 0, 1);
        _bolt.Target = charging ? 1 : 0;
        if (AnimationSettings.IsOff)
        {
            _fill.Snap(_fill.Target);
            _bolt.Snap(_bolt.Target);
            _opacity = 1;
            LevelChanged?.Invoke(_fill.Value);
            InvalidateVisual();
            return;
        }

        if (charging && _bolt.Value < 0.05)
        {
            _bolt.Snap(0);
        }

        AnimationSettings.Configure(_fill, 300, 0.95);
        AnimationSettings.Configure(_bolt, 460, 0.48);
        _loop.Start();
    }

    private bool OnFrame(double dt)
    {
        _clock += dt;
        _fill.Step(dt);
        _bolt.Step(dt);
        _opacity = _pulse ? 0.55 + 0.45 * (0.5 + 0.5 * Math.Cos(_clock * 6)) : 1;
        LevelChanged?.Invoke(Math.Clamp(_fill.Value, 0, 1));
        InvalidateVisual();

        double t = _clock / AnimationSettings.TimeScale;
        bool settled = _fill.SettleIfClose(0.001, 0.01) & _bolt.SettleIfClose(0.002, 0.02);
        if (settled && t > (_pulse ? 2.2 : 1.4))
        {
            _opacity = 1;
            InvalidateVisual();
            return false;
        }

        return true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var outline = new SolidColorBrush(Color.FromArgb(0x73, _tone.R, _tone.G, _tone.B));
        var body = new Rect(0.5, 0.5, BodyW - 1, BodyH - 1);
        dc.DrawRoundedRectangle(null, new Pen(outline, 1), body, 3.6, 3.6);
        dc.DrawRoundedRectangle(outline, null, new Rect(BodyW + 0.6, 4.2, 1.8, 4.1), 0.9, 0.9);

        double level = Math.Clamp(_fill.Value, 0, 1);
        double innerW = BodyW - 2 * Inset, innerH = BodyH - 2 * Inset;
        double w = level <= 0.001 ? 0 : Math.Max(2.4, innerW * level);
        if (w > 0)
        {
            var fill = new Rect(Inset, Inset, w, innerH);
            var tone = new SolidColorBrush(_tone) { Opacity = _opacity };
            dc.DrawRoundedRectangle(tone, null, fill, 2, 2);

            // One soft highlight sweeping left to right across the fill.
            double sweep = (_clock / AnimationSettings.TimeScale - 0.25) / 0.9;
            if (sweep is > 0 and < 1)
            {
                double x = Inset - 8 + (innerW + 16) * sweep;
                var shine = new LinearGradientBrush
                {
                    StartPoint = new Point(x - 6, 0),
                    EndPoint = new Point(x + 6, 0),
                    MappingMode = BrushMappingMode.Absolute,
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                        new GradientStop(Color.FromArgb(0x8C, 255, 255, 255), 0.5),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
                    },
                };
                dc.PushClip(new RectangleGeometry(fill, 2, 2));
                dc.DrawRectangle(shine, null, fill);
                dc.Pop();
            }
        }

        double bolt = _bolt.Value;
        if (bolt > 0.01)
        {
            // The bolt in the middle, with a dark outline so it reads on the green fill.
            dc.PushTransform(new TranslateTransform(BodyW / 2 - 3.45, BodyH / 2 - 5.2));
            dc.PushTransform(new ScaleTransform(bolt, bolt, 3.45, 5.2));
            dc.DrawGeometry(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0)), 1.1) { LineJoin = PenLineJoin.Round }, BoltShape);
            dc.Pop();
            dc.Pop();
        }
    }

    private static Geometry Freeze(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }
}
