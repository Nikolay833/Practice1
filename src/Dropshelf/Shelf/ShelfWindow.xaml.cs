using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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
    private const double AnimationMs = 250;
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
    private double _progress;          // 0 = collapsed pill, 1 = fully expanded
    private double _animFrom, _animTo, _animDurationMs;
    private long _animStart;
    private bool _rendering;
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

        ItemsView.Store = store;
        ItemsView.BusyChanged += (_, _) =>
        {
            if (!ItemsView.IsBusy)
            {
                ScheduleCollapse();
            }
        };
        store.Items.CollectionChanged += (_, _) => UpdateCounts();
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
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }

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
        AnimateTo(1);
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

    private void AnimateTo(double target)
    {
        if (Math.Abs(target - _progress) < 0.001)
        {
            return;
        }

        _animFrom = _progress;
        _animTo = target;
        _animDurationMs = Math.Max(60, AnimationMs * Math.Abs(target - _progress));
        _animStart = Stopwatch.GetTimestamp();

        if (!_rendering)
        {
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double elapsedMs = (Stopwatch.GetTimestamp() - _animStart) * 1000.0 / Stopwatch.Frequency;
        double t = Math.Min(1.0, elapsedMs / _animDurationMs);
        double eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
        _progress = _animFrom + (_animTo - _animFrom) * eased;
        ApplyLayout();

        if (t >= 1.0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
    }

    /// <summary>Sizes/positions the window for the current animation progress and updates the pill visuals.</summary>
    private void ApplyLayout()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        double p = _progress;
        double w = Lerp(CollapsedWidth, ExpandedWidth, p);
        double h = Lerp(CollapsedHeight, ExpandedHeight, p);
        double r = Lerp(CollapsedRadius, ExpandedRadius, p);

        int pxW = (int)Math.Round(w * _monitor.Scale);
        int pxH = (int)Math.Round(h * _monitor.Scale);
        int x = _monitor.Left + (_monitor.Width - pxW) / 2;
        int y = _monitor.Top; // flush with the top edge of the screen
        WindowHelper.SetBoundsTopmost(_hwnd, x, y, pxW, pxH);

        Pill.CornerRadius = new CornerRadius(0, 0, r, r);
        ExpandedPanel.Opacity = Math.Clamp((p - 0.45) / 0.55, 0, 1);
        CollapsedLayer.Opacity = 1 - Math.Clamp(p / 0.35, 0, 1);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    // ---- Drag and drop onto the shelf -------------------------------------------------------------

    private void OnDragOverOrEnter(object sender, DragEventArgs e)
    {
        _collapseTimer.Stop();

        bool fromThisShelf = DragState.IsActive && ReferenceEquals(DragState.Origin, _store);
        bool accept = !fromThisShelf && DropHelper.CanAccept(e.Data);

        e.Effects = !accept ? DragDropEffects.None : DragState.IsActive ? DropHelper.InternalEffect(e) : DragDropEffects.Copy;
        e.Handled = true;

        if (accept)
        {
            DropHighlight.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
            Expand();
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
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
