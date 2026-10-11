using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Dropshelf.Activities;
using Dropshelf.Activities.Notes;
using Dropshelf.Animation;
using Dropshelf.Interop;
using Dropshelf.Services;

namespace Dropshelf.Island;

/// <summary>
/// The island: a black notch flush with the top edge of the primary screen. Collapsed it is a small notch, or shows a live
/// activity (now playing, timer, a HUD) with a leading and a trailing view like the Dynamic Island; on hover it springs open
/// into a panel with one tab per activity.
/// <para>
/// Performance: the window is layered (per-pixel alpha), and resizing a layered WPF window reallocates its surfaces, so the
/// window is <b>not</b> resized every frame. It is grown once to fit an animation (with room for the overshoot), the island
/// is sized inside it, and the window shrinks back to the island once the springs settle. Pixels outside the island are
/// fully transparent, which a layered window lets clicks through. While collapsed the island itself is click-through too
/// (WS_EX_TRANSPARENT) so it never swallows clicks on tab strips and title bars at the top centre of other apps; a cheap
/// timer polls the cursor to expand on hover.
/// </para>
/// </summary>
public partial class IslandWindow : Window
{
    private const double CollapsedWidth = 184, CollapsedHeight = 32, CollapsedRadius = 11;
    private const double ExpandedWidth = 640, ExpandedHeight = 188, ExpandedRadius = 30;
    private const double Shoulder = 6;            // concave corners on both sides of the top edge
    private const double CompactGap = 40;         // empty middle between the leading and trailing views
    private const double CompactPad = 12;         // space between a compact view and the island edge
    private const double Overshoot = 1.06;        // window headroom for the opening spring's overshoot
    private const int HoverPollMs = 60;
    private const int HoverIdlePollMs = 250;      // collapsed, cursor far from the island
    private const double HoverNearDip = 120;      // closer than this keeps the fast poll
    private const int HoverDelayMs = 120;

    private readonly SettingsService _settings;
    private readonly Action _openSettings;
    private readonly ActivityHost _activities;
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _collapseTimer;

    private IntPtr _hwnd;
    private MonitorInfo _monitor = MonitorInfo.Primary();
    private bool _expanded;

    // Spring-driven state. _progress: 0 = collapsed, 1 = expanded (overshoots a little when opening).
    private readonly Spring _progress = new(0, 340, 0.78);
    private readonly Spring _collapsedW = new(CollapsedWidth, 420, 0.8);   // collapsed size: grows for activities
    private readonly Spring _collapsedH = new(CollapsedHeight, 420, 0.8);
    private readonly Spring _compact = new(0, 420, 1.0);                   // 0 = idle notch, 1 = activity content
    private readonly Spring _hide = new(0, 380, 1.0);                      // 1 = slid away above the screen (fullscreen app)
    private readonly FrameLoop _loop;

    // Last values pushed to Win32 / WPF, so unchanged frames cost nothing.
    private int _winX = int.MinValue, _winY = int.MinValue, _winW, _winH;
    private double _lastW = -1, _lastH = -1, _lastR = -1;

    private long _holdUntilTick;       // keeps a tray / hotkey expansion open for a moment
    private long _hoverSince;          // tick when the cursor started resting on the collapsed island, 0 = not hovering
    private string? _tab;
    private string _tabKey = string.Empty;
    private bool _fullscreen;
    private bool _hiddenNative;
    private bool _wheelEnabled;
    private int _wheelX = int.MinValue, _wheelY, _wheelW, _wheelH;
    private bool _wheelPublished;
    private bool _activationAllowed;   // WS_EX_NOACTIVATE is off while a text box in the island needs the keyboard
    private IntPtr _previousForeground;

