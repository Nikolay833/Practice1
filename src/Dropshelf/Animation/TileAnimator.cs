using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Dropshelf.Animation;

/// <summary>
/// Reusable animations for list tiles and rows: 3D-style flip-in (ScaleX from 0 plus a slight rotation), fly-in from a
/// point, a slide-and-fade for list rows, and the hover lift with a fake perspective tilt. Everything is skipped in
/// Off mode, and Subtle keeps only short fades and slides (no flips, no tilt).
/// </summary>
internal static class TileAnimator
{
    private static readonly Color Glow = Color.FromRgb(0x3B, 0x82, 0xF6);

    private sealed class Transforms
    {
        public ScaleTransform Scale { get; } = new(1, 1);
        public SkewTransform Skew { get; } = new(0, 0);
        public RotateTransform Rotate { get; } = new(0);
        public TranslateTransform Translate { get; } = new(0, 0);
    }

    private static readonly ConditionalWeakTable<FrameworkElement, Transforms> Table = new();

    /// <summary>The element's own scale / skew / rotate / translate stack, created (and applied) on first use.</summary>
    private static Transforms Of(FrameworkElement element)
    {
        if (!Table.TryGetValue(element, out var t))
        {
            t = new Transforms();
            var group = new TransformGroup();
            group.Children.Add(t.Scale);
            group.Children.Add(t.Skew);
            group.Children.Add(t.Rotate);
            group.Children.Add(t.Translate);
            element.RenderTransform = group;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            Table.Add(element, t);
        }

        return t;
    }

    // ---- Entrances ------------------------------------------------------------------------------

    /// <summary>Tile enters like a card flipping up: ScaleX 0 to 1, a few degrees of rotation, fade; staggered 30 ms per index.</summary>
    public static void FlipIn(FrameworkElement element, int index)
    {
        if (AnimationSettings.IsOff)
        {
            return;
        }

        var t = Of(element);
        if (AnimationSettings.IsFull)
        {
            double delay = Math.Min(index, 14) * 30;
            var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
            Run(t.Scale, ScaleTransform.ScaleXProperty, 0, 1, 380, delay, back);
            Run(t.Rotate, RotateTransform.AngleProperty, -10, 0, 380, delay, back);
            Run(t.Translate, TranslateTransform.YProperty, 8, 0, 320, delay, Cubic());
            Run(element, UIElement.OpacityProperty, 0, 1, 220, delay, null);
        }
        else
        {
            Run(t.Translate, TranslateTransform.YProperty, 5, 0, 160, 0, Cubic());
            Run(element, UIElement.OpacityProperty, 0, 1, 140, 0, null);
        }
    }

