using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Dropshelf.Animation;

namespace Dropshelf.Activities.Devices;

/// <summary>
/// The device icon of the Bluetooth HUD: a glyph in a filled circle. Connecting pops it in (blue) and sends two rings rippling
/// out of it; disconnecting greys it and lets it sink back a little. About a second of frames per change, then nothing.
/// </summary>
internal sealed class DeviceBadge : Grid
{
    private const double Size = 26;

    private static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);
    private static readonly Color Grey = Color.FromRgb(0x48, 0x48, 0x4A);

    private readonly Ellipse _disc;
    private readonly TextBlock _glyph;
    private readonly Ellipse[] _rings;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Spring _pop = new(1, 420, 0.5);
    private readonly FrameLoop _loop;
    private bool _connected;
    private double _clock;

    public DeviceBadge()
    {
        Width = 34;
        Height = 32;
        _loop = new FrameLoop(OnFrame);

        _rings = new Ellipse[2];
        for (int i = 0; i < _rings.Length; i++)
        {
            _rings[i] = new Ellipse
            {
                Width = Size,
                Height = Size,
                StrokeThickness = 1.5,
                Stroke = new SolidColorBrush(Blue),
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
                IsHitTestVisible = false,
            };
            Children.Add(_rings[i]);
        }

        var badge = new Grid { Width = Size, Height = Size, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _scale };
        _disc = new Ellipse { Fill = new SolidColorBrush(Blue) };
        _glyph = new TextBlock
        {
            FontSize = 13,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        badge.Children.Add(_disc);
        badge.Children.Add(_glyph);
        Children.Add(badge);
    }

    public void Play(string glyph, bool connected)
    {
        _glyph.Text = glyph;
        _connected = connected;
        _disc.Fill = new SolidColorBrush(connected ? Blue : Grey);
        _glyph.Opacity = connected ? 1 : 0.7;
        _clock = 0;
        if (AnimationSettings.IsOff)
        {
            _scale.ScaleX = _scale.ScaleY = 1;
            foreach (Ellipse ring in _rings)
            {
                ring.Opacity = 0;
            }

            return;
        }

        _pop.Snap(connected ? 0.3 : 1.12);
        AnimationSettings.Configure(_pop, connected ? 420 : 300, connected ? 0.5 : 0.75);
        _pop.Target = connected ? 1 : 0.88;
        _loop.Start();
    }

    private bool OnFrame(double dt)
    {
        _clock += dt;
        _pop.Step(dt);
        double s = Math.Max(0, _pop.Value);
        _scale.ScaleX = _scale.ScaleY = s;

        double t = _clock / AnimationSettings.TimeScale;
        bool ringsDone = true;
        for (int i = 0; i < _rings.Length; i++)
        {
            // Connect: each ring grows from the badge and fades; the second follows the first.
            double local = _connected ? (t - 0.12 - i * 0.22) / 0.75 : 1;
            var ring = _rings[i];
            if (local is > 0 and < 1)
            {
                double eased = 1 - Math.Pow(1 - local, 3);
                var scale = (ScaleTransform)ring.RenderTransform;
                scale.ScaleX = scale.ScaleY = 1 + 0.42 * eased;
                ring.Opacity = 0.75 * (1 - local);
                ringsDone = false;
            }
            else
            {
                ring.Opacity = 0;
                if (local <= 0)
                {
                    ringsDone = false;
                }
            }
        }

        bool settled = _pop.SettleIfClose(0.002, 0.02);
        if (settled && ringsDone)
        {
            _scale.ScaleX = _scale.ScaleY = _pop.Target;
            return false;
        }

        return true;
    }
}
