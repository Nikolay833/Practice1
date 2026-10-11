using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dropshelf.Animation;

namespace Dropshelf.Activities;

/// <summary>
/// Shows one child at a time and crossfades between them with a short, critically damped spring: the new child fades in
/// while sliding the last few pixels into place, the old one fades out. Children are kept (just collapsed) so views that
/// stop their own animation when invisible do so. Frames are only produced during a transition.
/// </summary>
internal sealed class CrossfadeHost : Grid
{
    private sealed class Holder : Grid
    {
        public Holder(FrameworkElement content)
        {
            Content = content;
            Shift = new TranslateTransform();
            RenderTransform = Shift;
            Children.Add(content);
        }

        public FrameworkElement Content { get; }
        public TranslateTransform Shift { get; }
    }

    private readonly Dictionary<FrameworkElement, Holder> _holders = new();
    private readonly Spring _t = new(1, 420, 1);
    private readonly FrameLoop _loop;
    private Holder? _to;
    private Holder? _from;
    private double _toStart, _fromStart;

    public CrossfadeHost()
    {
        _loop = new FrameLoop(OnFrame);
    }

    /// <summary>Distance (DIPs) the incoming child travels while fading in.</summary>
    public double Slide { get; set; } = 6;

    public HorizontalAlignment ChildHorizontalAlignment { get; set; } = HorizontalAlignment.Stretch;

    public VerticalAlignment ChildVerticalAlignment { get; set; } = VerticalAlignment.Stretch;

    public FrameworkElement? Current => _to?.Content;

    /// <summary>Takes over an element that was declared in XAML inside this host and makes it the current one.</summary>
    public void Adopt(FrameworkElement element)
    {
        Children.Remove(element);
        Show(element, instant: true);
    }

    public void Show(FrameworkElement? next, bool instant = false)
    {
        Holder? target = next == null ? null : GetHolder(next);
        if (target == _to)
        {
            return;
        }

        Holder? old = _to;
        if (_from == target)
        {
            _from = null; // coming back to the child that was still fading out
        }
        else if (_from != null)
        {
            Hide(_from); // a third child mid-fade is dropped
            _from = null;
        }

        _from = old;
        _fromStart = old?.Opacity ?? 0;
        _toStart = target != null && target.Visibility == Visibility.Visible ? target.Opacity : 0;
        _to = target;

        if (target != null)
        {
            target.Visibility = Visibility.Visible;
            target.IsHitTestVisible = true;
        }

        if (old != null)
        {
            old.IsHitTestVisible = false;
        }

        if (instant || AnimationSettings.IsOff || !IsVisible)
        {
            Finish();
            return;
        }

        AnimationSettings.Configure(_t, 420, 1);
        _t.Snap(0);
        _t.Target = 1;
        Apply();
        _loop.Start();
    }

    private Holder GetHolder(FrameworkElement element)
    {
        if (!_holders.TryGetValue(element, out Holder? holder))
        {
            holder = new Holder(element)
            {
                HorizontalAlignment = ChildHorizontalAlignment,
                VerticalAlignment = ChildVerticalAlignment,
                Visibility = Visibility.Collapsed,
                Opacity = 0,
            };
            _holders[element] = holder;
            Children.Add(holder);
        }

        return holder;
    }

    private bool OnFrame(double dt)
    {
        _t.Step(dt);
        Apply();
        if (_t.SettleIfClose(0.002, 0.02))
        {
            Finish();
            return false;
        }

        return true;
    }

    private void Apply()
    {
        double t = Math.Clamp(_t.Value, 0, 1);
        if (_to != null)
        {
            Place(_to, _toStart + (1 - _toStart) * t, incoming: true);
        }

        if (_from != null)
        {
            Place(_from, _fromStart * (1 - t), incoming: false);
        }
    }

    private void Place(Holder holder, double opacity, bool incoming)
    {
        holder.Opacity = opacity;
        holder.Shift.Y = (incoming ? Slide : -Slide * 0.5) * (1 - opacity);
    }

    private void Finish()
    {
        _loop.Stop();
        if (_from != null)
        {
            Hide(_from);
            _from = null;
        }

        if (_to != null)
        {
            _to.Opacity = 1;
            _to.Shift.Y = 0;
        }
    }

    private static void Hide(Holder holder)
    {
        holder.Opacity = 0;
        holder.Shift.Y = 0;
        holder.Visibility = Visibility.Collapsed;
    }
}
