using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Dropshelf.Animation;
using Dropshelf.Clipboard;
using Dropshelf.Screenshot;
using Dropshelf.Services;
using Dropshelf.Settings;
using Dropshelf.Tools;

namespace Dropshelf.Items;

/// <summary>
/// Reusable list of item tiles (one scrolling row, or a wrapping grid in the basket) with multi-select,
/// drag-out, double-click open and a context menu.
/// </summary>
public partial class ItemListView : UserControl
{
    public static readonly DependencyProperty IsWrapProperty = DependencyProperty.Register(
        nameof(IsWrap), typeof(bool), typeof(ItemListView),
        new PropertyMetadata(false, (d, _) => ((ItemListView)d).ApplyLayout()));

    public static readonly DependencyProperty IsListModeProperty = DependencyProperty.Register(
        nameof(IsListMode), typeof(bool), typeof(ItemListView),
        new PropertyMetadata(false, (d, _) => ((ItemListView)d).ApplyLayout()));

    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(
        nameof(TileWidth), typeof(double), typeof(ItemListView), new PropertyMetadata(84.0));

    private ItemStore? _store;
    private ScrollViewer? _scroll;
    private Point _pressPoint;
    private DropItem? _pressedItem;
    private bool _deferSingleSelect;
    private bool _menuOpen;
    private bool _dragging;
    private Point? _dropPoint;         // where the last drop landed, in this control's coordinates
    private long _dropTick;

    public ItemListView()
    {
        InitializeComponent();
        ApplyLayout();
    }

    /// <summary>Wrap tiles into a vertical grid instead of a single horizontal row.</summary>
    public bool IsWrap { get => (bool)GetValue(IsWrapProperty); set => SetValue(IsWrapProperty, value); }

