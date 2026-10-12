using System;
using System.Windows;
using Dropshelf.Activities.Notes;
using Dropshelf.Items;
using Dropshelf.Services;

namespace Dropshelf.Activities.Shelf;

/// <summary>
/// The "Shelf" tab: a parking spot for files, folders, text and links. Drag something onto the notch and the island opens
/// on this tab to take it; open the island later and drag it back out. Files are kept by reference (nothing is copied) and
/// are always copied out, never moved.
/// </summary>
internal sealed class ShelfActivity : PillActivityBase, IIslandDropTarget, IKeepsIslandOpen, IDisposable
{
    private readonly SettingsService _settings;
    private ShelfView? _view;

    public ShelfActivity(SettingsService settings)
    {
        _settings = settings;
        Store = new ShelfStore(settings);
    }

    public ShelfStore Store { get; }

    public override string Id => "shelf";
    public override int Priority => 5;
    public override string? TabTitle => "Shelf";
    public override int TabOrder => -100; // first, like Droppy
    public override bool HasExpanded => _settings.Current.ShelfEnabled;

    /// <summary>True while a tile is being dragged out or its menu is open: the island stays open.</summary>
    public bool KeepsIslandOpen => _view?.IsBusy == true;

    public void ApplySettings() => RaiseStateChanged();

    public override FrameworkElement? CreateExpanded() => _view = new ShelfView(Store);

    public bool CanDrop(IDataObject data) =>
        _settings.Current.ShelfEnabled
        && !(DragState.IsActive && ReferenceEquals(DragState.Origin, Store))
        && DropHelper.CanAccept(data);

    public bool Drop(IDataObject data) => DropHelper.Accept(data, Store) > 0;

    /// <summary>New tiles fly in from where the drop landed (a point in <paramref name="space"/>).</summary>
    public void NoteDropPoint(Point point, System.Windows.Media.Visual space) => _view?.NoteDropPoint(point, space);

    public void Dispose() => Store.Flush();
}
