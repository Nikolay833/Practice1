using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Dropshelf.Animation;
using Dropshelf.Interop;
using Dropshelf.Services;
using Dropshelf.Shelf;

namespace Dropshelf.Basket;

/// <summary>
/// Small floating drop zone summoned by shaking the mouse during a drag (or Ctrl+Shift+B).
/// Dark acrylic glass when the OS supports it, translucent dark otherwise.
/// </summary>
public partial class BasketWindow : Window
{
    private readonly ItemStore _basket;
    private readonly ShelfStore _shelf;
    private const double BaseSize = 260;       // DIPs; the window's own Width/Height change while it animates
    private const double StartScale = 0.6;

    private readonly DispatcherTimer _hideTimer;
    private readonly Spring _open = new(0, 520, 0.55);   // 0 = small at the cursor, 1 = fully open (overshoots a little)
    private readonly Spring _openHeight = new(0, 400, 0.5);   // height follows a touch later and softer: a jelly-like settle
    private readonly FrameLoop _loop;
    private IntPtr _hwnd;
    private bool _closing;
    private int _finalX, _finalY, _finalW, _finalH;      // open placement in pixels
    private int _originX, _originY;                      // the cursor: where it grows from and collapses to

    public BasketWindow(ItemStore basket, ShelfStore shelf)
    {
        InitializeComponent();
        _basket = basket;
        _shelf = shelf;
        _loop = new FrameLoop(OnFrame);

        // Auto-hide once emptied. Wait until no drag of ours is in flight so we never hide a live drag source.
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hideTimer.Tick += (_, _) =>
        {
            if (DragState.IsActive)
            {
                return;
            }

            _hideTimer.Stop();
            if (_basket.Count == 0)
            {
                HideAnimated();
            }
        };

        ItemsView.Store = basket;
        basket.Items.CollectionChanged += (_, e) =>
        {
            UpdateCounts();
            if (_basket.Count == 0 && e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
            {
                _hideTimer.Stop();
                _hideTimer.Start();
            }
        };
        UpdateCounts();

        DragEnter += OnDragOverOrEnter;
        DragOver += OnDragOverOrEnter;
        DragLeave += (_, _) => DropHighlight.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowHelper.MakeToolWindow(_hwnd, noActivate: true);

        if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } target)
        {
            target.BackgroundColor = Colors.Transparent;
        }

        GlassHelper.ExtendFrameIntoClient(_hwnd);
        GlassHelper.TryRoundCorners(_hwnd);