    public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }

    /// <summary>One row per item (icon, name, folder) instead of icons.</summary>
    public bool IsListMode { get => (bool)GetValue(IsListModeProperty); set => SetValue(IsListModeProperty, value); }

    /// <summary>When true, any successful drop (copy or move) removes the dragged items (basket behaviour).</summary>
    public bool RemoveOnAnyDrop { get; set; }

    /// <summary>True while a context menu is open or a drag is running; the host should not auto-collapse then.</summary>
    public bool IsBusy => _menuOpen || _dragging;

    public event EventHandler? BusyChanged;

    public ItemStore? Store
    {
        get => _store;
        set
        {
            if (_store != null)
            {
                _store.Removing -= OnItemsRemoving;
            }

            _store = value;
            List.ItemsSource = value?.Items;
            if (_store != null)
            {
                _store.Removing += OnItemsRemoving;
            }
        }
    }

    // ---- Animations ---------------------------------------------------------------------------

    /// <summary>Remembers where a drop landed so the tiles it creates fly in from that point.</summary>
    /// <param name="point">Drop position in the coordinate space of <paramref name="space"/>.</param>
    /// <param name="space">The visual (usually the host window) <paramref name="point"/> is relative to.</param>
    public void NoteDropPoint(Point point, Visual space)
    {
        try
        {
            _dropPoint = space.TransformToDescendant(this).Transform(point);
            _dropTick = Environment.TickCount64;
        }
        catch (InvalidOperationException)
        {
            _dropPoint = null; // not connected to the same visual tree yet
        }
    }

    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        // Only tiles created by a drop a moment ago fly in; tiles realised for any other reason just appear.
        if (sender is not FrameworkElement { DataContext: DropItem item } root || _dropPoint is not { } drop
            || Environment.TickCount64 - _dropTick > 1500 || (DateTime.UtcNow - item.AddedUtc).TotalSeconds > 3)
        {
            return;
        }

        try
        {
            var container = ContainerAt(root) as FrameworkElement ?? root;
            Point center = root.TranslatePoint(new Point(root.ActualWidth / 2, root.ActualHeight / 2), this);
            TileAnimator.FlyIn(container, drop - center);
        }
        catch (InvalidOperationException)
        {
            // Tile was not connected to this control; skip the flourish.
        }
    }

    /// <summary>
    /// Removed tiles shrink and fade: a bitmap snapshot of each tile is taken before it leaves the list and animated on the
    /// overlay canvas (the real tile is already gone by the time the collection has changed).
    /// </summary>
    private void OnItemsRemoving(IReadOnlyList<DropItem> items)
    {
        if (AnimationSettings.IsOff || !IsVisible || ActualWidth < 1)
        {
            return;
        }

        double scale = AnimationSettings.TimeScale;
        foreach (var item in items)
        {
            if (List.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement tile || !tile.IsVisible
                || tile.ActualWidth < 1 || tile.ActualHeight < 1)
            {
                continue;
            }

            try
            {
                var dpi = VisualTreeHelper.GetDpi(tile);
                var bitmap = new RenderTargetBitmap(
                    (int)Math.Ceiling(tile.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(tile.ActualHeight * dpi.DpiScaleY),
                    96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                bitmap.Render(tile);

                Point origin = tile.TransformToVisual(Ghosts).Transform(new Point(0, 0));
                var shrink = new ScaleTransform(1, 1);
                var ghost = new Image
                {
                    Source = bitmap,
                    Width = tile.ActualWidth,
                    Height = tile.ActualHeight,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = shrink,
                };
                Canvas.SetLeft(ghost, origin.X);
                Canvas.SetTop(ghost, origin.Y);
                Ghosts.Children.Add(ghost);

                var duration = TimeSpan.FromMilliseconds(220 * scale);
                var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
                var fade = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
                fade.Completed += (_, _) => Ghosts.Children.Remove(ghost);
                shrink.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.4, duration) { EasingFunction = ease });
                shrink.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.4, duration) { EasingFunction = ease });
                ghost.BeginAnimation(OpacityProperty, fade);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                Log.Error("Could not animate tile removal", ex);
            }
        }
    }

    private void ApplyLayout()
    {
        if (List == null)
        {
            return;
        }

        bool vertical = IsWrap || IsListMode;
        var factory = new FrameworkElementFactory(IsListMode ? typeof(StackPanel) : IsWrap ? typeof(WrapPanel) : typeof(StackPanel));
        if (!vertical)
        {
            factory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        }

        List.ItemsPanel = new ItemsPanelTemplate(factory);
        List.ItemTemplate = (DataTemplate)FindResource(IsListMode ? "RowTemplate" : "TileTemplate");
        ScrollViewer.SetHorizontalScrollBarVisibility(List, vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(List, vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
    }

    private void SetBusy(bool? menu = null, bool? dragging = null)
    {
        if (menu.HasValue)
        {
            _menuOpen = menu.Value;
        }

        if (dragging.HasValue)
        {
            _dragging = dragging.Value;
        }

        BusyChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Selection, drag-out ------------------------------------------------------------------

    private static ListBoxItem? ContainerAt(object? source)
    {
        var node = source as DependencyObject;
        while (node != null && node is not ListBoxItem)
        {
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return node as ListBoxItem;
    }

    private IReadOnlyList<DropItem> SelectedItems() => List.SelectedItems.OfType<DropItem>().ToList();

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedItem = null;
        _deferSingleSelect = false;

        var container = ContainerAt(e.OriginalSource);
        if (container?.DataContext is not DropItem item)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            OpenItems([item]);
            e.Handled = true;
            return;
        }

        _pressedItem = item;
        _pressPoint = e.GetPosition(List);

        // Pressing on one of several selected tiles must keep the selection so all of them can be dragged.
        // The click is resolved on mouse-up if no drag happened.
        if (container.IsSelected && Keyboard.Modifiers == ModifierKeys.None && List.SelectedItems.Count > 1)
        {
            _deferSingleSelect = true;
            e.Handled = true;
        }
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferSingleSelect && _pressedItem != null)
        {
            List.SelectedItems.Clear();
            List.SelectedItem = _pressedItem;
        }

        _pressedItem = null;
        _deferSingleSelect = false;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedItem == null)
        {
            return;
        }

        Point pos = e.GetPosition(List);
        if (Math.Abs(pos.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var pressed = _pressedItem;
        _pressedItem = null;
        _deferSingleSelect = false;
        StartDrag(pressed);
    }

    private void StartDrag(DropItem pressed)
    {
        var items = List.SelectedItems.Contains(pressed) ? SelectedItems() : new List<DropItem> { pressed };
        DragItems(items, List);
    }

    /// <summary>
    /// Drags <paramref name="items"/> out of the store (the basket's stacked preview drags all of them). Files and folders
    /// are offered as copies only; afterwards the items leave the store as described in <see cref="RemoveOnAnyDrop"/>.
    /// </summary>
    public void DragItems(IReadOnlyList<DropItem> items, DependencyObject source)
    {
        if (_store == null)
        {
            return;
        }

        var existing = items.Where(i => i.Exists).ToList();
        if (existing.Count == 0)
        {
            _ = _store.PruneMissingAsync();
            return;
        }

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, existing.Select(i => i.Path).ToArray());

        // File and folder items are references to the user's originals, so a drop must never move them (Explorer
        // moves on same-volume drops). Only the text/link files we created ourselves may be moved out.
        bool ownedOnly = existing.All(i => i.Kind is DropItemKind.Text or DropItemKind.Url);
        DragDropEffects allowed = ownedOnly ? DragDropEffects.Copy | DragDropEffects.Move : DragDropEffects.Copy;

        DragDropEffects result = DragDropEffects.None;
        DragState.Begin(_store, existing);
        SetBusy(dragging: true);
        try
        {
            result = DragDrop.DoDragDrop(source, data, allowed);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Error("Drag-out failed", ex);
        }
        finally
        {
            DragState.End();
            SetBusy(dragging: false);
        }

        // Move: the files left their folder, so the references are stale. (Items are also removed when the
        // drop landed in another Dropshelf store; Remove is a no-op then, so a transferred item's text/link file is
        // never deleted.) Basket items that were dropped (copied) elsewhere are gone for good, so their own
        // .txt/.url files are deleted too instead of leaking in the Items folder.
        if (result.HasFlag(DragDropEffects.Move) || (RemoveOnAnyDrop && result != DragDropEffects.None))
        {
            _store.Remove(existing, deleteOwnedFiles: true);
        }
    }

    private void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var container = ContainerAt(e.OriginalSource);
        if (container != null && !container.IsSelected)
        {
            List.SelectedItems.Clear();
            container.IsSelected = true;
        }
    }

    private void List_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsWrap)
        {
            return;
        }

        _scroll ??= FindScrollViewer(List);
        if (_scroll != null)
        {
            _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
            {
                return sv;
            }

            var nested = FindScrollViewer(child);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    // ---- Context menu actions -----------------------------------------------------------------

    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        SetBusy(menu: true);
        if (sender is ContextMenu menu)
        {
            try
            {
                PrepareMenu(menu);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                Log.Error("Could not build the item menu", ex);
            }
        }
    }

    private void Menu_Closed(object sender, RoutedEventArgs e) => SetBusy(menu: false);

    private void Open_Click(object sender, RoutedEventArgs e) => OpenItems(SelectedItems());

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        // One Explorer window per containing folder.
        foreach (var group in SelectedItems().Where(i => i.Exists).GroupBy(i => Path.GetDirectoryName(i.Path) ?? i.Path))
        {
            StartProcess("explorer.exe", $"/select,\"{group.First().Path}\"");
        }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedItems().Where(i => i.Exists).Select(i => i.Path).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        var files = new StringCollection();
        files.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(files);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
        await ClipboardAccess.TrySetDataObjectAsync(data);
    }

    // ---- Tools (convert, resize, compress, zip) and OCR ----------------------------------------------

    /// <summary>Fills the image sub-menus for the current selection and shows only the tools that apply to it.</summary>
    private void PrepareMenu(ContextMenu menu)
    {
        var existing = SelectedItems().Where(i => i.Exists).ToList();
        var images = existing.Where(i => i.Kind == DropItemKind.File && ImageOps.IsImage(i.Path)).Select(i => i.Path).ToList();
        var zips = existing.Where(i => i.Kind == DropItemKind.File && ZipOps.IsZip(i.Path)).Select(i => i.Path).ToList();
        _menuAll = existing.Select(i => i.Path).ToList();
        _menuZips = zips;

        foreach (object entry in menu.Items)
        {
            if (entry is not FrameworkElement element)
            {
                continue;
            }

            switch (element.Tag as string)
            {
                case "convert" when element is MenuItem item:
                    item.Items.Clear();
                    AddTool(item, "PNG", ToolKind.ConvertPng, images);
                    AddTool(item, "JPEG", ToolKind.ConvertJpg, images);
                    element.Visibility = images.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "resize" when element is MenuItem item:
                    item.Items.Clear();
                    AddTool(item, "50%", ToolKind.Resize50, images);
                    AddTool(item, "25%", ToolKind.Resize25, images);
                    AddTool(item, "Max Width...", ToolKind.ResizeToWidth, images);
                    element.Visibility = images.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "compress" when element is MenuItem item:
                    item.Items.Clear();
                    AddTool(item, "JPEG, High Quality (80)", ToolKind.CompressJpg80, images);
                    AddTool(item, "JPEG, Smaller (60)", ToolKind.CompressJpg60, images);
                    element.Visibility = images.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "ocr":
                    element.Visibility = images.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "zip" when element is MenuItem item:
                    item.Header = _menuAll.Count > 1 ? $"Create ZIP of {_menuAll.Count} Items" : "Create ZIP";
                    element.Visibility = _menuAll.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "unzip":
                    element.Visibility = zips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    break;
            }
        }
    }

    private List<string> _menuAll = new();
    private List<string> _menuZips = new();

    private void Zip_Click(object sender, RoutedEventArgs e) => RunTool(ToolKind.Zip, _menuAll);

    private void Unzip_Click(object sender, RoutedEventArgs e) => RunTool(ToolKind.Unzip, _menuZips);

    private void AddTool(MenuItem parent, string header, ToolKind kind, List<string> paths)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => RunTool(kind, paths);
        parent.Items.Add(item);
    }

    /// <summary>Runs a tool on <paramref name="paths"/> in the background and puts the results in this list with a fly-in.</summary>
    private async void RunTool(ToolKind kind, List<string> paths)
    {
        try
        {
            if (ToolsService.Current is not { } tools || _store == null)
            {
                return;
            }

            int maxWidth = 0;
            if (kind == ToolKind.ResizeToWidth)
            {
                var prompt = new NumberPromptWindow("Resize to max width", "Width in pixels (images that are already narrower are skipped)", 1280, 16, 20000);
                if (prompt.ShowDialog() != true)
                {
                    return;
                }

                maxWidth = prompt.Value;
            }

            DropItem? first = List.SelectedItems.OfType<DropItem>().FirstOrDefault();
            FrameworkElement? source = first == null ? null : List.ItemContainerGenerator.ContainerFromItem(first) as FrameworkElement;
            ToolResult result = await tools.RunAsync(kind, paths, maxWidth);
            if (result.Outputs.Count == 0)
            {
                return;
            }

            // The new tiles fly in from where the source tile was.
            try
            {
                Point from = source != null && source.IsLoaded
                    ? source.TranslatePoint(new Point(source.ActualWidth / 2, source.ActualHeight / 2), this)
                    : new Point(ActualWidth / 2, ActualHeight / 2);
                _dropPoint = from;
                _dropTick = Environment.TickCount64;
            }
            catch (InvalidOperationException)
            {
                _dropPoint = null;
            }

            _store.AddPaths(result.Outputs);
        }
        catch (Exception ex)
        {
            Log.Error($"Tool {kind} failed", ex);
        }
    }

    private async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        var images = SelectedItems().Where(i => i.Exists && i.Kind == DropItemKind.File && ImageOps.IsImage(i.Path)).Select(i => i.Path).ToList();
        if (images.Count == 0)
        {
            return;
        }

        try
        {
            await OcrActions.CopyTextAsync(images);
        }
        catch (Exception ex)
        {
            Log.Error("Copy text (OCR) failed", ex);
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => _store?.Remove(SelectedItems());

    /// <summary>The grey badge on a hovered tile removes just that item.</summary>
    private void RemoveBadge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DropItem item })
        {
            _store?.Remove([item]);
        }

        e.Handled = true;
    }

    private void OpenItems(IEnumerable<DropItem> items)
    {
        foreach (var item in items)
        {
            if (!item.Exists)
            {
                _ = _store?.PruneMissingAsync();
                continue;
            }

            StartProcess(item.Path, null);
        }
    }

    private static void StartProcess(string fileName, string? arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName) { UseShellExecute = true };
            if (arguments != null)
            {
                psi.Arguments = arguments;
            }

            Process.Start(psi);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Error($"Could not start {fileName}", ex);
        }
    }
}
