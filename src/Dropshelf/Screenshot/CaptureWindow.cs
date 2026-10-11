using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Dropshelf.Animation;
using Dropshelf.Interop;

namespace Dropshelf.Screenshot;

/// <summary>
/// The capture overlay of one monitor: a transparent, topmost, borderless window that dims the screen, lets the user drag a
/// rectangle (live size label, eight handles that pop in with a spring) and offers "Full screen". It is placed in physical
/// pixels with SetWindowPos (like the other windows), so it covers exactly its monitor at any per-monitor DPI; the selection is
/// converted back to physical screen pixels with PointToScreen.
/// </summary>
internal sealed class CaptureWindow : Window
{
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x86, 0x00, 0x00, 0x00)));
    private static readonly Brush HitBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x01, 0x00, 0x00, 0x00)));
    private static readonly Brush PanelBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xEB, 0x14, 0x14, 0x14)));
    private const double HandleSize = 10;

    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly RectangleGeometry _full = new();
    private readonly RectangleGeometry _hole = new();
    private readonly Rectangle _frame;
    private readonly Border _label;
    private readonly TextBlock _labelText;
    private readonly Border _hint;
    private readonly Ellipse[] _handles = new Ellipse[8];
    private readonly ScaleTransform[] _handleScales = new ScaleTransform[8];
    private readonly Spring _pop = new(0, 420, 0.42);
    private readonly FrameLoop _loop;
    private IntPtr _hwnd;
    private bool _dragging;
    private bool _popped;
    private Point _anchor;
    private Rect _selection = Rect.Empty;

    public CaptureWindow(MonitorInfo monitor)
    {
        Monitor = monitor;
        _loop = new FrameLoop(OnPop);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;
        Left = 0;
        Top = 0;
        Width = 100;
        Height = 100;

        var root = new Grid { Background = HitBrush };
        var dim = new Path
        {
            Fill = DimBrush,
            IsHitTestVisible = false,
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, _full, _hole),
        };
        root.Children.Add(dim);
        root.Children.Add(_canvas);

        _frame = new Rectangle { StrokeThickness = 1.5, Visibility = Visibility.Collapsed, Fill = Brushes.Transparent };
        _frame.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        _canvas.Children.Add(_frame);

        for (int i = 0; i < _handles.Length; i++)
        {
            var scale = new ScaleTransform(0, 0);
            var handle = new Ellipse
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = Brushes.White,
                StrokeThickness = 2,
                Visibility = Visibility.Collapsed,
                RenderTransform = scale,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            handle.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            _handles[i] = handle;
            _handleScales[i] = scale;
            _canvas.Children.Add(handle);
        }

        _labelText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        _labelText.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        _label = new Border
        {
            Background = PanelBrush,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 3, 8, 3),
            Child = _labelText,
            Visibility = Visibility.Collapsed,
        };
        _canvas.Children.Add(_label);

        // Top-centre hint bar with the "Full screen" button (the only part that takes clicks).
        var fullButton = new Button
        {
            Content = "Full screen  F",
            Style = (Style)Application.Current.FindResource("AccentButton"),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(12, 0, 0, 0),
        };
        fullButton.Click += (_, _) => FullScreenPicked?.Invoke(this);
        var hintText = new TextBlock { Text = "Drag to capture    Esc to cancel", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        hintText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        hintText.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        _hint = new Border
        {
            Background = PanelBrush,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 7, 8, 7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 22, 0, 0),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { hintText, fullButton } },
        };
        root.Children.Add(_hint);
        Content = root;

        SizeChanged += (_, _) =>
        {
            _full.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
            UpdateVisuals();
        };
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonDown += (_, _) => Cancelled?.Invoke();
        PreviewKeyDown += OnKeyDown;
        MouseEnter += (_, _) =>
        {
            if (!IsActive)
            {
                Activate(); // keys go to the monitor the pointer is on
            }
        };
        Closed += (_, _) => _loop.Stop();
    }

    public MonitorInfo Monitor { get; }

    public event Action<CaptureWindow, Rect>? RegionPicked;
    public event Action<CaptureWindow>? FullScreenPicked;
    public event Action? Cancelled;

    /// <summary>The Enter key: take the selection that is being dragged.</summary>
    public event Action<CaptureWindow>? ConfirmRequested;

    /// <summary>The F key: the manager picks the monitor under the pointer.</summary>
    public event Action? FullScreenKey;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowHelper.MakeToolWindow(_hwnd, noActivate: false);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyBounds();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_DPICHANGED)
        {
            // WPF resizes the window for the new DPI; put it back on the monitor's exact pixel rectangle afterwards.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(ApplyBounds));
        }

        return IntPtr.Zero;
    }

    /// <summary>Covers the whole monitor, in physical pixels.</summary>
    public void ApplyBounds()
    {
        if (_hwnd != IntPtr.Zero)
        {
            WindowHelper.SetBoundsTopmost(_hwnd, Monitor.Left, Monitor.Top, Monitor.Width, Monitor.Height);
        }
    }

    /// <summary>Converts a rectangle in this window's DIPs to physical screen pixels.</summary>
    public System.Drawing.Rectangle ToScreenPixels(Rect rect)
    {
        Point a = PointToScreen(rect.TopLeft);
        Point b = PointToScreen(rect.BottomRight);
        int x = (int)Math.Round(a.X), y = (int)Math.Round(a.Y);
        return new System.Drawing.Rectangle(x, y, Math.Max(1, (int)Math.Round(b.X) - x), Math.Max(1, (int)Math.Round(b.Y) - y));
    }

    // ---- Input -----------------------------------------------------------------------------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _anchor = Clamp(e.GetPosition(this));
        _selection = new Rect(_anchor, new Size(0, 0));
        _dragging = true;
        _hint.Visibility = Visibility.Collapsed;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        Point p = Clamp(e.GetPosition(this));
        _selection = new Rect(
            new Point(Math.Min(_anchor.X, p.X), Math.Min(_anchor.Y, p.Y)),
            new Point(Math.Max(_anchor.X, p.X), Math.Max(_anchor.Y, p.Y)));
        if (!_popped && (_selection.Width > 3 || _selection.Height > 3))
        {
            Pop();
        }

        UpdateVisuals();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        if (_selection.Width >= 4 && _selection.Height >= 4)
        {
            RegionPicked?.Invoke(this, _selection);
        }
        else
        {
            // A click without a drag: start over.
            _selection = Rect.Empty;
            _popped = false;
            _hint.Visibility = Visibility.Visible;
            UpdateVisuals();
        }

        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Cancelled?.Invoke();
                e.Handled = true;
                break;
            case Key.Enter:
                if (_dragging && _selection.Width >= 4 && _selection.Height >= 4)
                {
                    _dragging = false;
                    ReleaseMouseCapture();
                    ConfirmRequested?.Invoke(this);
                }

                e.Handled = true;
                break;
            case Key.F:
                FullScreenKey?.Invoke();
                e.Handled = true;
                break;
        }
    }

    /// <summary>The selection (DIPs) as it is now, for Enter.</summary>
    public Rect Selection => _selection;

    private Point Clamp(Point p) =>
        new(Math.Clamp(p.X, 0, Math.Max(0, ActualWidth)), Math.Clamp(p.Y, 0, Math.Max(0, ActualHeight)));

    // ---- Drawing ---------------------------------------------------------------------------------

    private void UpdateVisuals()
    {
        if (!_popped || _selection.IsEmpty)
        {
            _hole.Rect = Rect.Empty;
            _frame.Visibility = Visibility.Collapsed;
            _label.Visibility = Visibility.Collapsed;
            foreach (Ellipse h in _handles)
            {
                h.Visibility = Visibility.Collapsed;
            }

            return;
        }

        Rect r = _selection;
        _hole.Rect = r;
        _frame.Visibility = Visibility.Visible;
        _frame.Width = r.Width;
        _frame.Height = r.Height;
        Canvas.SetLeft(_frame, r.Left);
        Canvas.SetTop(_frame, r.Top);

        double mx = r.Left + r.Width / 2, my = r.Top + r.Height / 2;
        Point[] points =
        [
            new(r.Left, r.Top), new(mx, r.Top), new(r.Right, r.Top),
            new(r.Right, my), new(r.Right, r.Bottom), new(mx, r.Bottom),
            new(r.Left, r.Bottom), new(r.Left, my),
        ];
        for (int i = 0; i < _handles.Length; i++)
        {
            _handles[i].Visibility = Visibility.Visible;
            Canvas.SetLeft(_handles[i], points[i].X - HandleSize / 2);
            Canvas.SetTop(_handles[i], points[i].Y - HandleSize / 2);
        }

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _labelText.Text = $"{Math.Round(r.Width * scale):0} × {Math.Round(r.Height * scale):0}";
        _label.Visibility = Visibility.Visible;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = _label.DesiredSize;
        double lx = Math.Clamp(r.Left, 4, Math.Max(4, ActualWidth - size.Width - 4));
        double ly = r.Bottom + 8;
        if (ly + size.Height > ActualHeight - 4)
        {
            ly = r.Top - size.Height - 8; // no room below: above the selection
        }

        if (ly < 4)
        {
            ly = Math.Max(4, r.Bottom - size.Height - 8); // the selection fills the screen: label inside it
        }

        Canvas.SetLeft(_label, lx);
        Canvas.SetTop(_label, ly);
    }

    /// <summary>The handles pop in with an overshooting spring when the selection appears.</summary>
    private void Pop()
    {
        _popped = true;
        if (AnimationSettings.IsOff)
        {
            _pop.Snap(1);
            ApplyPop();
            return;
        }

        AnimationSettings.Configure(_pop, 420, 0.42);
        _pop.Snap(0);
        _pop.Target = 1;
        ApplyPop();
        _loop.Start();
    }

    private bool OnPop(double dt)
    {
        _pop.Step(dt);
        ApplyPop();
        return !_pop.SettleIfClose(0.002, 0.02);
    }

    private void ApplyPop()
    {
        foreach (ScaleTransform s in _handleScales)
        {
            s.ScaleX = s.ScaleY = _pop.Value;
        }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
