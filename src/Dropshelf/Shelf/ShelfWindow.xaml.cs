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
/// frame, so the transparent area around the pill never blocks clicks.
/// </summary>
public partial class ShelfWindow : Window
{
    private const double CollapsedWidth = 200, CollapsedHeight = 32, CollapsedRadius = 14;
    private const double ExpandedWidth = 640, ExpandedHeight = 180, ExpandedRadius = 24;
    private const double AnimationMs = 250;

    private readonly ShelfStore _store;
    private readonly SettingsService _settings;
    private readonly Action _openSettings;
    private readonly DispatcherTimer _expandTimer;
    private readonly DispatcherTimer _collapseTimer;

    private IntPtr _hwnd;
    private MonitorInfo _monitor = MonitorInfo.Primary();
    private bool _expanded;
    private double _progress;          // 0 = collapsed pill, 1 = fully expanded
    private double _animFrom, _animTo, _animDurationMs;
    private long _animStart;
    private bool _rendering;
    private long _holdUntilTick;       // keeps a tray-triggered expansion open for a moment

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

        _expandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _expandTimer.Tick += (_, _) =>
        {
            _expandTimer.Stop();
            if (WindowHelper.ContainsCursor(_hwnd))
            {
                Expand();
            }
        };

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _collapseTimer.Tick += OnCollapseTick;

        MouseEnter += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!_expanded && _settings.Current.ExpandShelfOnHover)
            {
                _expandTimer.Stop();
                _expandTimer.Start();
            }
        };
        MouseLeave += (_, _) =>
        {
            _expandTimer.Stop();
            ScheduleCollapse();
        };

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
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyLayout();
    }

    protected override void OnClosed(EventArgs e)
    {
        _expandTimer.Stop();
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
        _expandTimer.Stop();
        _collapseTimer.Stop();
        if (_expanded)
        {
            return;
        }

        _expanded = true;
        _monitor = MonitorInfo.Primary();
        int pruned = _store.PruneMissing(); // drop files that vanished while the shelf was closed
        if (pruned > 0)
        {
            Log.Info($"Pruned {pruned} missing shelf item(s)");
        }

        ExpandedPanel.IsHitTestVisible = true;
        AnimateTo(1);
    }

    private void Collapse()
    {
        _expandTimer.Stop();
        _collapseTimer.Stop();
        if (!_expanded)
        {
            return;
        }

        _expanded = false;
        ExpandedPanel.IsHitTestVisible = false;
        DropHighlight.Visibility = Visibility.Collapsed;
        AnimateTo(0);
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

        e.Effects = !accept ? DragDropEffects.None : DragState.IsActive ? DragDropEffects.Move : DragDropEffects.Copy;
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
        e.Effects = added > 0 ? (internalDrag ? DragDropEffects.Move : DragDropEffects.Copy) : DragDropEffects.None;
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
