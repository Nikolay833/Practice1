using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Dropshelf.Services;

namespace Dropshelf.Activities;

/// <summary>
/// Decides what the collapsed pill shows and which tabs the expanded shelf has. A HUD that was requested within the last
/// ~1.8 s wins, otherwise the active live activity with the highest priority, otherwise nothing. The two
/// <see cref="CrossfadeHost"/>s (<see cref="LeftView"/> / <see cref="RightView"/>) crossfade between activities; the
/// shelf window springs the pill to <see cref="Content"/>'s size whenever <see cref="CompactChanged"/> is raised.
/// </summary>
internal sealed class ActivityHost
{
    public static readonly TimeSpan HudDuration = TimeSpan.FromMilliseconds(1800);

    private readonly List<IPillActivity> _activities = new();
    private readonly Dictionary<IPillActivity, FrameworkElement> _expanded = new();
    private readonly DispatcherTimer _hudTimer;
    private IPillActivity? _hud;
    private bool _expandedOpen;
    private bool _fullscreen;

    public ActivityHost()
    {
        LeftView = new CrossfadeHost { ChildHorizontalAlignment = HorizontalAlignment.Right, ChildVerticalAlignment = VerticalAlignment.Center };
        RightView = new CrossfadeHost { ChildHorizontalAlignment = HorizontalAlignment.Left, ChildVerticalAlignment = VerticalAlignment.Center };
        _hudTimer = new DispatcherTimer { Interval = HudDuration };
        _hudTimer.Tick += (_, _) =>
        {
            _hudTimer.Stop();
            _hud = null;
            Reevaluate();
        };
    }

    /// <summary>Hosts the content left of the mascot (children are right-aligned).</summary>
    public FrameworkElement LeftView { get; }

    /// <summary>Hosts the content right of the mascot (children are left-aligned).</summary>
    public FrameworkElement RightView { get; }

    public IReadOnlyList<IPillActivity> Activities => _activities;

    /// <summary>What the collapsed pill shows right now, or null.</summary>
    public IPillActivity? Current { get; private set; }

    public CompactContent? Content { get; private set; }

    public bool IsHudShowing => _hud != null && ReferenceEquals(_hud, Current);

    /// <summary>The pill content or its size changed.</summary>
    public event Action? CompactChanged;

    /// <summary>The set of expanded-shelf tabs changed.</summary>
    public event Action? TabsChanged;

    /// <summary>Activities that currently have an expanded tab, highest priority first.</summary>
    public IReadOnlyList<IPillActivity> TabActivities =>
        _activities.Where(a => a.HasExpanded && a.TabTitle != null).OrderByDescending(a => a.Priority).ToList();

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

    /// <summary>The expanded shelf is open: HUDs stay away (the shelf covers the pill) and live content is not recomputed.</summary>
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
        if (sender is not IPillActivity activity || !CanShowHud(activity))
        {
            return;
        }

        _hud = activity;
        _hudTimer.Stop();
        _hudTimer.Start();
        Reevaluate();
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

    private void Reevaluate()
    {
        IPillActivity? pick = _hud;
        if (pick == null)
        {
            pick = _activities.Where(a => a.Kind == PillActivityKind.Live && a.IsActive).OrderByDescending(a => a.Priority).FirstOrDefault();
        }

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
        ((CrossfadeHost)LeftView).Show(content?.Left);
        ((CrossfadeHost)RightView).Show(content?.Right);
        CompactChanged?.Invoke();
    }
}