    internal IslandWindow(SettingsService settings, Action openSettings, ActivityHost activities)
    {
        InitializeComponent();
        _settings = settings;
        _openSettings = openSettings;
        _activities = activities;
        _loop = new FrameLoop(OnFrame);
        foreach (NotesActivity notes in _activities.Activities.OfType<NotesActivity>())
        {
            notes.ReleaseRequested += ReleaseKeyboard;
        }

        // Activities: leading / trailing views on the collapsed island, tabs in the expanded panel.
        Grid.SetColumn(_activities.LeftView, 0);
        Grid.SetColumn(_activities.RightView, 2);
        _activities.LeftView.Margin = new Thickness(CompactPad, 0, 0, 0);
        _activities.RightView.Margin = new Thickness(0, 0, CompactPad, 0);
        CompactLayer.Children.Add(_activities.LeftView);
        CompactLayer.Children.Add(_activities.RightView);
        CenterGap.Width = new GridLength(CompactGap);
        Tabs.TabClicked += id => SelectTab(id);
        _activities.CompactChanged += OnCompactChanged;
        _activities.TabsChanged += RebuildTabs;
        RebuildTabs();

        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(HoverPollMs) };
        _hoverTimer.Tick += OnHoverTick;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _collapseTimer.Tick += OnCollapseTick;

        // Hover-expand is done by OnHoverTick: the collapsed island is click-through and gets no mouse events.
        MouseEnter += (_, _) => _collapseTimer.Stop();
        MouseLeave += (_, _) => ScheduleCollapse();

