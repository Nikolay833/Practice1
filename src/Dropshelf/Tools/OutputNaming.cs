using System;
using System.IO;

namespace Dropshelf.Tools;

/// <summary>
/// Where tool output goes and what it is called. Output lands next to the source when that folder is writable, otherwise in
/// <c>%USERPROFILE%\Downloads</c>. A file is only ever created with <see cref="FileMode.CreateNew"/>, so an existing file
/// (the original included) can never be overwritten; a taken name gets " (2)", " (3)"... appended.
/// </summary>
internal static class OutputNaming
{
    public static string DownloadsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Creates a new empty file called <c>baseName + extension</c> (made unique) and opens it for writing.</summary>
    /// <param name="preferredDir">The source's folder; null or unwritable falls back to Downloads.</param>
    public static FileStream CreateUniqueFile(string? preferredDir, string baseName, string extension, out string path)
    {
        baseName = SafeName(baseName);
        foreach (string dir in Candidates(preferredDir))
        {
            try
            {
                if (dir != preferredDir)
                {
                    Directory.CreateDirectory(dir);
                }

                for (int i = 1; i < 10_000; i++)
                {
                    string candidate = Path.Combine(dir, i == 1 ? baseName + extension : $"{baseName} ({i}){extension}");
                    try
                    {
                        var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        path = candidate;
                        return stream;
                    }
                    catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate))
                    {
                        // Name taken: try the next one.
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException or ArgumentException or NotSupportedException)
            {
                // Not writable (read-only share, Program Files...): try the next folder.
            }
        }

        throw new IOException("No writable folder for the result.");
    }

    /// <summary>Creates a new empty folder called <paramref name="name"/> (made unique) and returns its path.</summary>
    public static string CreateUniqueDirectory(string? preferredDir, string name)
    {
        name = SafeName(name);
        foreach (string dir in Candidates(preferredDir))
        {
            try
            {
                if (dir != preferredDir)
                {
                    Directory.CreateDirectory(dir);
                }

                for (int i = 1; i < 10_000; i++)
                {
                    string candidate = Path.Combine(dir, i == 1 ? name : $"{name} ({i})");
                    if (File.Exists(candidate) || Directory.Exists(candidate))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(candidate);
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
            {
                // Try the next folder.
            }
        }

        throw new IOException("No writable folder for the result.");
    }

    private static string[] Candidates(string? preferredDir) =>
        string.IsNullOrEmpty(preferredDir) ? [DownloadsDir] : [preferredDir, DownloadsDir];

    /// <summary>A file-name-safe version of <paramref name="name"/> (illegal characters become spaces; never empty).</summary>
    public static string SafeName(string name)
    {
        var chars = name.ToCharArray();
        char[] invalid = Path.GetInvalidFileNameChars();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0 || char.IsControl(chars[i]))
            {
                chars[i] = ' ';
            }
        }

        string cleaned = new string(chars).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Output" : cleaned;
    }

    /// <summary>"5.3 MB", "812 KB"...</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = Math.Abs(bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} B" : $"{value:0.#} {units[unit]}";
    }
}
