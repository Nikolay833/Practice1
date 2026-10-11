using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Dropshelf.Animation;
using Dropshelf.Interop;
using Dropshelf.Services;

namespace Dropshelf.Shelf;

/// <summary>
/// The notch pill. Collapsed it is a small black tab flush with the top edge of the primary screen; on hover or
/// when something is dragged over it, it animates open into the shelf panel. The window itself is resized each
/// frame, so the transparent area around the pill never blocks clicks. While collapsed the pill itself is click-through
/// (WS_EX_TRANSPARENT) so it does not swallow clicks on tab strips and title bars at the top centre of other apps;
/// a timer polls the cursor to expand on hover and to make the pill solid again where it must receive drags or clicks.
/// </summary>
public partial class ShelfWindow : Window
{
    private const double CollapsedWidth = 200, CollapsedHeight = 32, CollapsedRadius = 14;
    private const double ExpandedWidth = 640, ExpandedHeight = 180, ExpandedRadius = 24;
    private const double MagnetRangeDip = 170;   // drag magnet and glow start this far from the pill
    private const double MagnetStretchW = 10, MagnetStretchH = 8, MagnetShift = 6;
    private const int HoverPollMs = 60;
    private const int HoverDelayMs = 150;
    private const double DragMarginDip = 16;     // the pill becomes solid this close to the cursor while dragging
    private const int DragStartPx = 8;

    private readonly ShelfStore _store;
    private readonly SettingsService _settings;
    private readonly Action _openSettings;
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _collapseTimer;

    private IntPtr _hwnd;
    private MonitorInfo _monitor = MonitorInfo.Primary();
    private bool _expanded;

    // Spring-driven state. _progress: 0 = collapsed pill, 1 = fully expanded (overshoots about 4 % when opening).
    private readonly Spring _progress = new(0, 600, 0.7156);
    private readonly Spring _stretchW = new(0, 420, 0.8);   // drag magnet: extra width, height and sideways shift (DIPs)
    private readonly Spring _stretchH = new(0, 420, 0.8);
    private readonly Spring _shiftX = new(0, 420, 0.8);
    private readonly Spring _glow = new(0, 140, 0.9);       // 0..1 brightness of the blue bloom / rim
    private readonly FrameLoop _loop;
    private readonly ScaleTransform _mascotScale = new(0.5, 0.5);
    private readonly TranslateTransform _mascotMove = new(16, 5);
    private double _nearness;          // 0..1, how close a drag is to the pill
    private bool _dragOver;            // something droppable is over the pill right now
    private bool _excited;
    private long _lastOpenAnimTick;
    private long _holdUntilTick;       // keeps a tray-triggered expansion open for a moment
    private long _hoverSince;          // tick when the cursor started resting on the collapsed pill, 0 = not hovering
    private bool _leftWasDown;
    private bool _leftDragging;        // left button held and moved since it went down
    private NativeMethods.POINT _leftPress;

    public ShelfWindow(ShelfStore store, SettingsService settings, Action openSettings)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;
        _openSettings = openSettings;
        _loop = new FrameLoop(OnFrame);

        var mascotTransform = new TransformGroup();
        mascotTransform.Children.Add(_mascotScale);
        mascotTransform.Children.Add(_mascotMove);
        Mascot.RenderTransform = mascotTransform;

