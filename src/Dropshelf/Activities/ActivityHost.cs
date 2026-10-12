using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Dropshelf.Services;

namespace Dropshelf.Activities;

/// <summary>
/// Decides what the collapsed pill shows and which tabs the expanded island has. A HUD that was requested within the last
/// ~1.8 s wins, otherwise the active live activity with the highest priority, otherwise nothing. The two
/// <see cref="CrossfadeHost"/>s (<see cref="LeftView"/> / <see cref="RightView"/>) crossfade between activities; the
/// island window springs the pill to <see cref="Content"/>'s size whenever <see cref="CompactChanged"/> is raised.
/// </summary>
internal sealed class ActivityHost
{
    public static readonly TimeSpan HudDuration = TimeSpan.FromMilliseconds(1800);

    private readonly List<IPillActivity> _activities = new();
    private readonly Dictionary<IPillActivity, FrameworkElement> _expanded = new();
    private readonly DispatcherTimer _hudTimer;
    private IPillActivity? _hud;
    private IPillActivity? _queuedHud;
    private long _queuedAt;
    private bool _expandedOpen;
    private bool _fullscreen;

    public ActivityHost()
    {
        // Leading content hugs the left edge of the island and trailing content the right edge, like the Dynamic Island.
        LeftView = new CrossfadeHost { ChildHorizontalAlignment = HorizontalAlignment.Left, ChildVerticalAlignment = VerticalAlignment.Center };
        RightView = new CrossfadeHost { ChildHorizontalAlignment = HorizontalAlignment.Right, ChildVerticalAlignment = VerticalAlignment.Center };
        BubbleView = new CrossfadeHost { ChildHorizontalAlignment = HorizontalAlignment.Center, ChildVerticalAlignment = VerticalAlignment.Center, Slide = 0 };
        _hudTimer = new DispatcherTimer { Interval = HudDuration };
        _hudTimer.Tick += (_, _) =>
        {
            _hudTimer.Stop();
            _hud = null;
            Reevaluate();
        };
    }

    /// <summary>Hosts the leading content (children are left-aligned).</summary>
    public FrameworkElement LeftView { get; }

    /// <summary>Hosts the trailing content (children are right-aligned).</summary>
    public FrameworkElement RightView { get; }

    /// <summary>Hosts the second live activity's minimal view, for the bubble beside the island.</summary>
    public FrameworkElement BubbleView { get; }

    /// <summary>The live activity in the bubble beside the island, or null.</summary>
    public IPillActivity? Secondary { get; private set; }

    /// <summary>Show a second live activity in a bubble (the "Split island" setting).</summary>
    public bool SplitEnabled
    {
        get => _splitEnabled;
        set
        {
            _splitEnabled = value;
            Reevaluate();
        }
    }

    private bool _splitEnabled = true;
    private FrameworkElement? _bubbleElement;

    /// <summary>The bubble got another activity, or lost it.</summary>
    public event Action? SecondaryChanged;

    public IReadOnlyList<IPillActivity> Activities => _activities;

    /// <summary>What the collapsed pill shows right now, or null.</summary>
    public IPillActivity? Current { get; private set; }

    public CompactContent? Content { get; private set; }

    public bool IsHudShowing => _hud != null && ReferenceEquals(_hud, Current);

    /// <summary>The pill content or its size changed.</summary>
    public event Action? CompactChanged;

    /// <summary>The set of expanded-island tabs changed.</summary>
    public event Action? TabsChanged;

    /// <summary>Activities that currently have an expanded tab, in <see cref="IPillActivity.TabOrder"/>.</summary>
    public IReadOnlyList<IPillActivity> TabActivities =>
        _activities.Where(a => a.HasExpanded && a.TabTitle != null).OrderBy(a => a.TabOrder).ToList();

    public void Register(IPillActivity activity)
    {
        if (_activities.Contains(activity))
        {
            return;
        }

        _activities.Add(activity);
        activity.StateChanged += OnStateChanged;
        activity.HudRequested += OnHudRequested;
        OnStateChanged(activity, EventArgs.Empty);
    }

    /// <summary>The island is expanded: HUDs stay away (the panel covers them) and are queued if they asked to be.</summary>
    public void SetExpandedOpen(bool open)
    {
        if (_expandedOpen == open)
        {
            return;
        }

        _expandedOpen = open;
        if (open)
        {
            EndHud();
        }
        else if (_queuedHud != null)
        {
            // Something finished while the island was open ("Converted 3 files"): say so now that the pill is visible again.
            IPillActivity queued = _queuedHud;
            bool fresh = Environment.TickCount64 - _queuedAt < 15_000;
            _queuedHud = null;
            if (fresh)
            {
                OnHudRequested(queued, EventArgs.Empty);
            }
        }
    }

