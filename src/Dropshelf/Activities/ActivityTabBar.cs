using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dropshelf.Animation;

namespace Dropshelf.Activities;

/// <summary>
/// The tabs at the top of the expanded island, drawn as an Apple segmented control: a rounded grey track with a lighter
/// "thumb" that glides (spring) behind the selected segment. With a single tab it is just that tab's title.
/// </summary>
internal sealed class ActivityTabBar : Border
{
    private const double SegmentHeight = 24, SegmentPadding = 12, Inset = 2;

    private sealed class Tab
    {
        public required string Id;
        public required Border Root;
        public required TextBlock Text;
    }

    private static readonly Brush TrackFill = Frozen(Color.FromArgb(0x3D, 0x76, 0x76, 0x80));  // tertiarySystemFill
    private static readonly Brush ThumbFill = Frozen(Color.FromRgb(0x63, 0x63, 0x66));         // systemGray2

    private readonly Border _thumb;
    private readonly TranslateTransform _thumbShift = new();
    private readonly StackPanel _strip;
    private readonly List<Tab> _tabs = new();
    private readonly Spring _x = new(0, 520, 0.85);
    private readonly Spring _w = new(0, 520, 0.85);
    private readonly FrameLoop _loop;
    private string _selected = string.Empty;

    public ActivityTabBar()
    {
        _loop = new FrameLoop(OnFrame);
        Height = SegmentHeight + 2 * Inset;
        CornerRadius = new CornerRadius(Height / 2);
        Padding = new Thickness(Inset);

        _thumb = new Border
        {
            Height = SegmentHeight,
            CornerRadius = new CornerRadius(SegmentHeight / 2),
            Background = ThumbFill,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            RenderTransform = _thumbShift,
        };

        _strip = new StackPanel { Orientation = Orientation.Horizontal };
        Child = new Grid { Children = { _thumb, _strip } };
    }

    public event Action<string>? TabClicked;

    public string Selected => _selected;

    public void SetTabs(IReadOnlyList<(string Id, string Title)> tabs)
    {
        _strip.Children.Clear();
        _tabs.Clear();
        foreach ((string id, string title) in tabs)
        {
            var text = new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var root = new Border
            {
                Height = SegmentHeight,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = text,
            };
            var tab = new Tab { Id = id, Root = root, Text = text };
            root.MouseLeftButtonUp += (_, e) =>
            {
                TabClicked?.Invoke(id);
                e.Handled = true;
            };
            root.MouseEnter += (_, _) => Restyle(tab, hover: true);
            root.MouseLeave += (_, _) => Restyle(tab, hover: false);
            root.SizeChanged += (_, _) => Retarget(animate: false);
            _tabs.Add(tab);
            _strip.Children.Add(root);
        }

        if (_tabs.Count > 0 && _tabs.TrueForAll(t => t.Id != _selected))
        {
            _selected = _tabs[0].Id;
        }

        ApplyTabLook();
        Retarget(animate: false);
    }

    public void Select(string id, bool instant = false)
    {
        if (_selected == id && !instant)
        {
            return;
        }

        _selected = id;
        ApplyTabLook();
        Retarget(animate: !instant);
    }

    private void ApplyTabLook()
    {
        bool single = _tabs.Count <= 1;
        Background = single ? Brushes.Transparent : TrackFill;
        foreach (Tab tab in _tabs)
        {
            tab.Root.Padding = new Thickness(single ? 2 : SegmentPadding, 0, single ? 2 : SegmentPadding, 0);
            tab.Text.FontSize = single ? 14 : 12;
            Restyle(tab, hover: false);
        }
    }

    private void Restyle(Tab tab, bool hover)
    {
        bool selected = tab.Id == _selected || _tabs.Count <= 1;
        tab.Text.SetResourceReference(TextBlock.ForegroundProperty, selected || hover ? "TextBrush" : "TextSecondaryBrush");
    }

    private void Retarget(bool animate)
    {
        if (_tabs.Count <= 1)
        {
            _thumb.Visibility = Visibility.Collapsed;
            return;
        }

        double x = 0;
        foreach (Tab tab in _tabs)
        {
            double width = tab.Root.ActualWidth;
            if (tab.Id == _selected)
            {
                bool first = _thumb.Visibility != Visibility.Visible;
                _thumb.Visibility = Visibility.Visible;
                _x.Target = x;
                _w.Target = width;
                if (!animate || first || AnimationSettings.IsOff || !IsVisible)
                {
                    _loop.Stop();
                    _x.Snap(x);
                    _w.Snap(width);
                    Apply();
                }
                else
                {
                    AnimationSettings.Configure(_x, 520, 0.85);
                    AnimationSettings.Configure(_w, 520, 0.85);
                    _loop.Start();
                }

                return;
            }

            x += width;
        }
    }

    private bool OnFrame(double dt)
    {
        _x.Step(dt);
        _w.Step(dt);
        Apply();
        return !(_x.SettleIfClose(0.05, 0.5) & _w.SettleIfClose(0.05, 0.5));
    }

    private void Apply()
    {
        _thumbShift.X = _x.Value;
        _thumb.Width = Math.Max(0, _w.Value);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
