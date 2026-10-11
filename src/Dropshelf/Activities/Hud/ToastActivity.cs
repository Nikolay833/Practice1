using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dropshelf.Animation;

namespace Dropshelf.Activities.Hud;

/// <summary>What a toast HUD shows: a glyph or tiny picture on the left, text (and an optional button) on the right.</summary>
internal sealed class ToastSpec
{
    /// <summary>Icon-font glyph for the left cell (ignored when <see cref="Image"/> is set).</summary>
    public string? Glyph { get; init; }

    /// <summary>Tiny picture for the left cell (a screenshot thumbnail).</summary>
    public ImageSource? Image { get; init; }

    public string Text { get; init; } = string.Empty;

    /// <summary>Smaller second line of text next to the main one (counts, errors...).</summary>
    public string? Detail { get; init; }

    public string? ButtonText { get; init; }
    public Action? ButtonClick { get; init; }

    /// <summary>Theme brush for the glyph: AccentBrush, SuccessBrush, WarnBrush, DangerBrush...</summary>
    public string Tone { get; init; } = "AccentBrush";

    public TimeSpan Duration { get; init; } = ActivityHost.HudDuration;

    /// <summary>Extra pill height while shown; the pill springs up by this much.</summary>
    public double ExtraHeight { get; init; } = 4;

    /// <summary>The left cell pops in with an overshooting spring ("ding").</summary>
    public bool Bounce { get; init; }
}

/// <summary>
/// The generic HUD: "Converted 3 files", "Screenshot saved [Copy text]", the timer's ding, "Meeting starting". Whoever has
/// something to say calls <see cref="Show"/>; the latest toast replaces the one on screen and restarts the HUD timer.
/// A toast with a button is <see cref="CompactContent.Interactive"/>, so the pill takes clicks while it is up.
/// </summary>
internal sealed class ToastActivity : PillActivityBase
{
    private readonly Spring _pop = new(1, 380, 0.42);
    private readonly FrameLoop _loop;
    private ScaleTransform? _popScale;
    private CompactContent? _content;
    private TimeSpan _duration = ActivityHost.HudDuration;

    public ToastActivity()
    {
        _loop = new FrameLoop(OnPop);
        Current = this;
    }

    /// <summary>The app's toast HUD, for code that has no reference to it (context menu actions).</summary>
    public static ToastActivity? Current { get; private set; }

    public override string Id => "toast";
    public override int Priority => 280;
    public override PillActivityKind Kind => PillActivityKind.Hud;
    public override TimeSpan HudDuration => _duration;
    public override bool QueueHudWhileExpanded => true;

    public override CompactContent? CreateCompact() => _content;

    /// <summary>Shows <paramref name="spec"/> on the pill (UI thread).</summary>
    public void Show(ToastSpec spec)
    {
        _duration = spec.Duration;

        FrameworkElement icon;
        if (spec.Image != null)
        {
            var image = new Image
            {
                Source = spec.Image,
                Width = 30,
                Height = 20,
                Stretch = Stretch.UniformToFill,
                Clip = new RectangleGeometry(new Rect(0, 0, 30, 20), 4, 4),
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            icon = image;
        }
        else
        {
            icon = HudParts.Glyph(spec.Glyph ?? "", spec.Tone, 16);
        }

        _popScale = new ScaleTransform(1, 1);
        icon.RenderTransform = _popScale;
        icon.RenderTransformOrigin = new Point(0.5, 0.5);
        FrameworkElement left = HudParts.Cell(icon, spec.Image != null ? 30 : 22);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 24 };
        TextBlock text = HudParts.Label(spec.Text, 13, "TextBrush", FontWeights.SemiBold);
        text.MaxWidth = 210;
        row.Children.Add(text);
        if (!string.IsNullOrEmpty(spec.Detail))
        {
            TextBlock detail = HudParts.Label(spec.Detail!, 11, "TextSecondaryBrush");
            detail.Margin = new Thickness(7, 0, 0, 0);
            detail.MaxWidth = 130;
            row.Children.Add(detail);
        }

        bool interactive = false;
        if (!string.IsNullOrEmpty(spec.ButtonText) && spec.ButtonClick != null)
        {
            var button = new Button
            {
                Content = spec.ButtonText,
                Style = (Style)Application.Current.FindResource("AccentButton"),
                Padding = new Thickness(9, 0, 9, 0),
                Height = 20,
                FontSize = 11,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Action click = spec.ButtonClick;
            button.Click += (_, _) => click();
            row.Children.Add(button);
            interactive = true;
        }

        _content = CompactContent.Of(left, row, spec.ExtraHeight, interactive);

        if (spec.Bounce && !AnimationSettings.IsOff)
        {
            AnimationSettings.Configure(_pop, 380, 0.42);
            _pop.Displace(0.25);
            _pop.Target = 1;
            ApplyPop();
            _loop.Start();
        }

        RequestHud();
    }

    private bool OnPop(double dt)
    {
        _pop.Step(dt);
        ApplyPop();
        return !_pop.SettleIfClose(0.002, 0.02);
    }

    private void ApplyPop()
    {
        if (_popScale != null)
        {
            _popScale.ScaleX = _popScale.ScaleY = _pop.Value;
        }
    }
}