    /// <summary>A fullscreen app hides the pill: only HUDs that opt in (volume, if the user wants it) may appear.</summary>
    public void SetFullscreen(bool fullscreen)
    {
        if (_fullscreen == fullscreen)
        {
            return;
        }

        _fullscreen = fullscreen;
        if (fullscreen && _hud is { ShowInFullscreen: false })
        {
            EndHud();
        }
    }

    /// <summary>
    /// Is the island on screen for <paramref name="activity"/> (not slid away for a fullscreen app)? The native Windows popups
    /// are hidden whenever it is, also while the island is open, so they never show next to it.
    /// </summary>
    public bool IslandVisibleFor(IPillActivity activity) => !(_fullscreen && !activity.ShowInFullscreen);

    /// <summary>Would a HUD request from <paramref name="activity"/> be shown right now?</summary>
    public bool CanShowHud(IPillActivity activity) => !_expandedOpen && !(_fullscreen && !activity.ShowInFullscreen);

    public FrameworkElement? GetExpanded(IPillActivity activity)
    {
        if (_expanded.TryGetValue(activity, out FrameworkElement? element))
        {
            return element;
        }

        try
        {
            element = activity.CreateExpanded();
        }
        catch (Exception ex)
        {
            Log.Error($"Activity '{activity.Id}' failed to create its expanded view", ex);
            return null;
        }

        if (element != null)
        {
            _expanded[activity] = element;
        }

        return element;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        TabsChanged?.Invoke();
        Reevaluate();
    }

    private void OnHudRequested(object? sender, EventArgs e)
    {
        if (sender is not IPillActivity activity)
        {
            return;
        }

        if (!CanShowHud(activity))
        {
            if (_expandedOpen && activity.QueueHudWhileExpanded)
            {
                _queuedHud = activity;
                _queuedAt = Environment.TickCount64;
            }

            return;
        }

        _hud = activity;
        _hudTimer.Stop();
        _hudTimer.Interval = activity.HudDuration;
        _hudTimer.Start();
        Reevaluate();
    }

    /// <summary>An interactive HUD is under the pointer: restart its timer so it does not vanish under the cursor.</summary>
    public void KeepHudAlive()
    {
        if (_hud != null && _hudTimer.IsEnabled)
        {
            _hudTimer.Stop();
            _hudTimer.Start();
        }
    }

    /// <summary>Ends <paramref name="activity"/>'s HUD right now (a click on it), if it is the one showing.</summary>
    public void DismissHud(IPillActivity activity)
    {
        if (ReferenceEquals(_hud, activity))
        {
            EndHud();
        }
    }

    private void EndHud()
    {
        _hudTimer.Stop();
        if (_hud != null)
        {
            _hud = null;
            Reevaluate();
        }
    }

    /// <summary>
    /// The bubble shows the most important live activity after the one that has the island (a HUD on the island does not
    /// push it out), if it has a minimal view.
    /// </summary>
    private void UpdateSecondary(List<IPillActivity> lives)
    {
        IPillActivity? secondary = null;
        FrameworkElement? element = null;
        if (_splitEnabled && lives.Count > 1)
        {
            IPillActivity primary = _hud == null && Current is { Kind: PillActivityKind.Live } current && lives.Contains(current) ? current : lives[0];
            foreach (IPillActivity candidate in lives)
            {
                if (ReferenceEquals(candidate, primary))
                {
                    continue;
                }

                try
                {
                    element = candidate.CreateMinimal();
                }
                catch (Exception ex)
                {
                    Log.Error($"Activity '{candidate.Id}' failed to create its minimal view", ex);
                    element = null;
                }

                if (element != null)
                {
                    secondary = candidate;
                    break;
                }
            }
        }

        if (ReferenceEquals(secondary, Secondary) && ReferenceEquals(element, _bubbleElement))
        {
            return;
        }

        Secondary = secondary;
        _bubbleElement = element;
        ((CrossfadeHost)BubbleView).Show(element);
        SecondaryChanged?.Invoke();
    }

    private void Reevaluate()
    {
        var lives = _activities.Where(a => a.Kind == PillActivityKind.Live && a.IsActive).OrderByDescending(a => a.Priority).ToList();
        UpdateSecondary(lives);
        IPillActivity? pick = _hud ?? lives.FirstOrDefault();

        CompactContent? content = null;
        if (pick != null)
        {
            try
            {
                content = pick.CreateCompact();
            }
            catch (Exception ex)
            {
                Log.Error($"Activity '{pick.Id}' failed to create its compact view", ex);
            }

            if (content == null)
            {
                pick = null;
            }
        }

        if (ReferenceEquals(pick, Current) && (content?.SameAs(Content) ?? Content == null))
        {
            return;
        }

        Current = pick;
        Content = content;
        UpdateSecondary(lives);
        ((CrossfadeHost)LeftView).Show(content?.Left);
        ((CrossfadeHost)RightView).Show(content?.Right);
        CompactChanged?.Invoke();
    }
}
