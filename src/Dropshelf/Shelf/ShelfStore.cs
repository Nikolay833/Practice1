using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Threading;
using Dropshelf.Services;

namespace Dropshelf.Shelf;

/// <summary>The notch shelf's items, persisted to shelf.json when "Remember shelf items" is on.</summary>
public sealed class ShelfStore : ItemStore
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

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleSave();

    /// <summary>Call when the "Remember shelf items" setting flips.</summary>
    public void OnRememberChanged()
    {
        if (_settings.Current.RememberShelf)
        {
            Save();
        }
        else
        {
            JsonFile.Delete(AppPaths.ShelfFile);
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Load()
    {
        var saved = JsonFile.Load<List<ShelfItem>>(AppPaths.ShelfFile);
        if (saved == null)
        {
            return;
        }

        foreach (var item in saved.Where(i => !string.IsNullOrEmpty(i.Path)))
        {
            Items.Add(item);
        }

        PruneMissing();
    }

    public void Save()
    {
        if (!_settings.Current.RememberShelf)
        {
            return;
        }

        JsonFile.Save(AppPaths.ShelfFile, Items.ToList());
    }

    /// <summary>Writes pending changes immediately (called on exit).</summary>
    public void Flush()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
        }

        Save();
    }
}
