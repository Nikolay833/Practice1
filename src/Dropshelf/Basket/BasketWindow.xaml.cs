using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
/// The floating basket, summoned by shaking the mouse during a drag (or Ctrl+Shift+B). Laid out like Droppy's basket: a
/// glass card with a grab handle that is either <b>compact</b> (close and menu buttons, a fanned stack of up to three previews
/// that drags everything out at once, and an "N Files &gt;" button) or <b>expanded</b> (back button, title and total size,
/// icon / list switch, every item). The window is placed once per layout; opening and closing spring the content.
/// </summary>
public partial class BasketWindow : Window
{
    private static readonly Size CompactSize = new(188, 214);
    private static readonly Size ExpandedSize = new(468, 372);
    private const double StartScale = 0.86;

    private readonly ItemStore _basket;
    private readonly DispatcherTimer _hideTimer;
    private readonly Spring _open = new(0, 420, 0.75);   // 0 = small and transparent, 1 = open (overshoots a little)
    private readonly FrameLoop _loop;
    private IntPtr _hwnd;
    private bool _closing;
    private bool _expanded;
    private Point _stackPress;
    private bool _stackPressed;
    private int _sizeVersion;
    private readonly HashSet<DropItem> _watched = new();   // items whose thumbnail arrival rebuilds the stack

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
            UpdateContent();
            if (_basket.Count == 0 && e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
            {
                _hideTimer.Stop();
                _hideTimer.Start();
            }
        };
        UpdateContent();
        GridToggle.IsChecked = true;

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
    }

    /// <summary>Shows the basket (compact) centred on the cursor, clamped to that monitor's work area, and springs it open.</summary>
    public void ShowNearCursor()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.GetCursorPos(out var cursor);

        bool alreadyOpen = IsVisible && !_closing;
        _closing = false;
        if (!alreadyOpen)
        {
            SetExpanded(false, place: false);
        }

        Place(cursor.X, cursor.Y, _expanded ? ExpandedSize : CompactSize);

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

        Pop();
    }

    /// <summary>Places the window (size in DIPs) centred on a pixel point, inside that monitor's work area.</summary>
    private void Place(int centerX, int centerY, Size size)
    {
        var monitor = MonitorInfo.FromPoint(centerX, centerY);
        int w = (int)Math.Round(size.Width * monitor.Scale);
        int h = (int)Math.Round(size.Height * monitor.Scale);
        int x = Math.Clamp(centerX - w / 2, monitor.WorkLeft, Math.Max(monitor.WorkLeft, monitor.WorkRight - w));
        int y = Math.Clamp(centerY - h / 2, monitor.WorkTop, Math.Max(monitor.WorkTop, monitor.WorkBottom - h));
        WindowHelper.SetBoundsTopmost(_hwnd, x, y, w, h);
    }

    /// <summary>Compact or expanded layout; with <paramref name="place"/> the window keeps its centre and changes size once.</summary>
    private void SetExpanded(bool expanded, bool place = true)
    {
        _expanded = expanded;
        CompactView.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandedView.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (!place || _hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var r))
        {
            return;
        }

        Place((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2, expanded ? ExpandedSize : CompactSize);
        if (!AnimationSettings.IsOff)
        {
            _open.Snap(0.55);
            Pop();
        }
    }

    private void Pop()
    {
        AnimationSettings.Configure(_open, 420, 0.75);
        _open.Target = 1;
        _loop.Start();
    }

    /// <summary>Quick collapse, then hides the window.</summary>
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
        Chrome.Opacity = Math.Clamp(t * 1.6, 0, 1);
    }

    // ---- Content ----------------------------------------------------------------------------------

    private void UpdateContent()
    {
        int n = _basket.Count;
        string title = Describe(_basket.Items);
        CountButton.Content = title;
        CountButton.Visibility = n > 0 ? Visibility.Visible : Visibility.Hidden;
        MenuButton.IsEnabled = n > 0;
        EmptyState.Visibility = n > 0 ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Text = n == 0 ? "Basket" : title;
        BuildStack();
        _ = UpdateSizeAsync();
        if (n == 0 && _expanded)
        {
            SetExpanded(false);
        }
    }

    /// <summary>"1 Video", "3 Images", "2 Folders", or "5 Files" for a mix.</summary>
    private static string Describe(IReadOnlyCollection<DropItem> items)
    {
        int n = items.Count;
        string? kind = null;
        foreach (DropItem item in items)
        {
            string k = item.Kind switch
            {
                DropItemKind.Folder => "Folder",
                DropItemKind.Text => "Text",
                DropItemKind.Url => "Link",
                _ => KindOf(Path.GetExtension(item.Path)),
            };
            if (kind == null)
            {
                kind = k;
            }
            else if (kind != k)
            {
                kind = "File";
                break;
            }
        }

        kind ??= "File";
        return n == 1 ? $"1 {kind}" : $"{n} {(kind == "Text" ? "Texts" : kind + "s")}";
    }

    private static string KindOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp" or ".heic" or ".avif" or ".ico" => "Image",
        ".mp4" or ".mov" or ".mkv" or ".avi" or ".wmv" or ".webm" or ".m4v" => "Video",
        ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" or ".ogg" or ".wma" => "Song",
        ".pdf" => "PDF",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "Archive",
        _ => "File",
    };

    /// <summary>Adds up the file sizes off the UI thread (folders are not walked) and shows them like "258.1 MB".</summary>
    private async Task UpdateSizeAsync()
    {
        int version = ++_sizeVersion;
        var paths = _basket.Items.Where(i => i.Kind != DropItemKind.Folder).Select(i => i.Path).ToList();
        int folders = _basket.Items.Count(i => i.Kind == DropItemKind.Folder);
        long total = await Task.Run(() =>
        {
            long sum = 0;
            foreach (string path in paths)
            {
                try
                {
                    sum += new FileInfo(path).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    // Gone or unreadable: leave it out of the total.
                }
            }

            return sum;
        });

        if (version != _sizeVersion)
        {
            return;
        }

        string size = FormatSize(total);
        SizeText.Text = folders == 0 ? size : paths.Count == 0 ? (folders == 1 ? "1 folder" : $"{folders} folders") : $"{size} + folders";
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000; // Apple counts in powers of 1000
            unit++;
        }

        return unit == 0 ? $"{bytes} bytes" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>Up to three previews, the newest on top, fanned out a few degrees like a stack of cards.</summary>
    private void BuildStack()
    {
        Stack.Children.Clear();
        _watched.RemoveWhere(i => !_basket.Items.Contains(i));
        var top = _basket.Items.Reverse().Take(3).Reverse().ToList();
        double[] angles = top.Count switch { 1 => new[] { 0.0 }, 2 => new[] { -7.0, 4.0 }, _ => new[] { -9.0, 7.0, 0.0 } };
        for (int i = 0; i < top.Count; i++)
        {
            DropItem item = top[i];
            if (_watched.Add(item))
            {
                // Thumbnails load in the background; once a picture preview arrives, the card drops its icon padding.
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DropItem.IsThumbnail) && _basket.Items.Contains(item))
                    {
                        BuildStack();
                    }
                };
            }

            var image = new Image { Stretch = Stretch.UniformToFill };
            image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(DropItem.Icon)) { Source = item });
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var card = new Border
            {
                Width = 74,
                Height = 74,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(item.IsThumbnail ? 0 : 10),
                Child = image,
                RenderTransformOrigin = new Point(0.5, 0.8),
                RenderTransform = new RotateTransform(angles[i]),
                IsHitTestVisible = false,
            };
            image.Clip = item.IsThumbnail ? new RectangleGeometry(new Rect(0, 0, 74, 74), 10, 10) : null;
            Stack.Children.Add(card);
        }
    }

    // ---- Drag in / out ----------------------------------------------------------------------------

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

    private void Stack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            SetExpanded(true);
            return;
        }

        _stackPress = e.GetPosition(Stack);
        _stackPressed = true;
    }

    /// <summary>Dragging the stack drags every item out at once.</summary>
    private void Stack_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_stackPressed || e.LeftButton != MouseButtonState.Pressed)
        {
            _stackPressed = false;
            return;
        }

        Point p = e.GetPosition(Stack);
        if (Math.Abs(p.X - _stackPress.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(p.Y - _stackPress.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _stackPressed = false;
        ItemsView.DragItems(_basket.Items.ToList(), Stack);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
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

    // ---- Buttons ----------------------------------------------------------------------------------

    private void Close_Click(object sender, RoutedEventArgs e) => HideAnimated();

    private void Expand_Click(object sender, RoutedEventArgs e) => SetExpanded(true);

    private void Collapse_Click(object sender, RoutedEventArgs e) => SetExpanded(false);

    private void GridMode_Click(object sender, RoutedEventArgs e)
    {
        ItemsView.IsListMode = false;
        GridToggle.IsChecked = true;
        ListToggle.IsChecked = false;
    }

    private void ListMode_Click(object sender, RoutedEventArgs e)
    {
        ItemsView.IsListMode = true;
        GridToggle.IsChecked = false;
        ListToggle.IsChecked = true;
    }

    /// <summary>The chevron menu: show all, select everything for the tools menu, clear.</summary>
    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MakeItem("Show All", "", () => SetExpanded(true)));
        menu.Items.Add(MakeItem("Copy All", "", CopyAll));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Clear Basket", "", () => _basket.Clear()));
        menu.IsOpen = true;
    }

    private static MenuItem MakeItem(string header, string glyph, Action action)
    {
        var item = new MenuItem { Header = header, Icon = glyph };
        item.Click += (_, _) => action();
        return item;
    }

    private async void CopyAll()
    {
        var paths = _basket.Items.Where(i => i.Exists).Select(i => i.Path).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        var files = new System.Collections.Specialized.StringCollection();
        files.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(files);
        await Dropshelf.Clipboard.ClipboardAccess.TrySetDataObjectAsync(data);
    }
}