        ItemsView.Store = store;
        ItemsView.BusyChanged += (_, _) =>
        {
            if (!ItemsView.IsBusy)
            {
                ScheduleCollapse();
            }
        };
        store.Items.CollectionChanged += (_, e) =>
        {
            UpdateCounts();
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                Mascot.Gulp();
            }
        };
        UpdateCounts();

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverPollMs) };
        _hoverTimer.Tick += OnHoverTick;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _collapseTimer.Tick += OnCollapseTick;

        // Hover-expand is done by OnHoverTick: the collapsed pill is click-through and gets no mouse events.
        MouseEnter += (_, _) => _collapseTimer.Stop();
        MouseLeave += (_, _) => ScheduleCollapse();

        // Click on the collapsed pill expands it (the only way in when hover-expand is off).
        PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (!_expanded)
            {
                Expand();
            }
        };

        DragEnter += OnDragOverOrEnter;
        DragOver += OnDragOverOrEnter;
        DragLeave += (_, _) =>
        {
            DropHighlight.Visibility = Visibility.Collapsed;
            SetDragOver(false);
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
        ApplyLayout();
        _hoverTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
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
                ApplyLayout();
            }));
        }

        return IntPtr.Zero;
    }

    // ---- Expand / collapse ----------------------------------------------------------------------

    /// <summary>Expands the shelf and keeps it open for a few seconds even if the mouse is elsewhere (tray / menu use).</summary>
    public void ShowExpanded()
    {
        _holdUntilTick = Environment.TickCount64 + 4000;
        Expand();
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void Expand()
    {
        _hoverTimer.Stop();
        _hoverSince = 0;
        _collapseTimer.Stop();
        if (_expanded)
        {
            return;
        }

        _expanded = true;
        _monitor = MonitorInfo.Primary();
        WindowHelper.SetClickThrough(_hwnd, false);
        _ = PruneMissingAsync(); // drop files that vanished while the shelf was closed, without blocking the UI

        ExpandedPanel.IsHitTestVisible = true;
        _nearness = 0;
        SetMagnet(0, 0);
        UpdateGlowTarget();
        PlayOpenAnimation();
        AnimateTo(1);
    }

    /// <summary>Tiles flip in one after the other (30 ms apart) each time the shelf opens, but not on every quick re-open.</summary>
    private void PlayOpenAnimation()
    {
        long now = Environment.TickCount64;
        if (now - _lastOpenAnimTick < 1200)
        {
            return;
        }

        _lastOpenAnimTick = now;
        ItemsView.PlayOpenAnimation();
    }

    private async System.Threading.Tasks.Task PruneMissingAsync()
    {
        int pruned = await _store.PruneMissingAsync();
        if (pruned > 0)
        {
            Log.Info($"Pruned {pruned} missing shelf item(s)");
        }
    }

    private void Collapse()
    {
        _collapseTimer.Stop();
        if (!_expanded)
        {
            return;
        }

        _expanded = false;
        ExpandedPanel.IsHitTestVisible = false;
        DropHighlight.Visibility = Visibility.Collapsed;
        _dragOver = false;
        UpdateGlowTarget();
        AnimateTo(0);

        // Back to the click-through pill; OnHoverTick makes it solid again where needed.
        WindowHelper.SetClickThrough(_hwnd, true);
        _hoverSince = 0;
        _leftWasDown = false;
        _leftDragging = false;
        _hoverTimer.Start();
    }

    /// <summary>
    /// Runs while the shelf is collapsed. The pill is click-through unless (a) something is being dragged near it, so
    /// OLE drag-over/drop reaches the window, or (b) hover-expand is off and the cursor is over it, so it can be
    /// clicked. With hover-expand on, resting the cursor on it for a moment expands it.
    /// </summary>
    private void OnHoverTick(object? sender, EventArgs e)
    {
        if (_expanded)
        {
            _hoverTimer.Stop();
            return;
        }

        bool leftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        TrackLeftDrag(leftDown);

        bool dragging = DragState.IsActive || _leftDragging;
        bool hoverExpand = _settings.Current.ExpandShelfOnHover;
        bool onPill = WindowHelper.ContainsCursor(_hwnd);
        int dragMargin = (int)Math.Round(DragMarginDip * _monitor.Scale);

        bool solid = (dragging && WindowHelper.ContainsCursor(_hwnd, dragMargin)) || (!hoverExpand && onPill);
        WindowHelper.SetClickThrough(_hwnd, !solid);
        UpdateMagnet(dragging);

        if (hoverExpand && onPill && !leftDown)
        {
            long now = Environment.TickCount64;
            if (_hoverSince == 0)
            {
                _hoverSince = now;
            }
            else if (now - _hoverSince >= HoverDelayMs)
            {
                Expand();
            }
        }
        else
        {
            _hoverSince = 0;
        }
    }

    /// <summary>True drag = left button held and the cursor moved a few pixels since the press (a plain click is not one).</summary>
    private void TrackLeftDrag(bool leftDown)
    {
        if (!leftDown || !NativeMethods.GetCursorPos(out var p))
        {
            _leftWasDown = false;
            _leftDragging = false;
            return;
        }

        if (!_leftWasDown)
        {
            _leftWasDown = true;
            _leftDragging = false;
            _leftPress = p;
        }
        else if (!_leftDragging && (Math.Abs(p.X - _leftPress.X) > DragStartPx || Math.Abs(p.Y - _leftPress.Y) > DragStartPx))
        {
            _leftDragging = true;
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
        if (ItemsView.IsBusy)
        {
            return; // BusyChanged reschedules once the menu closes / drag ends
        }

        if (WindowHelper.ContainsCursor(_hwnd) || Environment.TickCount64 < _holdUntilTick)
        {
            // Still hovered (or held open): look again shortly.
            _collapseTimer.Start();
            return;
        }

        Collapse();
    }

    // ---- Animation ------------------------------------------------------------------------------

    /// <summary>
    /// Springs the pill open or closed. Opening uses an under-damped spring (damping ratio 0.7156 = about 4 % overshoot);
    /// closing is faster and critically damped (no overshoot). Width, height and corner radius all follow the same value.
    /// </summary>
    private void AnimateTo(double target)
    {
        if (AnimationSettings.IsOff)
        {
            _progress.Snap(target);
            ApplyLayout();
            return;
        }

        if (target > 0.5)
        {
            AnimationSettings.Configure(_progress, 600, 0.7156);
        }
        else
        {
            AnimationSettings.Configure(_progress, 900, 1.0);
        }

        _progress.Target = target;
        _loop.Start();
    }

    private bool OnFrame(double dt)
    {
        _progress.Step(dt);
        _stretchW.Step(dt);
        _stretchH.Step(dt);
        _shiftX.Step(dt);
        _glow.Step(dt);
        ApplyLayout();

        bool settled = _progress.SettleIfClose(0.0005, 0.01) & _stretchW.SettleIfClose(0.02, 0.2)
                       & _stretchH.SettleIfClose(0.02, 0.2) & _shiftX.SettleIfClose(0.02, 0.2) & _glow.SettleIfClose(0.002, 0.02);
        if (settled)
        {
            ApplyLayout();
            return false;
        }

        return true;
    }

    /// <summary>Sizes/positions the window for the current spring state and updates the pill visuals.</summary>
    private void ApplyLayout()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        double p = Math.Clamp(_progress.Value, 0, 1.2);
        double w = Lerp(CollapsedWidth, ExpandedWidth, p) + _stretchW.Value;
        double h = Lerp(CollapsedHeight, ExpandedHeight, p) + _stretchH.Value;
        double r = Math.Max(0, Lerp(CollapsedRadius, ExpandedRadius, p));

        int pxW = (int)Math.Round(w * _monitor.Scale);
        int pxH = (int)Math.Round(h * _monitor.Scale);
        int x = _monitor.Left + (_monitor.Width - pxW) / 2 + (int)Math.Round(_shiftX.Value * _monitor.Scale);
        int y = _monitor.Top; // flush with the top edge of the screen
        WindowHelper.SetBoundsTopmost(_hwnd, x, y, pxW, pxH);

        var radius = new CornerRadius(0, 0, r, r);
        Pill.CornerRadius = radius;
        GlowRim.CornerRadius = radius;
        ExpandedPanel.Opacity = Math.Clamp((p - 0.45) / 0.55, 0, 1);
        CollapsedLayer.Opacity = 1 - Math.Clamp(p / 0.35, 0, 1);

        double glow = Math.Clamp(_glow.Value, 0, 1);
        GlowRim.Opacity = glow;
        GlowBloom.Opacity = glow * 0.85;

        // The mascot grows from 22 px (collapsed) to about 40 px (expanded) and moves with the pill.
        double m = Math.Clamp(p, 0, 1.06);
        double scale = Lerp(0.5, 0.9, m);
        _mascotScale.ScaleX = scale;
        _mascotScale.ScaleY = scale;
        _mascotMove.X = Lerp(16, 14, m);
        _mascotMove.Y = Lerp(5, 8, m);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>The animation setting changed: refresh the mascot and jump to the final state when animations are off.</summary>
    public void OnAnimationModeChanged()
    {
        Mascot.Refresh();
        if (AnimationSettings.IsOff)
        {
            _progress.Snap(_expanded ? 1 : 0);
            SetMagnet(0, 0);
            _stretchW.Snap(0);
            _stretchH.Snap(0);
            _shiftX.Snap(0);
            _glow.Snap(_glow.Target);
            ApplyLayout();
        }
        else
        {
            UpdateGlowTarget();
        }
    }

    // ---- Drag magnet and glow -----------------------------------------------------------------------

    /// <summary>
    /// While something is being dragged toward the collapsed pill it stretches a few pixels toward the cursor (wider,
    /// taller, shifted sideways) and glows brighter the closer the cursor is. Only the glow in Subtle; nothing in Off.
    /// </summary>
    private void UpdateMagnet(bool dragging)
    {
        double nearness = 0;
        double shift = 0;
        if (dragging && !AnimationSettings.IsOff && NativeMethods.GetCursorPos(out var p) && NativeMethods.GetWindowRect(_hwnd, out var r))
        {
            double scale = _monitor.Scale;
            double dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
            double dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
            double distance = Math.Sqrt(dx * dx + dy * dy) / scale;
            nearness = Math.Clamp(1 - distance / MagnetRangeDip, 0, 1);
            double centerX = (r.Left + r.Right) / 2.0;
            shift = Math.Clamp((p.X - centerX) / (MagnetRangeDip * scale), -1, 1) * MagnetShift * nearness;
        }

        bool changed = Math.Abs(nearness - _nearness) > 0.01;
        _nearness = nearness;
        if (AnimationSettings.IsFull)
        {
            SetMagnet(MagnetStretchW * nearness, MagnetStretchH * nearness, shift);
        }
        else
        {
            SetMagnet(0, 0);
        }

        if (changed)
        {
            UpdateGlowTarget();
        }
    }

    private void SetMagnet(double stretchW, double stretchH, double shiftX = 0)
    {
        if (Math.Abs(_stretchW.Target - stretchW) < 0.02 && Math.Abs(_stretchH.Target - stretchH) < 0.02
            && Math.Abs(_shiftX.Target - shiftX) < 0.02)
        {
            return;
        }

        _stretchW.Target = stretchW;
        _stretchH.Target = stretchH;
        _shiftX.Target = shiftX;
        _loop.Start();
    }

    private void SetDragOver(bool value)
    {
        if (_dragOver != value)
        {
            _dragOver = value;
            UpdateGlowTarget();
        }
    }

    /// <summary>Glow target: dim while open, full while a drag hovers, scaled by how near a drag is. Also wakes the mascot.</summary>
    private void UpdateGlowTarget()
    {
        double target = Math.Max(_expanded ? 0.5 : 0, Math.Max(_dragOver ? 1 : 0, _nearness));
        if (AnimationSettings.IsOff)
        {
            _glow.Snap(target);
            ApplyLayout();
        }
        else if (Math.Abs(_glow.Target - target) > 0.005)
        {
            AnimationSettings.Configure(_glow, 140, 0.9);
            _glow.Target = target;
            _loop.Start();
        }

        // Hysteresis so the mascot does not flicker between excited and calm at the edge of the range.
        bool excited = _dragOver || _nearness > (_excited ? 0.1 : 0.25);
        if (excited != _excited)
        {
            _excited = excited;
            Mascot.SetExcited(excited);
        }
    }

    // ---- Drag and drop onto the shelf -------------------------------------------------------------

    private void OnDragOverOrEnter(object sender, DragEventArgs e)
    {
        _collapseTimer.Stop();

        bool fromThisShelf = DragState.IsActive && ReferenceEquals(DragState.Origin, _store);
        bool accept = !fromThisShelf && DropHelper.CanAccept(e.Data);

        e.Effects = !accept ? DragDropEffects.None : DragState.IsActive ? DropHelper.InternalEffect(e) : DragDropEffects.Copy;
        e.Handled = true;

        SetDragOver(accept);
        if (accept)
        {
            DropHighlight.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
            Expand();
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        SetDragOver(false);
        ItemsView.NoteDropPoint(e.GetPosition(this), this); // new tiles fly in from where they were dropped
        bool internalDrag = DragState.IsActive;
        int added = DropHelper.Accept(e.Data, _store);
        e.Effects = added > 0 ? (internalDrag ? DropHelper.InternalEffect(e) : DragDropEffects.Copy) : DragDropEffects.None;
        e.Handled = true;
        ScheduleCollapse();
    }

    // ---- Header buttons / counters ----------------------------------------------------------------

    private void Clear_Click(object sender, RoutedEventArgs e) => _store.Clear();

    private void Settings_Click(object sender, RoutedEventArgs e) => _openSettings();

    private void UpdateCounts()
    {
        int n = _store.Count;
        BadgeText.Text = n > 99 ? "99+" : n.ToString();
        Badge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = n switch { 0 => string.Empty, 1 => "1 item", _ => $"{n} items" };
        EmptyState.Visibility = n > 0 ? Visibility.Collapsed : Visibility.Visible;
        ItemsView.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = n > 0;
    }
}
