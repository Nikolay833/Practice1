using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dropshelf.Animation;

namespace Dropshelf.Activities;

/// <summary>
/// The tab strip at the top of the expanded shelf. A soft accent pill glides (spring) behind the selected tab.
/// With a single tab it is just a bold title, like the shelf header used to be.
/// </summary>
internal sealed class ActivityTabBar : Grid
{
    private const double TabHeight = 24, TabPadding = 12, TabGap = 4;

    private sealed class Tab
    {
        public required string Id;
        public required Border Root;
        public required TextBlock Text;
    }

    private readonly Border _indicator;
    private readonly TranslateTransform _indicatorShift = new();
    private readonly StackPanel _strip;
    private readonly List<Tab> _tabs = new();
    private readonly Spring _x = new(0, 520, 0.9);
    private readonly Spring _w = new(0, 520, 0.9);
    private readonly FrameLoop _loop;
    private string _selected = string.Empty;

    public ActivityTabBar()
    {
        _loop = new FrameLoop(OnFrame);
        Height = TabHeight;

        _indicator = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            RenderTransform = _indicatorShift,
        };
        _indicator.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        _indicator.SetResourceReference(Border.BorderBrushProperty, "AccentStrokeBrush");
        Children.Add(_indicator);

        _strip = new StackPanel { Orientation = Orientation.Horizontal };
        Children.Add(_strip);
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
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var root = new Border
            {
                Height = TabHeight,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = text,
                Margin = new Thickness(0, 0, TabGap, 0),
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
        Margin = single ? new Thickness(0) : new Thickness(-TabPadding, 0, 0, 0); // the first label stays where the old title was
        foreach (Tab tab in _tabs)
        {
            tab.Root.Padding = single ? new Thickness(0) : new Thickness(TabPadding, 0, TabPadding, 0);
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
            _indicator.Visibility = Visibility.Collapsed;
            return;
        }

        double x = 0;
        foreach (Tab tab in _tabs)
        {
            double width = tab.Root.ActualWidth;
            if (tab.Id == _selected)
            {
                bool first = _indicator.Visibility != Visibility.Visible;
                _indicator.Visibility = Visibility.Visible;
                _indicator.Height = TabHeight;
                _x.Target = x;
                _w.Target = width;
                if (!animate || first || AnimationSettings.IsOff)
                {
                    _x.Snap(x);
                    _w.Snap(width);
                    Apply();
                }
                else
                {
                    AnimationSettings.Configure(_x, 520, 0.9);
                    AnimationSettings.Configure(_w, 520, 0.9);
                    _loop.Start();
                }

                return;
            }

            x += width + TabGap;
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
        _indicatorShift.X = _x.Value;
        _indicator.Width = Math.Max(0, _w.Value);
    }
}
