using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dropshelf.Animation;

namespace Dropshelf.Activities.Hud;

/// <summary>Small building blocks shared by the HUD activities (volume, brightness, battery, devices).</summary>
internal static class HudParts
{
    /// <summary>An icon-font glyph (Segoe Fluent Icons, Segoe MDL2 Assets as the fallback) in a theme brush.</summary>
    public static TextBlock Glyph(string glyph, string brushKey = "TextBrush", double size = 15)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontSize = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        text.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return text;
    }

    public static TextBlock Label(string text, double size = 12, string brushKey = "TextBrush", FontWeight? weight = null)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        label.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        label.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return label;
    }

    /// <summary>Wraps a glyph in a fixed-size cell so the compact content has a stable width.</summary>
    public static Grid Cell(FrameworkElement child, double width, double height = 24)
    {
        var grid = new Grid { Width = width, Height = height };
        grid.Children.Add(child);
        return grid;
    }
}

/// <summary>
/// A thin rounded track whose fill glides to the new value with a spring, followed by the value as text ("63%").
/// Frames are only produced while the fill moves.
/// </summary>
internal sealed class HudBar : Grid
{
    private const double TrackWidth = 54, TrackHeight = 4, LabelWidth = 34;

    private readonly Border _fill;
    private readonly TextBlock _label;
    private readonly Spring _value = new(0, 380, 0.9);
    private readonly FrameLoop _loop;

    public HudBar()
    {
        _loop = new FrameLoop(OnFrame);
        Width = TrackWidth + 8 + LabelWidth;
        Height = 24;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TrackWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8 + LabelWidth) });

        var track = new Border { Height = TrackHeight, CornerRadius = new CornerRadius(TrackHeight / 2), VerticalAlignment = VerticalAlignment.Center };
        track.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        SetColumn(track, 0);
        Children.Add(track);

        _fill = new Border
        {
            Height = TrackHeight,
            CornerRadius = new CornerRadius(TrackHeight / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 0,
        };
        _fill.SetResourceReference(Border.BackgroundProperty, "TextBrush");
        SetColumn(_fill, 0);
        Children.Add(_fill);

        _label = HudParts.Label(string.Empty, 12, "TextBrush", FontWeights.SemiBold);
        _label.HorizontalAlignment = HorizontalAlignment.Right;
        _label.Margin = new Thickness(8, 0, 0, 0);
        SetColumn(_label, 1);
        Children.Add(_label);
    }

    /// <summary>Sets the fill (0..1), the text, and which theme brush colours the fill ("TextBrush", "DangerBrush", ...).</summary>
    public void Set(double value, string label, string fillBrushKey = "TextBrush", bool animate = true)
    {
        value = Math.Clamp(value, 0, 1);
        _label.Text = label;
        _fill.SetResourceReference(Border.BackgroundProperty, fillBrushKey);

        if (!animate || AnimationSettings.IsOff || !IsVisible)
        {
            _value.Snap(value);
            Apply();
            return;
        }

        AnimationSettings.Configure(_value, 380, 0.9);
        _value.Target = value;
        _loop.Start();
    }

    private bool OnFrame(double dt)
    {
        _value.Step(dt);
        Apply();
        return !_value.SettleIfClose(0.001, 0.01);
    }

    private void Apply() => _fill.Width = Math.Max(0, Math.Min(TrackWidth, _value.Value * TrackWidth));
}
