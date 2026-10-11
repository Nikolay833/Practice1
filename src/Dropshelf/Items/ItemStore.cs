using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Dropshelf.Services;

namespace Dropshelf.Items;

/// <summary>An observable list of <see cref="DropItem"/>s with the add/remove rules used by the basket.</summary>
public class ItemStore
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher; // stores are created on the UI thread
    private volatile bool _pruning;

    public ObservableCollection<DropItem> Items { get; } = new();

    /// <summary>
    /// Raised just before items leave <see cref="Items"/>, while their tiles still exist, so the UI can animate the removal
    /// (shrink + fade a snapshot of the tile).
    /// </summary>
    public event Action<IReadOnlyList<DropItem>>? Removing;

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

            Items.Add(new DropItem
            {
                Path = path,
                DisplayName = GetDisplayName(path),
                Kind = isDir ? DropItemKind.Folder : DropItemKind.File,
            });
            added++;
        }

        return added;
    }

    /// <summary>Stores dropped text as a .txt file so it can be dragged out like any other file.</summary>
    public DropItem? AddText(string text)
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

        var item = new DropItem { Path = path, DisplayName = display, Kind = DropItemKind.Text };
        Items.Add(item);
        return item;
    }

    /// <summary>Stores a dropped link as an Internet Shortcut (.url) file.</summary>
    public DropItem? AddUrl(string url)
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

        var item = new DropItem { Path = path, DisplayName = display, Kind = DropItemKind.Url };
        Items.Add(item);
        return item;
    }

    /// <summary>Moves already-built items into this store.</summary>
    public void AddRange(IEnumerable<DropItem> items)
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
    public void Remove(IEnumerable<DropItem> items, bool deleteOwnedFiles = true)
    {
        var list = items.ToList();
        var present = list.Where(Items.Contains).ToList();
        if (present.Count > 0)
        {
            Removing?.Invoke(present);
        }

        foreach (var item in list)
        {
            if (Items.Remove(item) && deleteOwnedFiles)
            {
                DeleteOwnedFile(item);
            }
        }
    }

    public void Clear(bool deleteOwnedFiles = true) => Remove(Items.ToList(), deleteOwnedFiles);

    /// <summary>
    /// Drops items whose file vanished from disk and returns how many were removed. The file checks run on a worker
    /// thread (they can stall on slow or offline volumes); the removal happens on the UI thread. Items on a volume
    /// that is not available right now (unplugged USB drive, offline network share) are kept.
    /// </summary>
    public async Task<int> PruneMissingAsync()
    {
        if (_pruning)
        {
            return 0;
        }

        _pruning = true;
        try
        {
            var snapshot = Items.ToList();
            var missing = await Task.Run(() => FindMissing(snapshot));
            if (missing.Count == 0)
            {
                return 0;
            }

            // Back on the dispatcher thread whatever context the caller had.
            return await _dispatcher.InvokeAsync(() =>
            {
                var stillListed = missing.Where(Items.Contains).ToList();
                Remove(stillListed, deleteOwnedFiles: false);
                return stillListed.Count;
            });
        }
        catch (Exception ex)
        {
            Log.Error("Pruning missing items failed", ex);
            return 0;
        }
        finally
        {
            _pruning = false;
        }
    }

    private static List<DropItem> FindMissing(List<DropItem> items)
    {
        var volumes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<DropItem>();
        foreach (var item in items)
        {
            string? root = VolumeRoot(item.Path);
            if (root == null)
            {
                continue; // UNC or unusable path: never prune
            }

            if (!volumes.TryGetValue(root, out bool available))
            {
                available = Directory.Exists(root);
                volumes[root] = available;
            }

            if (available && !item.Exists)
            {
                missing.Add(item);
            }
        }

        return missing;
    }

    /// <summary>Root of a local drive path, or null for UNC / unparsable paths (those are never pruned).</summary>
    private static string? VolumeRoot(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            bool unc = root is { Length: >= 2 } && IsSeparator(root[0]) && IsSeparator(root[1]);
            return string.IsNullOrEmpty(root) || unc ? null : root;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

    private static void DeleteOwnedFile(DropItem item)
    {
        if (item.Kind is DropItemKind.Text or DropItemKind.Url && AppPaths.IsInside(item.Path, AppPaths.ItemsDir))
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