        // With real acrylic the tint comes from the accent policy; otherwise keep the translucent XAML fill.
        if (GlassHelper.TryEnableAcrylic(_hwnd, 0xE6141414))
        {
            Chrome.Background = Brushes.Transparent;
        }
    }

    /// <summary>
    /// Shows the basket centred on the cursor (clamped to that monitor's work area). It springs open from the cursor:
    /// scale 0.6 to 1 with a slight overshoot; height settles a beat after width. (WPF has no 3D projection for ordinary
    /// elements, and a window cannot tilt, so this soft squash is the stand-in for a tilt.)
    /// </summary>
    public void ShowNearCursor()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.GetCursorPos(out var cursor);
        var monitor = MonitorInfo.FromPoint(cursor.X, cursor.Y);

        int w = (int)Math.Round(BaseSize * monitor.Scale);
        int h = (int)Math.Round(BaseSize * monitor.Scale);
        _finalW = w;
        _finalH = h;
        _finalX = Math.Clamp(cursor.X - w / 2, monitor.WorkLeft, Math.Max(monitor.WorkLeft, monitor.WorkRight - w));
        _finalY = Math.Clamp(cursor.Y - h / 2, monitor.WorkTop, Math.Max(monitor.WorkTop, monitor.WorkBottom - h));
        _originX = cursor.X;
        _originY = cursor.Y;

        bool alreadyOpen = IsVisible && !_closing;
        _closing = false;

        if (AnimationSettings.IsOff || alreadyOpen)
        {
            // Instant: animations off, or the hotkey was pressed again while it is open (just move it).
            _loop.Stop();
            _open.Snap(1);
            _openHeight.Snap(1);
            ApplyFrame();
            if (!IsVisible)
            {
                Show();
            }

            ApplyFrame();
            return;
        }

        if (!IsVisible)
        {
            _open.Snap(0);
            _openHeight.Snap(0);
            ApplyFrame();
            Show();
            ApplyFrame();
        }

        AnimationSettings.Configure(_open, 520, 0.55);
        AnimationSettings.Configure(_openHeight, 400, 0.5);
        _open.Target = 1;
        _openHeight.Target = 1;
        _loop.Start();
    }

    /// <summary>Quick spring collapse toward the cursor, then hides the window.</summary>
    private void HideAnimated()
    {
        if (!IsVisible)
        {
            return;
        }

        if (AnimationSettings.IsOff)
        {
            _loop.Stop();
            Hide();
            return;
        }

        if (_closing)
        {
            return;
        }

        // The user may have dragged the window by its header, so collapse from where it is now.
        if (NativeMethods.GetWindowRect(_hwnd, out var r) && NativeMethods.GetCursorPos(out var cursor))
        {
            _finalX = r.Left;
            _finalY = r.Top;
            _finalW = r.Right - r.Left;
            _finalH = r.Bottom - r.Top;
            _originX = cursor.X;
            _originY = cursor.Y;
        }

        _closing = true;
        AnimationSettings.Configure(_open, 900, 1.0);
        AnimationSettings.Configure(_openHeight, 900, 1.0);
        _open.Target = -0.7;
        _openHeight.Target = -0.7;
        _loop.Start();
    }

    private bool OnFrame(double dt)
    {
        _open.Step(dt);
        _openHeight.Step(dt);
        ApplyFrame();

        if (_closing)
        {
            if (_open.Value > -0.45)
            {
                return true;
            }

            _closing = false;
            _open.Snap(0);
            _openHeight.Snap(0);
            Hide();
            return false;
        }

        if (_open.SettleIfClose(0.001, 0.02) & _openHeight.SettleIfClose(0.001, 0.02))
        {
            ApplyFrame();
            return false;
        }

        return true;
    }

    /// <summary>Places the window for the current spring value: grows from / shrinks toward the cursor.</summary>
    private void ApplyFrame()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        double t = _open.Value;
        double size = Math.Max(0.25, StartScale + (1 - StartScale) * t);
        double sizeH = Math.Max(0.25, StartScale + (1 - StartScale) * _openHeight.Value);
        double travel = 1 - Math.Clamp(t, 0, 1);               // 0 = at the final spot, 1 = at the cursor
        double cx = _finalX + _finalW / 2.0 + (_originX - (_finalX + _finalW / 2.0)) * travel;
        double cy = _finalY + _finalH / 2.0 + (_originY - (_finalY + _finalH / 2.0)) * travel;
        double w = _finalW * size;
        double h = _finalH * sizeH;
        WindowHelper.SetBoundsTopmost(_hwnd, (int)Math.Round(cx - w / 2), (int)Math.Round(cy - h / 2), (int)Math.Round(w), (int)Math.Round(h));
    }

    private void OnDragOverOrEnter(object sender, DragEventArgs e)
    {
        bool fromBasket = DragState.IsActive && ReferenceEquals(DragState.Origin, _basket);
        bool accept = !fromBasket && DropHelper.CanAccept(e.Data);
        e.Effects = !accept ? DragDropEffects.None : DragState.IsActive ? DropHelper.InternalEffect(e) : DragDropEffects.Copy;
        DropHighlight.Visibility = accept ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        ItemsView.NoteDropPoint(e.GetPosition(this), this); // new tiles fly in from where they were dropped
        bool internalDrag = DragState.IsActive;
        int added = DropHelper.Accept(e.Data, _basket);
        e.Effects = added > 0 ? (internalDrag ? DropHelper.InternalEffect(e) : DragDropEffects.Copy) : DragDropEffects.None;
        e.Handled = true;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Button was released before the move started.
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HideAnimated();

    private void MoveAll_Click(object sender, RoutedEventArgs e)
    {
        var items = _basket.Items.ToList();
        if (items.Count == 0)
        {
            return;
        }

        _shelf.AddRange(items);
        _basket.Remove(items, deleteOwnedFiles: false); // the shelf now owns the text/link files
    }

    private void UpdateCounts()
    {
        int n = _basket.Count;
        CountText.Text = n == 0 ? string.Empty : n == 1 ? "1 item" : $"{n} items";
        EmptyState.Visibility = n > 0 ? Visibility.Collapsed : Visibility.Visible;
        ItemsView.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoveButton.IsEnabled = n > 0;
    }
}
