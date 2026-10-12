using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Threading;
using Dropshelf.Items;
using Dropshelf.Services;

namespace Dropshelf.Activities.Shelf;

/// <summary>
/// The island shelf's items. Saved (encrypted, like the notes) to shelf.json half a second after a change while "Remember
/// shelf items" is on, and loaded at start-up; items whose file vanished meanwhile are dropped.
/// </summary>
internal sealed class ShelfStore : ItemStore
{
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _saveTimer;

    public ShelfStore(SettingsService settings)
    {
        _settings = settings;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };

        if (_settings.Current.RememberShelf)
        {
            Load();
        }

        Items.CollectionChanged += OnItemsChanged;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>"Remember shelf items" flipped: save now, or forget the saved list.</summary>
    public void OnRememberChanged()
    {
        if (_settings.Current.RememberShelf)
        {
            Save();
        }
        else
        {
            _saveTimer.Stop();
            JsonFile.Delete(AppPaths.ShelfFile);
        }
    }

    private void Load()
    {
        List<DropItem>? saved = JsonFile.LoadProtected<List<DropItem>>(AppPaths.ShelfFile, out bool wasPlain);
        if (saved == null)
        {
            return;
        }

        foreach (DropItem item in saved.Where(i => !string.IsNullOrEmpty(i.Path)))
        {
            Items.Add(item);
        }

        if (wasPlain)
        {
            Save(); // a list from an older version: store it encrypted from now on
        }

        _ = PruneMissingAsync(); // off the UI thread; drops files that vanished while the app was closed
    }

    private void Save()
    {
        if (_settings.Current.RememberShelf)
        {
            JsonFile.SaveProtected(AppPaths.ShelfFile, Items.ToList());
        }
    }

    /// <summary>Writes a pending change immediately (exit, log-off).</summary>
    public void Flush()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            Save();
        }
    }
}
