using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Dropshelf.Services;

namespace Dropshelf.Shelf;

/// <summary>An observable list of <see cref="ShelfItem"/>s with the add/remove rules shared by shelf and basket.</summary>
public class ItemStore
{
    public ObservableCollection<ShelfItem> Items { get; } = new();

    public int Count => Items.Count;

    /// <summary>Adds files/folders by reference. Missing paths and duplicates are skipped. Returns the number added.</summary>
    public int AddPaths(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            bool isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path))
            {
                continue;
            }

            if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Items.Add(new ShelfItem
            {
                Path = path,
                DisplayName = GetDisplayName(path),
                Kind = isDir ? ShelfItemKind.Folder : ShelfItemKind.File,
            });
            added++;
        }

        return added;
    }

    /// <summary>Stores dropped text as a .txt file so it can be dragged out like any other file.</summary>
    public ShelfItem? AddText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string firstLine = text.Trim().Split('\n', '\r')[0].Trim();
        string display = Truncate(firstLine, 40);
        string path = WriteOwnedFile(FileSafe(Truncate(firstLine, 24), "Text"), ".txt", text, new UTF8Encoding(false));
        if (path.Length == 0)
        {
            return null;
        }

        var item = new ShelfItem { Path = path, DisplayName = display, Kind = ShelfItemKind.Text };
        Items.Add(item);
        return item;
    }

    /// <summary>Stores a dropped link as an Internet Shortcut (.url) file.</summary>
    public ShelfItem? AddUrl(string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return AddText(url);
        }

        string display = Truncate(uri.Host + uri.PathAndQuery.TrimEnd('/'), 40);
        string content = $"[InternetShortcut]\r\nURL={uri.AbsoluteUri}\r\n";
        string path = WriteOwnedFile(FileSafe(uri.Host, "Link"), ".url", content, Encoding.ASCII);
        if (path.Length == 0)
        {
            return null;
        }

        var item = new ShelfItem { Path = path, DisplayName = display, Kind = ShelfItemKind.Url };
        Items.Add(item);
        return item;
    }

    /// <summary>Moves already-built items into this store (used for basket to shelf transfers).</summary>
    public void AddRange(IEnumerable<ShelfItem> items)
    {
        foreach (var item in items)
        {
            if (Items.Any(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Items.Add(item);
        }
    }

    /// <summary>
    /// Removes items. With <paramref name="deleteOwnedFiles"/> the .txt/.url files we created for text and
    /// link items are deleted too (not wanted when the file was dragged away or handed to another store).
    /// </summary>
    public void Remove(IEnumerable<ShelfItem> items, bool deleteOwnedFiles = true)
    {
        foreach (var item in items.ToList())
        {
            if (Items.Remove(item) && deleteOwnedFiles)
            {
                DeleteOwnedFile(item);
            }
        }
    }

    public void Clear(bool deleteOwnedFiles = true) => Remove(Items.ToList(), deleteOwnedFiles);

    /// <summary>Drops items whose file vanished from disk. Returns how many were removed.</summary>
    public int PruneMissing()
    {
        var missing = Items.Where(i => !i.Exists).ToList();
        Remove(missing, deleteOwnedFiles: false);
        return missing.Count;
    }

    private static void DeleteOwnedFile(ShelfItem item)
    {
        if (item.Kind is ShelfItemKind.Text or ShelfItemKind.Url && AppPaths.IsInside(item.Path, AppPaths.ItemsDir))
        {
            try
            {
                File.Delete(item.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Could not delete {item.Path}", ex);
            }
        }
    }

    private static string WriteOwnedFile(string baseName, string extension, string content, Encoding encoding)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ItemsDir);
            string path = Path.Combine(AppPaths.ItemsDir, baseName + extension);
            for (int i = 2; File.Exists(path); i++)
            {
                path = Path.Combine(AppPaths.ItemsDir, $"{baseName} ({i}){extension}");
            }

            File.WriteAllText(path, content, encoding);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error("Could not write item file", ex);
            return string.Empty;
        }
    }

    private static string GetDisplayName(string path)
    {
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.Length > 0 ? name : path; // drive roots like "C:\"
    }

    private static string FileSafe(string name, string fallback)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || char.IsControl(c) ? ' ' : c);
        }

        string cleaned = sb.ToString().Trim().TrimEnd('.');
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "\u2026";
}