        // A click on the collapsed island expands it (the only way in when hover-expand is off).
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            // A button on an interactive HUD ("Copy text") is a click on that button, not a request to open.
            if (!_expanded && FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource as DependencyObject) == null)
            {
                Expand();
            }
        };

        // A click into a text box (notes, custom timer) lets the no-activate window take the keyboard.
        PreviewMouseDown += OnPreviewMouseDown;
        Deactivated += (_, _) =>
        {
            RestoreNoActivate();
            ScheduleCollapse();
        };

        DragEnter += OnDragOverOrEnter;
        DragOver += OnDragOverOrEnter;
        DragLeave += (_, _) =>
        {
            TabDropHighlight.Visibility = Visibility.Collapsed;
            ScheduleCollapse();
        };
        Drop += OnDrop;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowHelper.MakeToolWindow(_hwnd, noActivate: true);
        WindowHelper.SetClickThrough(_hwnd, true);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        OnCompactChanged();
        ApplyLayout(settled: true);
        _hoverTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _activities.CompactChanged -= OnCompactChanged;
        _activities.TabsChanged -= RebuildTabs;
        PublishWheelRegion(null);
        _hoverTimer.Stop();
        _collapseTimer.Stop();
        _loop.Stop();

        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_DPICHANGED)
        {
            // Re-anchor after WPF has handled the change.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _monitor = MonitorInfo.Primary();
                _winX = int.MinValue; // force a placement and a resize: Windows may have resized the window itself
                _winW = 0;
                ApplyLayout(settled: !_loop.IsRunning);
            }));
        }

        return IntPtr.Zero;
    }

    // ---- Expand / collapse ----------------------------------------------------------------------

    /// <summary>Expands the island and keeps it open for a few seconds even if the mouse is elsewhere (tray / menu use).</summary>
    public void ShowExpanded()
    {
        _holdUntilTick = Environment.TickCount64 + 4000;
        Expand();
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void Expand(string? tab = null)
    {
        _hoverTimer.Stop();
        _hoverSince = 0;
        _collapseTimer.Stop();
        if (_expanded)
        {
            if (tab != null)
            {
                SelectTab(tab);
            }

            return;
        }

        _expanded = true;
        _activities.SetExpandedOpen(true);
        PublishWheelRegion(null);
        _monitor = MonitorInfo.Primary();
        WindowHelper.SetClickThrough(_hwnd, false);

        // Like the Dynamic Island: opening shows the activity that is live on the island (the song, the timer), unless a
        // specific tab was asked for; otherwise the tab that was open last time.
        string? pick = tab ?? (_activities.Current is { } live && _activities.TabActivities.Contains(live) ? live.Id : _tab);
        SelectTab(pick, instant: true);

        ExpandedPanel.Visibility = Visibility.Visible; // now, not on the next frame: Ctrl+Shift+N focuses the editor right away
        ExpandedPanel.IsHitTestVisible = true;
        AnimateTo(1);
    }

    private void Collapse()
    {
        _collapseTimer.Stop();
        if (!_expanded)
        {
            return;
        }

        _expanded = false;
        Keyboard.ClearFocus();
        RestoreNoActivate();
        _activities.SetExpandedOpen(false);
        ExpandedPanel.IsHitTestVisible = false;
        TabDropHighlight.Visibility = Visibility.Collapsed;
        AnimateTo(0);

        // Back to the click-through notch; OnHoverTick makes it solid again where needed.
        WindowHelper.SetClickThrough(_hwnd, true);
        _hoverSince = 0;
        _hoverTimer.Start();
    }

    /// <summary>
    /// Runs while the island is collapsed. It is click-through unless hover-expand is off (or an interactive HUD shows) and the
    /// cursor is over it, so it can be clicked. With hover-expand on, resting the cursor on it for a moment expands it.
    /// </summary>
    private void OnHoverTick(object? sender, EventArgs e)
    {
        if (_expanded)
        {
            _hoverTimer.Stop();
            return;
        }

        if (IsSlidAway)
        {
            WindowHelper.SetClickThrough(_hwnd, true); // a fullscreen app is in front: never catch input
            _hoverSince = 0;
            _hoverTimer.Stop(); // UpdateSlide restarts it when the island comes back
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        bool leftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        bool hoverExpand = _settings.Current.ExpandOnHover;
        bool onIsland = IslandContains(cursor, 0);

        // An interactive HUD (screenshot "Copy text") needs the mouse: solid under the cursor, no hover-expand, and it stays up.
        bool interactiveHud = _activities.IsHudShowing && _activities.Content is { Interactive: true };
        bool solid = onIsland && (!hoverExpand || interactiveHud);
        WindowHelper.SetClickThrough(_hwnd, !solid);
        if (interactiveHud && onIsland)
        {
            _activities.KeepHudAlive();
        }

        // Not while the left button is held: dragging a window or selecting text across the top must not pop it open.
        if (hoverExpand && onIsland && !leftDown && !interactiveHud)
        {
            long now = Environment.TickCount64;
            if (_hoverSince == 0)
            {
                _hoverSince = now;
            }
            else if (now - _hoverSince >= HoverDelayMs)
            {
                Expand();
                return;
            }
        }
        else
        {
            _hoverSince = 0;
        }

        // Poll fast while the cursor is near, slowly otherwise.
        bool near = _hoverSince != 0 || IslandContains(cursor, (int)Math.Round(HoverNearDip * _monitor.Scale));
        var interval = TimeSpan.FromMilliseconds(near ? HoverPollMs : HoverIdlePollMs);
        if (_hoverTimer.Interval != interval)
        {
            _hoverTimer.Interval = interval;
        }
    }

    private void ScheduleCollapse()
    {
        if (_expanded)
        {
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }
    }

    private void OnCollapseTick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        if (_activationAllowed && IsActive && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
        {
            _collapseTimer.Start(); // typing in a note: the island stays open for as long as the text box has the keyboard
            return;
        }

        bool hovered = NativeMethods.GetCursorPos(out var cursor) && IslandContains(cursor, 0);
        if (hovered || Environment.TickCount64 < _holdUntilTick || Mouse.Captured != null)
        {
            // Still hovered, held open or a control has the mouse (a slider being dragged): look again shortly.
            _collapseTimer.Start();
            return;
        }

        Collapse();
    }

    // ---- Animation ------------------------------------------------------------------------------

    /// <summary>
    /// Springs the island open or closed. Opening is a soft, slightly bouncy spring like the Dynamic Island; closing is a bit
    /// quicker and critically damped. Width, height and corner radius all follow the same value.
    /// </summary>
    private void AnimateTo(double target)
    {
        if (AnimationSettings.IsOff)
        {
            _progress.Snap(target);
            ApplyLayout(settled: true);
            return;
        }

        if (target > 0.5)
        {
            AnimationSettings.Configure(_progress, 340, 0.78);
        }
        else
        {
            AnimationSettings.Configure(_progress, 520, 1.0);
        }

        _progress.Target = target;
        _loop.Start();
    }

    private bool OnFrame(double dt)
    {
        _progress.Step(dt);
        _collapsedW.Step(dt);
        _collapsedH.Step(dt);
        _compact.Step(dt);
        _hide.Step(dt);

        bool settled = _progress.SettleIfClose(0.0005, 0.01) & _collapsedW.SettleIfClose(0.05, 0.5)
                       & _collapsedH.SettleIfClose(0.05, 0.5) & _compact.SettleIfClose(0.002, 0.02)
                       & _hide.SettleIfClose(0.002, 0.02);
        ApplyLayout(settled);
        if (settled)
        {
            OnSettled();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Pushes the spring state into the island's size, corner radius and layers, and places the window. With
    /// <paramref name="settled"/> the window is fitted exactly to the island; otherwise it only ever grows (to the animation's
    /// end size plus overshoot), so a running animation costs no window resizes.
    /// </summary>
    private void ApplyLayout(bool settled)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        double p = Math.Clamp(_progress.Value, 0, 1.2);
        double cw = _collapsedW.Value, ch = _collapsedH.Value;
        double w = Math.Max(1, Lerp(cw, ExpandedWidth, p));
        double h = Math.Max(1, Lerp(ch, ExpandedHeight, p));
        double r = Math.Clamp(Lerp(CollapsedRadius, ExpandedRadius, p), 0, h / 2);

        if (Math.Abs(w - _lastW) > 0.01 || Math.Abs(h - _lastH) > 0.01)
        {
            _lastW = w;
            _lastH = h;
            Island.Width = w + 2 * Shoulder;
            Island.Height = h;
        }

        if (Math.Abs(r - _lastR) > 0.01)
        {
            _lastR = r;
            Body.CornerRadius = new CornerRadius(0, 0, r, r);
        }

        // A layer that cannot be seen is hidden, not just transparent: its views (the waveform, the Now Playing scrubber)
        // stop their timers when they are not visible, so nothing repaints the window for content nobody can see.
        double compact = Math.Clamp(_compact.Value, 0, 1);
        double opening = Math.Clamp(p / 0.3, 0, 1);
        double compactOpacity = (1 - opening) * compact;
        CompactLayer.Opacity = compactOpacity;
        CompactLayer.Visibility = compactOpacity > 0.001 ? Visibility.Visible : Visibility.Hidden;
        CompactLayer.Width = Math.Max(1, cw);
        CompactLayer.Height = Math.Max(1, ch);
        double panel = Math.Clamp((p - 0.35) / 0.5, 0, 1);
        ExpandedPanel.Opacity = panel;
        ExpandedPanel.Visibility = _expanded || panel > 0.001 ? Visibility.Visible : Visibility.Hidden;
        double zoom = Lerp(0.92, 1, Math.Clamp(p, 0, 1));
        ExpandedScale.ScaleX = zoom;
        ExpandedScale.ScaleY = zoom;

        // Window: exactly the island when settled; while animating, big enough for where the springs are heading.
        double scale = _monitor.Scale;
        int needW = (int)Math.Ceiling((w + 2 * Shoulder) * scale);
        int needH = (int)Math.Ceiling(h * scale);
        int winW = _winW, winH = _winH;
        if (settled)
        {
            winW = needW;
            winH = needH;
        }
        else if (needW > winW || needH > winH)
        {
            double t = Math.Max(_progress.Target, Math.Clamp(_progress.Value, 0, 1));
            double headW = Math.Max(Lerp(_collapsedW.Target, ExpandedWidth, t), w) * (t > 0.5 ? Overshoot : 1.04);
            double headH = Math.Max(Lerp(_collapsedH.Target, ExpandedHeight, t), h) * (t > 0.5 ? Overshoot : 1.04);
            winW = Math.Max(needW, (int)Math.Ceiling((headW + 2 * Shoulder) * scale));
            winH = Math.Max(needH, (int)Math.Ceiling(headH * scale));
        }

        int x = _monitor.Left + (_monitor.Width - winW) / 2;
        double hide = Math.Clamp(_hide.Value, 0, 1);
        int y = _monitor.Top - (int)Math.Round(needH * hide); // flush with the top edge; slides away for fullscreen apps
        if (x != _winX || y != _winY || winW != _winW || winH != _winH)
        {
            bool sized = winW != _winW || winH != _winH;
            _winX = x;
            _winY = y;
            _winW = winW;
            _winH = winH;
            if (sized)
            {
                WindowHelper.SetBoundsTopmost(_hwnd, x, y, winW, winH);
            }
            else
            {
                WindowHelper.MoveTopmost(_hwnd, x, y);
            }
        }

        Island.Opacity = 1 - hide;
        PublishWheelRegion(hide < 0.5 && !_expanded ? IslandRect() : null);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>The island's bounds on screen in physical pixels (the notch body, without the shoulders).</summary>
    private NativeMethods.RECT IslandRect()
    {
        double scale = _monitor.Scale;
        int w = (int)Math.Round(Math.Max(1, _lastW) * scale);
        int h = (int)Math.Round(Math.Max(1, _lastH) * scale);
        int left = _monitor.Left + (_monitor.Width - w) / 2;
        int top = _winY == int.MinValue ? _monitor.Top : _winY;
        return new NativeMethods.RECT { Left = left, Top = top, Right = left + w, Bottom = top + h };
    }

    private bool IslandContains(NativeMethods.POINT p, int margin)
    {
        NativeMethods.RECT r = IslandRect();
        return p.X >= r.Left - margin && p.X < r.Right + margin && p.Y >= r.Top - margin && p.Y < r.Bottom + margin;
    }

    /// <summary>The animation setting changed: jump to the final state when animations are off.</summary>
    public void OnAnimationModeChanged()
    {
        if (AnimationSettings.IsOff)
        {
            _loop.Stop();
            _progress.Snap(_expanded ? 1 : 0);
            _collapsedW.Snap(_collapsedW.Target);
            _collapsedH.Snap(_collapsedH.Target);
            _compact.Snap(_compact.Target);
            _hide.Snap(_hide.Target);
            ApplyLayout(settled: true);
            OnSettled();
        }
    }

    // ---- Drops onto a tab (text onto Notes) ---------------------------------------------------------

    private void OnDragOverOrEnter(object sender, DragEventArgs e)
    {
        bool accept = _expanded && !DragState.IsActive && CurrentDropTarget() is { } target && target.CanDrop(e.Data);
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        TabDropHighlight.Visibility = accept ? Visibility.Visible : Visibility.Collapsed;
        if (_expanded)
        {
            _collapseTimer.Stop();
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        TabDropHighlight.Visibility = Visibility.Collapsed;
        bool taken = _expanded && !DragState.IsActive && CurrentDropTarget() is { } target && target.CanDrop(e.Data) && target.Drop(e.Data);
        e.Effects = taken ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        ScheduleCollapse();
    }

    /// <summary>The expanded tab that accepts dropped content itself (the Notes tab takes text), or null.</summary>
    private IIslandDropTarget? CurrentDropTarget() =>
        _tab == null ? null : _activities.TabActivities.FirstOrDefault(a => a.Id == _tab) as IIslandDropTarget;

    // ---- Keyboard input (notes, custom timer) ------------------------------------------------------

    /// <summary>
    /// The island is a no-activate window so it never steals focus from the app you are working in. A click into one of its text
    /// boxes is the exception: it turns WS_EX_NOACTIVATE off, activates the window and gives the box the keyboard. The flag comes
    /// back when the window is deactivated or collapses, and focus returns to the window that had it before.
    /// </summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_expanded || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var box = FindAncestor<System.Windows.Controls.Primitives.TextBoxBase>(source);
        if (box == null)
        {
            return;
        }

        AllowActivation();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Keyboard.Focus(box)));
    }

    /// <summary>Lets the window take the keyboard: removes WS_EX_NOACTIVATE and brings it to the foreground.</summary>
    private void AllowActivation()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!_activationAllowed)
        {
            _activationAllowed = true;
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            _previousForeground = foreground == _hwnd ? IntPtr.Zero : foreground;
            WindowHelper.SetNoActivate(_hwnd, false);
        }

        _collapseTimer.Stop();
        Activate();
        if (!IsActive)
        {
            WindowHelper.ForceForeground(_hwnd);
        }
    }

    /// <summary>Puts WS_EX_NOACTIVATE back. If the island still has the foreground, it goes back to the window it came from.</summary>
    private void RestoreNoActivate()
    {
        if (!_activationAllowed)
        {
            return;
        }

        _activationAllowed = false;
        WindowHelper.SetNoActivate(_hwnd, true);
        IntPtr previous = _previousForeground;
        _previousForeground = IntPtr.Zero;
        if (previous != IntPtr.Zero && NativeMethods.GetForegroundWindow() == _hwnd && NativeMethods.IsWindow(previous))
        {
            WindowHelper.ForceForeground(previous);
        }
    }

    /// <summary>Esc in a text box: give the keyboard back; the island closes when the pointer is elsewhere.</summary>
    private void ReleaseKeyboard()
    {
        Keyboard.ClearFocus();
        RestoreNoActivate();
        ScheduleCollapse();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match)
            {
                return match;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>Ctrl+Shift+N: opens the island on the Notes tab with the caret in the editor.</summary>
    public void ShowNotes()
    {
        NotesActivity? notes = _activities.Activities.OfType<NotesActivity>().FirstOrDefault();
        if (notes == null)
        {
            ShowExpanded();
            return;
        }

        _holdUntilTick = Environment.TickCount64 + 4000;
        Expand(notes.Id);
        AllowActivation();
        notes.FocusEditor();

        // AllowActivation stopped the collapse timer. If the foreground change was refused the editor never gets the keyboard, so
        // restart it: it keeps the island open while the editor is typed in or until _holdUntilTick, then lets it close.
        ScheduleCollapse();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _openSettings();

    // ---- Tabs ---------------------------------------------------------------------------------------

    /// <summary>One tab per activity that currently has an expanded view.</summary>
    private void RebuildTabs()
    {
        var tabs = _activities.TabActivities.Select(a => (a.Id, a.TabTitle!)).ToList();
        string key = string.Join('|', tabs.Select(t => t.Id));
        if (key != _tabKey)
        {
            _tabKey = key;
            Tabs.SetTabs(tabs);
        }

        if (_tab == null || tabs.All(t => t.Id != _tab))
        {
            SelectTab(null, instant: !_expanded); // the activity behind the current tab went away (playback ended...)
        }
    }

    /// <summary>Shows <paramref name="id"/>'s page, or the first tab when that one does not exist (null = first).</summary>
    private void SelectTab(string? id, bool instant = false)
    {
        IReadOnlyList<IPillActivity> tabs = _activities.TabActivities;
        IPillActivity? activity = tabs.FirstOrDefault(a => a.Id == id) ?? tabs.FirstOrDefault();
        FrameworkElement? page = activity == null ? null : _activities.GetExpanded(activity);

        _tab = page == null ? null : activity!.Id;
        EmptyText.Visibility = page == null ? Visibility.Visible : Visibility.Collapsed;
        if (_tab != null)
        {
            Tabs.Select(_tab, instant);
        }

        if (Pages.Current != page)
        {
            Pages.Show(page, instant || !_expanded);
        }
    }

    /// <summary>All springs are at rest: an island that slid away leaves the screen.</summary>
    private void OnSettled()
    {
        if (_hide.Value >= 0.999 && _hide.Target >= 1 && !_hiddenNative)
        {
            _hiddenNative = true;
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE); // off-screen is not enough with a monitor above
            Island.Visibility = Visibility.Hidden;                // and WPF would keep animating what it thinks is visible
        }
    }

    // ---- Activities, fullscreen ---------------------------------------------------------------------

    private bool IsSlidAway => _hide.Target >= 1;

    /// <summary>Sizes the collapsed island to the current activity content (or back to the idle notch) with springs.</summary>
    private void OnCompactChanged()
    {
        CompactContent? content = _activities.Content;
        double targetW = CollapsedWidth, targetH = CollapsedHeight;
        if (content != null)
        {
            double side = Math.Max(content.LeftWidth, content.RightWidth) + CompactPad;
            targetW = Math.Max(CollapsedWidth, CompactGap + 2 * side);
            targetH = CollapsedHeight + content.ExtraHeight;
        }

        _collapsedW.Target = targetW;
        _collapsedH.Target = targetH;
        _compact.Target = content != null ? 1 : 0;
        CompactLayer.IsHitTestVisible = content is { Interactive: true };

        if (AnimationSettings.IsOff || _hwnd == IntPtr.Zero)
        {
            _collapsedW.Snap(targetW);
            _collapsedH.Snap(targetH);
            _compact.Snap(_compact.Target);
            ApplyLayout(settled: !_loop.IsRunning);
        }
        else
        {
            AnimationSettings.Configure(_collapsedW, 420, 0.8); // a touch of overshoot: the island "grows"
            AnimationSettings.Configure(_collapsedH, 420, 0.8);
            AnimationSettings.Configure(_compact, 420, 1.0);
            _loop.Start();
        }

        UpdateSlide();
    }

    /// <summary>A fullscreen app is (or is no longer) in front: slide the island away, unless a HUD that may show over it is up.</summary>
    public void SetFullscreen(bool fullscreen)
    {
        _fullscreen = fullscreen;
        if (fullscreen && _expanded)
        {
            Collapse();
        }

        UpdateSlide();
    }

    private void UpdateSlide()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        bool away = _fullscreen && !_activities.IsHudShowing;
        double target = away ? 1 : 0;
        if (Math.Abs(_hide.Target - target) < 0.5)
        {
            return;
        }

        if (!away && _hiddenNative)
        {
            _hiddenNative = false;
            Island.Visibility = Visibility.Visible;
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        }

        _hide.Target = target;
        if (away)
        {
            _hoverSince = 0;
            WindowHelper.SetClickThrough(_hwnd, true);
            _hoverTimer.Stop();
        }
        else if (!_expanded)
        {
            _hoverTimer.Start();
        }

        if (AnimationSettings.IsOff)
        {
            _hide.Snap(target);
            ApplyLayout(settled: true);
            OnSettled();
        }
        else
        {
            AnimationSettings.Configure(_hide, 380, 1.0);
            _loop.Start();
        }
    }

    /// <summary>
    /// Where the mouse wheel is captured for the volume scroll (the collapsed island's bounds), or null. Set by the app, which
    /// hands it to the mouse hook.
    /// </summary>
    internal Action<NativeMethods.RECT?>? WheelRegionSink { get; set; }

    /// <summary>Wheel over the collapsed island changes the volume (the setting is on and there is a volume to change).</summary>
    public bool WheelEnabled
    {
        get => _wheelEnabled;
        set
        {
            _wheelEnabled = value;
            PublishWheelRegion(value && !_expanded && _hwnd != IntPtr.Zero ? IslandRect() : null);
        }
    }

    private void PublishWheelRegion(NativeMethods.RECT? region)
    {
        if (!_wheelEnabled || WheelRegionSink == null)
        {
            region = null;
        }

        if (region is { } r)
        {
            if (_wheelPublished && r.Left == _wheelX && r.Top == _wheelY && r.Width == _wheelW && r.Height == _wheelH)
            {
                return;
            }

            _wheelX = r.Left;
            _wheelY = r.Top;
            _wheelW = r.Width;
            _wheelH = r.Height;
            _wheelPublished = true;
            WheelRegionSink!(r);
        }
        else if (_wheelPublished)
        {
            _wheelPublished = false;
            WheelRegionSink?.Invoke(null);
        }
    }
}
