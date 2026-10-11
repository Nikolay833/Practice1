using System;
using System.IO;

namespace Dropshelf.Services;

/// <summary>Well-known locations under %LOCALAPPDATA%\Dropshelf.</summary>
internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropshelf");

    /// <summary>Text / URL basket items are materialised as files here so they can be dragged out.</summary>
    public static string ItemsDir { get; } = Path.Combine(Root, "Items");

    /// <summary>PNG files of captured clipboard images.</summary>
    public static string ClipboardDir { get; } = Path.Combine(Root, "Clipboard");

    public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");
    /// <summary>Saved list of the removed shelf feature; deleted at start-up if an older version left it behind.</summary>
    public static string ShelfFile { get; } = Path.Combine(Root, "shelf.json");
    public static string ClipboardFile { get; } = Path.Combine(Root, "clipboard.json");
    public static string NotesFile { get; } = Path.Combine(Root, "notes.json");
    public static string TimerFile { get; } = Path.Combine(Root, "timer.json");
    public static string CalendarFile { get; } = Path.Combine(Root, "calendar.json");   // ICS URLs (encrypted)
    public static string WeatherFile { get; } = Path.Combine(Root, "weather.json");     // last forecast + location (encrypted)
    public static string RestartsFile { get; } = Path.Combine(Root, "restarts.json");
    public static string LogFile { get; } = Path.Combine(Root, "dropshelf.log");

    public static void EnsureDirectories()
    {
        try
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(ItemsDir);
            Directory.CreateDirectory(ClipboardDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not create data directories", ex);
        }
    }

    /// <summary>True when <paramref name="path"/> lives inside <paramref name="dir"/>.</summary>
    public static bool IsInside(string path, string dir)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
