using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Dropshelf.Tools;

internal sealed record ZipResult(string Path, int Entries, int Skipped);

/// <summary>Zip creation and extraction on top of System.IO.Compression. Never overwrites anything and never writes outside its target folder.</summary>
internal static class ZipOps
{
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // never follow junctions / symlinks out of the tree
        ReturnSpecialDirectories = false,
    };

    public static bool IsZip(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>"photo.zip" for one item, "Archive.zip" for several.</summary>
    public static string ArchiveBaseName(IReadOnlyList<string> sources)
    {
        if (sources.Count != 1)
        {
            return "Archive";
        }

        string only = sources[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Directory.Exists(only) ? Path.GetFileName(only) : Path.GetFileNameWithoutExtension(only);
        return string.IsNullOrWhiteSpace(name) ? "Archive" : name;
    }

    /// <summary>
    /// Zips files and folders into a new archive. Files that cannot be read (locked, no access) are skipped and counted.
    /// <paramref name="progress"/> receives 0..1 by bytes. On failure the partial archive is deleted.
    /// </summary>
    public static ZipResult Create(IReadOnlyList<string> sources, Action<double>? progress = null)
    {
        string? preferredDir = Path.GetDirectoryName(sources[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        using FileStream output = OutputNaming.CreateUniqueFile(preferredDir, ArchiveBaseName(sources), ".zip", out string zipPath);
        try
        {
            // Plan first (cheap), so progress can be by bytes and the archive's own path is excluded.
            var plan = new List<(string FullPath, string EntryName, bool IsDirectory)>();
            var usedTopLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string source in sources)
            {
                string trimmed = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                bool isDirectory = Directory.Exists(trimmed);
                if (!isDirectory && !File.Exists(trimmed))
                {
                    continue;
                }

                string top = UniqueTopLevel(Path.GetFileName(trimmed), usedTopLevel);
                if (!isDirectory)
                {
                    plan.Add((trimmed, top, false));
                    continue;
                }

                plan.Add((trimmed, top + "/", true));
                foreach (string entry in Directory.EnumerateFileSystemEntries(trimmed, "*", Walk))
                {
                    string relative = Path.GetRelativePath(trimmed, entry).Replace('\\', '/');
                    bool entryIsDirectory = Directory.Exists(entry);
                    plan.Add((entry, top + "/" + relative + (entryIsDirectory ? "/" : string.Empty), entryIsDirectory));
                }
            }

            long total = 0;
            foreach (var item in plan)
            {
                if (!item.IsDirectory && !SamePath(item.FullPath, zipPath))
                {
                    total += SafeLength(item.FullPath);
                }
            }

            long done = 0;
            int entries = 0, skipped = 0;
            var buffer = new byte[81920];
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var item in plan)
                {
                    if (item.IsDirectory)
                    {
                        archive.CreateEntry(item.EntryName);
                        entries++;
                        continue;
                    }

                    if (SamePath(item.FullPath, zipPath))
                    {
                        continue; // the archive being written
                    }

                    try
                    {
                        using var input = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        ZipArchiveEntry entry = archive.CreateEntry(item.EntryName, CompressionLevel.Optimal);
                        entry.LastWriteTime = ClampTime(File.GetLastWriteTime(item.FullPath));
                        using Stream target = entry.Open();
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            target.Write(buffer, 0, read);
                            done += read;
                            if (total > 0)
                            {
                                progress?.Invoke(Math.Min(1, done / (double)total));
                            }
                        }

                        entries++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped++; // locked or unreadable: leave it out, keep going
                    }
                }
            }

            progress?.Invoke(1);
            return new ZipResult(zipPath, entries, skipped);
        }
        catch
        {
            output.Dispose();
            TryDelete(zipPath);
            throw;
        }
    }

    /// <summary>
    /// Extracts <paramref name="zipPath"/> into a new folder named after it, next to it (Downloads if that is not writable).
    /// Entries that would land outside that folder (".." or absolute paths) are skipped, and existing files are never replaced.
    /// </summary>
    public static ZipResult Extract(string zipPath, Action<double>? progress = null)
    {
        string name = Path.GetFileNameWithoutExtension(zipPath);
        string destination = OutputNaming.CreateUniqueDirectory(Path.GetDirectoryName(zipPath), name);
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            long total = Math.Max(1, archive.Entries.Sum(e => e.Length));
            long done = 0;
            int extracted = 0, skipped = 0;
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var buffer = new byte[81920];

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string target;
                try
                {
                    target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    skipped++;
                    continue;
                }

                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++; // zip-slip: "../../x" or an absolute path
                    continue;
                }

                try
                {
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using Stream input = entry.Open();
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        done += read;
                        progress?.Invoke(Math.Min(1, done / (double)total));
                    }

                    extracted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
                {
                    skipped++;
                }
            }

            progress?.Invoke(1);
            return new ZipResult(destination, extracted, skipped);
        }
        catch
        {
            TryDeleteEmptyDirectory(destination);
            throw;
        }
    }

    private static string UniqueTopLevel(string name, HashSet<string> used)
    {
        name = name.Length == 0 ? "Folder" : name;
        string candidate = name;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 2; !used.Add(candidate); i++)
        {
            candidate = $"{stem} ({i}){ext}";
        }

        return candidate;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Zip timestamps must be 1980..2107.</summary>
    private static DateTimeOffset ClampTime(DateTime time)
    {
        var min = new DateTime(1980, 1, 1);
        var max = new DateTime(2107, 12, 31);
        return new DateTimeOffset(time < min ? min : time > max ? max : time);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave it.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave it.
        }
    }
}
