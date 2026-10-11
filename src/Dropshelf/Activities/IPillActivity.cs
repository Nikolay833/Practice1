using System;
using System.Windows;

namespace Dropshelf.Activities;

/// <summary>How an activity uses the collapsed pill.</summary>
internal enum PillActivityKind
{
    /// <summary>Stays on the pill for as long as <see cref="IPillActivity.IsActive"/> (now playing, later a timer...).</summary>
    Live,

    /// <summary>Takes the pill over for a moment when it raises <see cref="IPillActivity.HudRequested"/> (volume, battery...).</summary>
    Hud,
}

/// <summary>
/// What an activity shows around the mascot while the pill is collapsed: one element left of it, one right of it.
/// The pill springs to a width that fits the wider side. The same element instances may be handed out again by later
/// <see cref="IPillActivity.CreateCompact"/> calls (so a live view can keep updating); the host then does not crossfade.
/// </summary>
internal sealed class CompactContent
{
    private CompactContent(FrameworkElement? left, double leftWidth, FrameworkElement? right, double rightWidth, double extraHeight)
    {
        Left = left;
        LeftWidth = leftWidth;
        Right = right;
        RightWidth = rightWidth;
        ExtraHeight = extraHeight;
    }

    public FrameworkElement? Left { get; }
    public FrameworkElement? Right { get; }
    public double LeftWidth { get; }
    public double RightWidth { get; }

    /// <summary>Extra pill height (DIPs) while this is shown; HUDs grow the pill a little.</summary>
    public double ExtraHeight { get; }

    /// <summary>Builds content and measures the elements to get their widths.</summary>
    public static CompactContent Of(FrameworkElement? left, FrameworkElement? right, double extraHeight = 0) =>
        new(left, Measure(left), right, Measure(right), extraHeight);

    private static double Measure(FrameworkElement? element)
    {
        if (element == null)
        {
            return 0;
        }

        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(element.DesiredSize.Width);
    }

    public bool SameAs(CompactContent? other) =>
        other != null && ReferenceEquals(Left, other.Left) && ReferenceEquals(Right, other.Right)
        && Math.Abs(LeftWidth - other.LeftWidth) < 0.5 && Math.Abs(RightWidth - other.RightWidth) < 0.5
        && Math.Abs(ExtraHeight - other.ExtraHeight) < 0.5;
}

/// <summary>
/// Something that can show up on the pill. Register instances with <see cref="ActivityHost"/>. All members are used
/// on the UI thread; an activity that gets events from other threads marshals them itself.
/// </summary>
internal interface IPillActivity
{
    string Id { get; }

    /// <summary>Higher wins when several <see cref="PillActivityKind.Live"/> activities are active.</summary>
    int Priority { get; }

    PillActivityKind Kind { get; }

    /// <summary>For live activities: does it want the pill right now? Always false for HUDs.</summary>
    bool IsActive { get; }

    /// <summary>Title of the tab in the expanded shelf, or null when the activity has no expanded view.</summary>
    string? TabTitle { get; }

    /// <summary>The tab exists right now (the activity is enabled and has something to show).</summary>
    bool HasExpanded { get; }

    /// <summary>HUDs only: may it appear while a fullscreen app hides the pill? (The user can opt volume in.)</summary>
    bool ShowInFullscreen { get; }

    /// <summary>Raised when <see cref="IsActive"/>, <see cref="HasExpanded"/> or the compact content changed.</summary>
    event EventHandler? StateChanged;

    /// <summary>Raised by a HUD to show itself now (again). The host keeps it up for about 1.8 s after the last request.</summary>
    event EventHandler? HudRequested;

    /// <summary>Compact content for the collapsed pill, or null for nothing.</summary>
    CompactContent? CreateCompact();

    /// <summary>The expanded tab's content (created once and cached by the host), or null.</summary>
    FrameworkElement? CreateExpanded();
}

/// <summary>Convenience base class: events, defaults for "no tab", "not active".</summary>
internal abstract class PillActivityBase : IPillActivity
{
    public abstract string Id { get; }
    public abstract int Priority { get; }
    public virtual PillActivityKind Kind => PillActivityKind.Live;
    public virtual bool IsActive => false;
    public virtual string? TabTitle => null;
    public virtual bool HasExpanded => false;
    public virtual bool ShowInFullscreen => false;

    public event EventHandler? StateChanged;
    public event EventHandler? HudRequested;

    public virtual CompactContent? CreateCompact() => null;
    public virtual FrameworkElement? CreateExpanded() => null;

    protected void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    protected void RequestHud() => HudRequested?.Invoke(this, EventArgs.Empty);
}
