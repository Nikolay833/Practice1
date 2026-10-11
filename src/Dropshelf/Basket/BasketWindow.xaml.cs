using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Dropshelf.Animation;
using Dropshelf.Interop;
using Dropshelf.Items;
using Dropshelf.Services;

namespace Dropshelf.Basket;

/// <summary>
/// Small floating drop zone summoned by shaking the mouse during a drag (or Ctrl+Shift+B): a frosted popover with
/// Windows' acrylic blur when the OS supports it, translucent dark otherwise. The window is placed once at its final size;
/// opening and closing animate the content (spring scale + fade from the cursor side), never the window bounds.
/// </summary>
public partial class BasketWindow : Window
{
    private const double BaseSize = 272;     // DIPs
    private const double StartScale = 0.86;

    private readonly ItemStore _basket;
    private readonly DispatcherTimer _hideTimer;
    private readonly Spring _open = new(0, 420, 0.75);   // 0 = small and transparent, 1 = open (overshoots a little)
    private readonly FrameLoop _loop;
    private IntPtr _hwnd;
    private bool _closing;

    public BasketWindow(ItemStore basket)
    {
        InitializeComponent();
        _basket = basket;
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
        if (GlassHelper.MakeFrosted(_hwnd, 0xCC1E1C1C))
        {
            Chrome.Background = Brushes.Transparent; // the acrylic tint replaces the XAML fill
        }
    }

    /// <summary>Shows the basket centred on the cursor (clamped to that monitor's work area) and springs it open.</summary>
    public void ShowNearCursor()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.GetCursorPos(out var cursor);
        var monitor = MonitorInfo.FromPoint(cursor.X, cursor.Y);

        int w = (int)Math.Round(BaseSize * monitor.Scale);
        int h = (int)Math.Round(BaseSize * monitor.Scale);
        int x = Math.Clamp(cursor.X - w / 2, monitor.WorkLeft, Math.Max(monitor.WorkLeft, monitor.WorkRight - w));
        int y = Math.Clamp(cursor.Y - h / 2, monitor.WorkTop, Math.Max(monitor.WorkTop, monitor.WorkBottom - h));

        bool alreadyOpen = IsVisible && !_closing;
        _closing = false;
        WindowHelper.SetBoundsTopmost(_hwnd, x, y, w, h);

        // The content grows out of the side where the cursor is.
        Body.RenderTransformOrigin = new Point(
            Math.Clamp((cursor.X - x) / (double)Math.Max(1, w), 0, 1),
            Math.Clamp((cursor.Y - y) / (double)Math.Max(1, h), 0, 1));

        if (AnimationSettings.IsOff || alreadyOpen)
        {
            _loop.Stop();
            _open.Snap(1);
            Apply();
            if (!IsVisible)
            {
                Show();
            }

            return;
        }

        if (!IsVisible)
        {
            _open.Snap(0);
            Apply();
            Show();
        }

        AnimationSettings.Configure(_open, 420, 0.75);
        _open.Target = 1;
        _loop.Start();
    }

    /// <summary>Quick collapse toward the cursor side, then hides the window.</summary>
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

        _closing = true;
        AnimationSettings.Configure(_open, 700, 1.0);
        _open.Target = -0.05;
        _loop.Start();
    }

    private bool OnFrame(double dt)
    {
        _open.Step(dt);
        Apply();

        if (_closing)
        {
            if (_open.Value > 0.02)
            {
                return true;
            }

            _closing = false;
            _open.Snap(0);
            Hide();
            return false;
        }

        if (_open.SettleIfClose(0.001, 0.02))
        {
            Apply();
            return false;
        }

        return true;
    }

    private void Apply()
    {
        double t = Math.Max(0, _open.Value);
        double scale = StartScale + (1 - StartScale) * t;
        ContentScale.ScaleX = scale;
        ContentScale.ScaleY = scale;
        Body.Opacity = Math.Clamp(t * 1.6, 0, 1);
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

    private void Clear_Click(object sender, RoutedEventArgs e) => _basket.Clear();

    private void UpdateCounts()
    {
        int n = _basket.Count;
        CountText.Text = n == 0 ? string.Empty : n == 1 ? "1 item" : $"{n} items";
        EmptyState.Visibility = n > 0 ? Visibility.Collapsed : Visibility.Visible;
        ItemsView.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
