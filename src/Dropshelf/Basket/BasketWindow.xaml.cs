using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _hideTimer;
    private IntPtr _hwnd;

    public BasketWindow(ItemStore basket, ShelfStore shelf)
    {
        InitializeComponent();
        _basket = basket;
        _shelf = shelf;

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
                Hide();
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

    /// <summary>Shows the basket centred on the cursor (clamped to that monitor's work area).</summary>
    public void ShowNearCursor()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.GetCursorPos(out var cursor);
        var monitor = MonitorInfo.FromPoint(cursor.X, cursor.Y);

        int w = (int)Math.Round(Width * monitor.Scale);
        int h = (int)Math.Round(Height * monitor.Scale);
        int x = Math.Clamp(cursor.X - w / 2, monitor.WorkLeft, Math.Max(monitor.WorkLeft, monitor.WorkRight - w));
        int y = Math.Clamp(cursor.Y - h / 2, monitor.WorkTop, Math.Max(monitor.WorkTop, monitor.WorkBottom - h));

        WindowHelper.SetBoundsTopmost(_hwnd, x, y, w, h);
        if (!IsVisible)
        {
            Show();
        }

        WindowHelper.SetBoundsTopmost(_hwnd, x, y, w, h);
    }

    private void OnDragOverOrEnter(object sender, DragEventArgs e)
    {
        bool fromBasket = DragState.IsActive && ReferenceEquals(DragState.Origin, _basket);
        bool accept = !fromBasket && DropHelper.CanAccept(e.Data);
        e.Effects = !accept ? DragDropEffects.None : DragState.IsActive ? DragDropEffects.Move : DragDropEffects.Copy;
        DropHighlight.Visibility = accept ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        bool internalDrag = DragState.IsActive;
        int added = DropHelper.Accept(e.Data, _basket);
        e.Effects = added > 0 ? (internalDrag ? DragDropEffects.Move : DragDropEffects.Copy) : DragDropEffects.None;
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

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

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
