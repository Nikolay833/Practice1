using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Dropshelf.Animation;
using Dropshelf.Themes;

namespace Dropshelf.Activities.Hud;

/// <summary>
/// A thin circular progress ring with an optional glyph in the middle. Determinate (an arc from 12 o'clock, clockwise) or
/// indeterminate (a quarter arc that spins). The spin only produces frames while the ring is visible, and only while it is
/// indeterminate. Colour follows the accent unless <see cref="SetColor"/> is used.
/// </summary>
internal sealed class ProgressRing : Grid
{
    private readonly double _size;
    private readonly double _thickness;
    private readonly Path _arc;
    private readonly TextBlock? _glyph;
    private readonly RotateTransform _spin = new(0);
    private readonly FrameLoop _loop;
    private double _value;
    private bool _indeterminate;

    public ProgressRing(double size = 24, double thickness = 2.5, string? glyph = null, double glyphSize = 11)
    {
        _size = size;
        _thickness = thickness;
        Width = size;
        Height = size;
        _loop = new FrameLoop(OnSpin);

        var track = new Ellipse { Margin = new Thickness(thickness / 2), StrokeThickness = thickness };
        track.Stroke = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        Children.Add(track);

        _arc = new Path
        {
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = _spin,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Width = size,
            Height = size,
        };
        _arc.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        Children.Add(_arc);

        if (glyph != null)
        {
            _glyph = HudParts.Glyph(glyph, "TextBrush", glyphSize);
            Children.Add(_glyph);
        }

        IsVisibleChanged += (_, _) => UpdateSpin();
        Unloaded += (_, _) => _loop.Stop();
    }

    /// <summary>The middle glyph (ignored when the ring was built without one).</summary>
    public string? Glyph
    {
        set
        {
            if (_glyph != null && value != null)
            {
                _glyph.Text = value;
            }
        }
    }

    public void SetGlyphBrush(string brushKey) => _glyph?.SetResourceReference(TextBlock.ForegroundProperty, brushKey);

    /// <summary>Back to the accent colour.</summary>
    public void FollowAccent() => _arc.SetResourceReference(Shape.StrokeProperty, "AccentBrush");

    /// <summary>A fixed colour for the arc (the timer shifts it from the accent to orange).</summary>
    public void SetColor(Color color)
    {
        _arc.Stroke = ColorUtil.Brush(color);
    }

    /// <summary>Sets the fraction (0..1) shown by a determinate ring and switches the spin off.</summary>
    public void SetProgress(double value)
    {
        value = Math.Clamp(value, 0, 1);
        if (!_indeterminate && Math.Abs(value - _value) < 0.0005 && _arc.Data != null)
        {
            return;
        }

        _value = value;
        _indeterminate = false;
        _spin.Angle = 0;
        _arc.Data = Arc(_value);
        UpdateSpin();
    }

    public void SetIndeterminate()
    {
        if (_indeterminate)
        {
            return;
        }

        _indeterminate = true;
        _arc.Data = Arc(0.27);
        UpdateSpin();
    }

    private void UpdateSpin()
    {
        if (_indeterminate && IsVisible && !AnimationSettings.IsOff)
        {
            _loop.Start();
        }
        else
        {
            _loop.Stop();
        }
    }

    private bool OnSpin(double dt)
    {
        _spin.Angle = (_spin.Angle + dt * 300) % 360;
        return _indeterminate && IsVisible;
    }

    private Geometry Arc(double fraction)
    {
        if (fraction <= 0.0005)
        {
            return Geometry.Empty;
        }

        double radius = (_size - _thickness) / 2;
        double cx = _size / 2, cy = _size / 2;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            Point start = new(cx, cy - radius);
            ctx.BeginFigure(start, false, false);
            if (fraction >= 0.9995)
            {
                ctx.ArcTo(new Point(cx, cy + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                ctx.ArcTo(start, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            else
            {
                double angle = fraction * 2 * Math.PI;
                Point end = new(cx + radius * Math.Sin(angle), cy - radius * Math.Cos(angle));
                ctx.ArcTo(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }
}
