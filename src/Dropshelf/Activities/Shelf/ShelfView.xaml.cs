using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dropshelf.Clipboard;

namespace Dropshelf.Activities.Shelf;

/// <summary>The Shelf tab: the item row (drag out, double-click to open, right-click for tools) and Copy All / Clear.</summary>
public partial class ShelfView : UserControl
{
    private readonly ShelfStore _store;

    internal ShelfView(ShelfStore store)
    {
        InitializeComponent();
        _store = store;
        Items.Store = store;
        store.Items.CollectionChanged += OnItemsChanged;
        Update();
    }

    /// <summary>A tile is being dragged out or its menu is open.</summary>
    public bool IsBusy => Items.IsBusy;

    internal void NoteDropPoint(Point point, Visual space) => Items.NoteDropPoint(point, space);

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Update();

    private void Update()
    {
        int n = _store.Count;
        CountText.Text = n == 0 ? string.Empty : n == 1 ? "1 item" : $"{n} items";
        EmptyState.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        Actions.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        string[] paths = _store.Items.Where(i => i.Exists).Select(i => i.Path).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        var files = new StringCollection();
        files.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(files);
        await ClipboardAccess.TrySetDataObjectAsync(data);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _store.Clear();
}