    /// <summary>A freshly dropped tile flies in from <paramref name="from"/> (offset from its final position) while growing.</summary>
    public static void FlyIn(FrameworkElement element, Vector from)
    {
        if (AnimationSettings.IsOff)
        {
            return;
        }

        var t = Of(element);
        if (AnimationSettings.IsFull)
        {
            var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };
            Run(t.Translate, TranslateTransform.XProperty, from.X, 0, 460, 0, Cubic());
            Run(t.Translate, TranslateTransform.YProperty, from.Y, 0, 460, 0, Cubic());
            Run(t.Scale, ScaleTransform.ScaleXProperty, 0.3, 1, 460, 0, back);
            Run(t.Scale, ScaleTransform.ScaleYProperty, 0.3, 1, 460, 0, back);
            Run(element, UIElement.OpacityProperty, 0, 1, 200, 0, null);
        }
        else
        {
            Run(element, UIElement.OpacityProperty, 0, 1, 160, 0, null);
        }
    }

    /// <summary>List row fades in while sliding up a few pixels (clipboard popup, staggered by the caller).</summary>
    public static void SlideFadeIn(FrameworkElement element, double delayMs)
    {
        if (AnimationSettings.IsOff)
        {
            return;
        }

        var t = Of(element);
        double scale = AnimationSettings.TimeScale;
        Run(t.Translate, TranslateTransform.YProperty, 8, 0, 260 * scale, delayMs * scale, Cubic());
        Run(element, UIElement.OpacityProperty, 0, 1, 200 * scale, delayMs * scale, null);
    }

    // ---- Hover: lift + fake perspective tilt ------------------------------------------------------

    public static void HoverEnter(FrameworkElement root, FrameworkElement card)
    {
        if (AnimationSettings.IsOff)
        {
            return;
        }

        var t = Of(root);
        t.Skew.BeginAnimation(SkewTransform.AngleXProperty, null);
        t.Skew.BeginAnimation(SkewTransform.AngleYProperty, null);

        double scale = AnimationSettings.IsFull ? 1.05 : 1.0;
        double ms = 140 * AnimationSettings.TimeScale;
        To(t.Translate, TranslateTransform.YProperty, -3, ms);
        To(t.Scale, ScaleTransform.ScaleXProperty, scale, ms);
        To(t.Scale, ScaleTransform.ScaleYProperty, scale, ms);

        // Shadow that grows under the lifted tile (a soft blue glow; black would vanish on the dark panel).
        var shadow = new DropShadowEffect { Color = Glow, ShadowDepth = 3, BlurRadius = 2, Opacity = 0 };
        card.Effect = shadow;
        shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(14, TimeSpan.FromMilliseconds(ms)));
        shadow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.45, TimeSpan.FromMilliseconds(ms)));
    }

    /// <summary>Tilts the tile toward the cursor with a small skew (Full mode only). <paramref name="position"/> is relative to the tile.</summary>
    public static void HoverMove(FrameworkElement root, Point position)
    {
        if (!AnimationSettings.IsFull || root.ActualWidth < 1 || root.ActualHeight < 1)
        {
            return;
        }

        var t = Of(root);
        double nx = Math.Clamp(position.X / root.ActualWidth * 2 - 1, -1, 1);
        double ny = Math.Clamp(position.Y / root.ActualHeight * 2 - 1, -1, 1);
        t.Skew.AngleX = -ny * 4; // cursor low: the bottom edge swings toward you
        t.Skew.AngleY = nx * 4;
    }

    public static void HoverLeave(FrameworkElement root, FrameworkElement card)
    {
        if (!Table.TryGetValue(root, out var t))
        {
            return;
        }

        double ms = 160 * Math.Max(AnimationSettings.TimeScale, 0.01);
        To(t.Translate, TranslateTransform.YProperty, 0, ms);
        To(t.Scale, ScaleTransform.ScaleXProperty, 1, ms);
        To(t.Scale, ScaleTransform.ScaleYProperty, 1, ms);
        To(t.Skew, SkewTransform.AngleXProperty, 0, ms);
        To(t.Skew, SkewTransform.AngleYProperty, 0, ms);

        if (card.Effect is DropShadowEffect shadow)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(ms));
            fade.Completed += (_, _) =>
            {
                if (ReferenceEquals(card.Effect, shadow))
                {
                    card.Effect = null;
                }
            };
            shadow.BeginAnimation(DropShadowEffect.OpacityProperty, fade);
        }
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static IEasingFunction Cubic() => new CubicEase { EasingMode = EasingMode.EaseOut };

    private static void To(Animatable target, DependencyProperty property, double to, double ms)
    {
        target.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Cubic() });
    }

    /// <summary>
    /// Animates <paramref name="property"/> from <paramref name="from"/> to <paramref name="to"/> after
    /// <paramref name="delayMs"/>. The start value is applied immediately so the element is already in its start pose during
    /// the delay; when done, the animation is removed and the final value is written to the property itself.
    /// </summary>
    private static void Run(DependencyObject target, DependencyProperty property, double from, double to, double ms, double delayMs, IEasingFunction? ease)
    {
        var animatable = (IAnimatable)target;
        target.SetValue(property, from);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(Math.Max(1, ms)))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.Completed += (_, _) =>
        {
            animatable.BeginAnimation(property, null);
            target.SetValue(property, to);
        };
        animatable.BeginAnimation(property, animation);
    }
}
