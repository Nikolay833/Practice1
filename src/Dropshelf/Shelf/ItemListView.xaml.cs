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
using Dropshelf.Clipboard;
using Dropshelf.Services;

namespace Dropshelf.Shelf;

/// <summary>
/// Reusable list of item tiles (shelf: one scrolling row; basket: wrapping grid) with multi-select,
/// drag-out, double-click open and a context menu.
/// </summary>
public partial class ItemListView : UserControl
{
    public static readonly DependencyProperty IsWrapProperty = DependencyProperty.Register(
        nameof(IsWrap), typeof(bool), typeof(ItemListView),
        new PropertyMetadata(false, (d, _) => ((ItemListView)d).ApplyLayout()));

    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(
        nameof(TileWidth), typeof(double), typeof(ItemListView), new PropertyMetadata(84.0));

    private ItemStore? _store;
    private ScrollViewer? _scroll;
    private Point _pressPoint;
    private ShelfItem? _pressedItem;
    private bool _deferSingleSelect;
    private bool _menuOpen;
    private bool _dragging;

    public ItemListView()
    {
        InitializeComponent();
        ApplyLayout();
    }

    /// <summary>Wrap tiles into a vertical grid instead of a single horizontal row.</summary>
    public bool IsWrap { get => (bool)GetValue(IsWrapProperty); set => SetValue(IsWrapProperty, value); }

    public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }

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
            _store = value;
            List.ItemsSource = value?.Items;
        }
    }

    private void ApplyLayout()
    {
        if (List == null)
        {
            return;
        }

        var factory = new FrameworkElementFactory(IsWrap ? typeof(WrapPanel) : typeof(StackPanel));
        if (!IsWrap)
        {
            factory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        }

        List.ItemsPanel = new ItemsPanelTemplate(factory);
        ScrollViewer.SetHorizontalScrollBarVisibility(List, IsWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(List, IsWrap ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
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

    private IReadOnlyList<ShelfItem> SelectedItems() => List.SelectedItems.OfType<ShelfItem>().ToList();

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedItem = null;
        _deferSingleSelect = false;

        var container = ContainerAt(e.OriginalSource);
        if (container?.DataContext is not ShelfItem item)
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

    private void StartDrag(ShelfItem pressed)
    {
        if (_store == null)
        {
            return;
        }

        var items = List.SelectedItems.Contains(pressed) ? SelectedItems() : new List<ShelfItem> { pressed };
        var existing = items.Where(i => i.Exists).ToList();
        if (existing.Count == 0)
        {
            _store.PruneMissing();
            return;
        }

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, existing.Select(i => i.Path).ToArray());

        DragDropEffects result = DragDropEffects.None;
        DragState.Begin(_store, existing);
        SetBusy(dragging: true);
        try
        {
            result = DragDrop.DoDragDrop(List, data, DragDropEffects.Copy | DragDropEffects.Move);
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
        // drop landed in another Dropshelf store; Remove is a no-op then.)
        if (result.HasFlag(DragDropEffects.Move) || (RemoveOnAnyDrop && result != DragDropEffects.None))
        {
            _store.Remove(existing, deleteOwnedFiles: false);
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

    private void Menu_Opened(object sender, RoutedEventArgs e) => SetBusy(menu: true);

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

    private void Copy_Click(object sender, RoutedEventArgs e)
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
        ClipboardAccess.TrySetDataObject(data);
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => _store?.Remove(SelectedItems());

    private void OpenItems(IEnumerable<ShelfItem> items)
    {
        foreach (var item in items)
        {
            if (!item.Exists)
            {
                _store?.PruneMissing();
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
