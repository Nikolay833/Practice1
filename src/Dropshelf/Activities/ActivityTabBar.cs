using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dropshelf.Animation;

namespace Dropshelf.Activities;

/// <summary>
/// The tab switcher that floats under the open island: a glass capsule of round icon buttons, with a lighter circle that
/// glides (spring) behind the selected one. Each tab shows its title as a tooltip.
/// </summary>
internal sealed class ActivityTabBar : Border
{
    private const double ButtonSize = 30, Gap = 2, Inset = 4;

    private sealed class Tab
    {
        public required string Id;
        public required Border Root;
        public required TextBlock Glyph;
    }

    private static readonly Brush ThumbFill = Frozen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));

    private readonly Border _thumb;
    private readonly TranslateTransform _thumbShift = new();
    private readonly StackPanel _strip;
    private readonly List<Tab> _tabs = new();
    private readonly Spring _x = new(0, 520, 0.85);
    private readonly FrameLoop _loop;
    private string _selected = string.Empty;

    public ActivityTabBar()
    {
        _loop = new FrameLoop(OnFrame);
        Height = ButtonSize + 2 * Inset;
        CornerRadius = new CornerRadius(Height / 2);
        Padding = new Thickness(Inset);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "GlassFillBrush");
        SetResourceReference(BorderBrushProperty, "GlassRimBrush");

        _thumb = new Border
        {
            Width = ButtonSize,
            Height = ButtonSize,
            CornerRadius = new CornerRadius(ButtonSize / 2),
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

    /// <summary>Rebuilds the buttons: one per tab, showing <c>Glyph</c> (Segoe Fluent Icons) with <c>Title</c> as tooltip.</summary>
    public void SetTabs(IReadOnlyList<(string Id, string Title, string Glyph)> tabs)
    {
        _strip.Children.Clear();
        _tabs.Clear();
        foreach ((string id, string title, string glyph) in tabs)
        {
            var text = new TextBlock
            {
                Text = glyph,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            text.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            var root = new Border
            {
                Width = ButtonSize,
                Height = ButtonSize,
                CornerRadius = new CornerRadius(ButtonSize / 2),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = text,
                ToolTip = title,
                Margin = new Thickness(0, 0, Gap, 0),
            };
            var tab = new Tab { Id = id, Root = root, Glyph = text };
            root.MouseLeftButtonUp += (_, e) =>
            {
                TabClicked?.Invoke(id);
                e.Handled = true;
            };
            root.MouseEnter += (_, _) => Restyle(tab, hover: true);
            root.MouseLeave += (_, _) => Restyle(tab, hover: false);
            _tabs.Add(tab);
            _strip.Children.Add(root);
        }

        if (_tabs.Count > 0)
        {
            _tabs[^1].Root.Margin = new Thickness(0);
            if (_tabs.TrueForAll(t => t.Id != _selected))
            {
                _selected = _tabs[0].Id;
            }
        }

        Visibility = _tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (Tab tab in _tabs)
        {
            Restyle(tab, hover: false);
        }

        Retarget(animate: false);
    }

    public void Select(string id, bool instant = false)
    {
        if (_selected == id && !instant)
        {
            return;
        }

        _selected = id;
        foreach (Tab tab in _tabs)
        {
            Restyle(tab, hover: false);
        }

        Retarget(animate: !instant);
    }

    private void Restyle(Tab tab, bool hover)
    {
        bool selected = tab.Id == _selected;
        tab.Glyph.SetResourceReference(TextBlock.ForegroundProperty, selected || hover ? "TextBrush" : "TextSecondaryBrush");
    }

    private void Retarget(bool animate)
    {
        int index = _tabs.FindIndex(t => t.Id == _selected);
        if (index < 0)
        {
            _thumb.Visibility = Visibility.Collapsed;
            return;
        }

        double x = index * (ButtonSize + Gap);
        bool first = _thumb.Visibility != Visibility.Visible;
        _thumb.Visibility = Visibility.Visible;
        _x.Target = x;
        if (!animate || first || AnimationSettings.IsOff || !IsVisible)
        {
            _loop.Stop();
            _x.Snap(x);
            _thumbShift.X = x;
            return;
        }

        AnimationSettings.Configure(_x, 520, 0.85);
        _loop.Start();
    }

    private bool OnFrame(double dt)
    {
        _x.Step(dt);
        _thumbShift.X = _x.Value;
        return !_x.SettleIfClose(0.05, 0.5);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
