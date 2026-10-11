using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Dropshelf.Animation;

/// <summary>
/// One-shot entrance animations for list tiles and rows: a fly-in from a point for freshly dropped tiles and a short
/// slide-and-fade for list rows. Both are a handful of frames and leave no animation clocks behind. Everything is skipped
/// in Off mode; Subtle keeps only the fades.
/// </summary>
internal static class TileAnimator
{
    private sealed class Transforms
    {
        public ScaleTransform Scale { get; } = new(1, 1);
        public TranslateTransform Translate { get; } = new(0, 0);
    }

    private static readonly ConditionalWeakTable<FrameworkElement, Transforms> Table = new();

    /// <summary>The element's own scale / translate stack, created (and applied) on first use.</summary>
    private static Transforms Of(FrameworkElement element)
    {
        if (!Table.TryGetValue(element, out var t))
        {
            t = new Transforms();
            var group = new TransformGroup();
            group.Children.Add(t.Scale);
            group.Children.Add(t.Translate);
            element.RenderTransform = group;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            Table.Add(element, t);
        }

        return t;
    }

    /// <summary>A freshly dropped tile flies in from <paramref name="from"/> (offset from its final position) while growing.</summary>
    public static void FlyIn(FrameworkElement element, Vector from)
    {
        if (AnimationSettings.IsOff)
        {
            return;
        }

        if (AnimationSettings.IsFull)
        {
            var t = Of(element);
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            Run(t.Translate, TranslateTransform.XProperty, from.X, 0, 380, 0, ease);
            Run(t.Translate, TranslateTransform.YProperty, from.Y, 0, 380, 0, ease);
            Run(t.Scale, ScaleTransform.ScaleXProperty, 0.5, 1, 380, 0, ease);
            Run(t.Scale, ScaleTransform.ScaleYProperty, 0.5, 1, 380, 0, ease);
        }

        Run(element, UIElement.OpacityProperty, 0, 1, 180, 0, null);
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
        Run(t.Translate, TranslateTransform.YProperty, 6, 0, 240 * scale, delayMs * scale, new QuinticEase { EasingMode = EasingMode.EaseOut });
        Run(element, UIElement.OpacityProperty, 0, 1, 180 * scale, delayMs * scale, null);
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
