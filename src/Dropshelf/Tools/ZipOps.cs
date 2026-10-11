using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Dropshelf.Tools;

internal sealed record ZipResult(string Path, int Entries, int Skipped);

/// <summary>An archive that looks like a zip bomb (too many entries, too much data, absurd compression ratio): extraction was stopped and cleaned up.</summary>
internal sealed class ZipSafetyException(string message) : IOException(message);

/// <summary>Extraction limits. <see cref="MaxTotalBytes"/> null = the smaller of 20 GB and the free space of the target drive minus 1 GB.</summary>
internal sealed record ZipLimits
{
    public int MaxEntries { get; init; } = 100_000;
    public long? MaxTotalBytes { get; init; }

    /// <summary>An entry is only judged by its ratio once it has produced this many bytes...</summary>
    public long RatioAfterBytes { get; init; } = 10L * 1024 * 1024;

    /// <summary>...and is stopped when output : compressed size is above this.</summary>
    public long MaxRatio { get; init; } = 1000;
}


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

    /// <summary>Raised inside <see cref="Create"/> when a source file could not be read halfway through: the archive is rebuilt without it.</summary>
    private sealed class SourceReadException(string sourcePath, Exception inner) : IOException("Could not read " + sourcePath, inner)
    {
        public string SourcePath { get; } = sourcePath;
    }

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

            // ZipArchiveEntry.Delete only works in Update mode (which buffers whole files in memory), so an entry cannot be taken
            // back once its copy failed halfway. Instead the archive is rewritten from scratch without the unreadable file: a
            // truncated entry never survives.
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int entries, skipped;
            while (true)
            {
                try
                {
                    (entries, skipped) = WriteArchive(output, plan, zipPath, excluded, total, progress);
                    break;
                }
                catch (SourceReadException ex) when (excluded.Count < 100)
                {
                    excluded.Add(ex.SourcePath);
                    output.SetLength(0);
                    output.Position = 0;
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
    /// Writes every planned item to <paramref name="output"/>. A file that cannot be opened is skipped and counted (nothing is
    /// added for it). A source that fails while being read throws <see cref="SourceReadException"/>; write errors (disk full) propagate.
    /// </summary>
    private static (int Entries, int Skipped) WriteArchive(
        FileStream output, List<(string FullPath, string EntryName, bool IsDirectory)> plan, string zipPath,
        HashSet<string> excluded, long total, Action<double>? progress)
    {
        long done = 0;
        int entries = 0, skipped = excluded.Count;
        var buffer = new byte[81920];
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
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

            if (excluded.Contains(item.FullPath))
            {
                continue; // failed while being read on an earlier pass; already counted
            }

            // Open and stat the file before an entry exists, so an unreadable file leaves nothing behind.
            FileStream input;
            DateTime lastWrite;
            try
            {
                input = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                try
                {
                    lastWrite = File.GetLastWriteTime(input.SafeFileHandle);
                }
                catch
                {
                    input.Dispose();
                    throw;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++; // locked or unreadable: leave it out, keep going
                continue;
            }

            using (input)
            {
                ZipArchiveEntry entry = archive.CreateEntry(item.EntryName, CompressionLevel.Optimal);
                entry.LastWriteTime = ClampTime(lastWrite);
                using Stream target = entry.Open();
                while (true)
                {
                    int read;
                    try
                    {
                        read = input.Read(buffer, 0, buffer.Length);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new SourceReadException(item.FullPath, ex);
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    target.Write(buffer, 0, read);
                    done += read;
                    if (total > 0)
                    {
                        progress?.Invoke(Math.Min(1, done / (double)total));
                    }
                }

                entries++;
            }
        }

        return (entries, skipped);
    }

    /// <summary>
    /// Extracts <paramref name="zipPath"/> into a new folder named after it, next to it (Downloads if that is not writable).
    /// Entries that would land outside that folder (".." or absolute paths) or that Windows cannot store safely (see
    /// <see cref="IsSafeEntryName"/>) are skipped, and existing files are never replaced. The archive's "downloaded from the
    /// internet" mark (Zone.Identifier) is copied to every extracted file. Archives that look like zip bombs
    /// (see <see cref="ZipLimits"/>) are stopped with a <see cref="ZipSafetyException"/> and the partial folder is removed.
    /// </summary>
    public static ZipResult Extract(string zipPath, Action<double>? progress = null, ZipLimits? limits = null)
    {
        limits ??= new ZipLimits();
        string name = Path.GetFileNameWithoutExtension(zipPath);
        string destination = OutputNaming.CreateUniqueDirectory(Path.GetDirectoryName(zipPath), name);
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count > limits.MaxEntries)
            {
                throw new ZipSafetyException($"Archive has too many files (more than {limits.MaxEntries:N0})");
            }

            long maxBytes = limits.MaxTotalBytes ?? DefaultMaxBytes(destination);
            byte[]? zone = ReadZoneIdentifier(zipPath);
            long total = Math.Max(1, archive.Entries.Sum(e => e.Length));
            long done = 0;
            int extracted = 0, skipped = 0;
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var buffer = new byte[81920];

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!IsSafeEntryName(entry.FullName))
                {
                    skipped++;
                    continue;
                }

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
                    long entryBytes = 0;
                    long compressed = Math.Max(1, entry.CompressedLength);
                    using (Stream input = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            // Checked on what is actually produced, not on the sizes the headers claim.
                            entryBytes += read;
                            done += read;
                            if (done > maxBytes)
                            {
                                throw new ZipSafetyException($"Archive would expand to more than {OutputNaming.FormatSize(maxBytes)} (disk space limit)");
                            }

                            if (entryBytes > limits.RatioAfterBytes && entryBytes / compressed > limits.MaxRatio)
                            {
                                throw new ZipSafetyException($"\"{entry.Name}\" expands more than {limits.MaxRatio}:1 (zip bomb?)");
                            }

                            output.Write(buffer, 0, read);
                            progress?.Invoke(Math.Min(1, done / (double)total));
                        }
                    }

                    CopyZoneIdentifier(zone, target);
                    extracted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException
                                           && ex is not ZipSafetyException)
                {
                    skipped++;
                }
            }

            progress?.Invoke(1);
            return new ZipResult(destination, extracted, skipped);
        }
        catch (ZipSafetyException)
        {
            TryDeleteDirectory(destination); // the folder was created for this run, so what is in it is only partial output
            throw;
        }
        catch
        {
            TryDeleteEmptyDirectory(destination);
            throw;
        }
    }

    /// <summary>Windows device names that cannot be used as file names, with or without an extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// False for an entry name Windows would treat as something other than a plain relative file: any ':' (drive letters, alternate
    /// data streams), a path segment that is a reserved device name (<c>CON</c>, <c>nul.txt</c>, <c>LPT1.tar.gz</c>...), or a
    /// segment ending in a dot or a space (Windows silently strips those, so <c>..</c> and <c>.. </c> would escape).
    /// </summary>
    public static bool IsSafeEntryName(string entryName)
    {
        if (entryName.Contains(':'))
        {
            return false;
        }

        foreach (string segment in entryName.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment[^1] is '.' or ' ')
            {
                return false;
            }

            int dot = segment.IndexOf('.');
            string stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
            if (ReservedNames.Contains(stem))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The smaller of 20 GB and the free space of the target drive minus 1 GB (just 20 GB when the drive cannot be asked).</summary>
    private static long DefaultMaxBytes(string destination)
    {
        const long Gb = 1024L * 1024 * 1024;
        long max = 20 * Gb;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(destination));
            if (!string.IsNullOrEmpty(root))
            {
                max = Math.Min(max, Math.Max(0, new DriveInfo(root).AvailableFreeSpace - Gb));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A network path or similar: only the fixed cap applies.
        }

        return max;
    }

    /// <summary>The "Mark of the Web" of the archive (<c>zip:Zone.Identifier</c>), or null when it has none (or the file system has no streams).</summary>
    private static byte[]? ReadZoneIdentifier(string zipPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(zipPath + ":Zone.Identifier");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Best effort: files that came out of a downloaded archive keep the download mark, so SmartScreen still checks them.</summary>
    private static void CopyZoneIdentifier(byte[]? zone, string target)
    {
        if (zone == null)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(target + ":Zone.Identifier", zone);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // FAT / network shares have no streams: nothing to do.
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
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
