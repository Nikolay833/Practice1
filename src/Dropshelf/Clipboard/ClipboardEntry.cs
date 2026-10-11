using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dropshelf.Services;

namespace Dropshelf.Clipboard;

public enum ClipboardEntryKind
{
    Text,
    Image,
    Files,
}

/// <summary>One item of clipboard history (persisted in clipboard.json).</summary>
public sealed partial class ClipboardEntry : NotifyBase
{
    private bool _isPinned;
    private DateTime _timestampUtc = DateTime.UtcNow;
    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;
    private string? _preview;

    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ClipboardEntryKind Kind { get; set; }

    /// <summary>Text content (Kind = Text).</summary>
    public string? Text { get; set; }

    /// <summary>File name of the PNG inside the clipboard folder (Kind = Image).</summary>
    public string? ImageFile { get; set; }

    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    /// <summary>File paths (Kind = Files).</summary>
    public List<string> Files { get; set; } = new();

    /// <summary>Content hash used to drop consecutive duplicates.</summary>
    public string Hash { get; set; } = string.Empty;

    public bool IsPinned { get => _isPinned; set => SetField(ref _isPinned, value); }

    public DateTime TimestampUtc
    {
        get => _timestampUtc;
        set
        {
            if (SetField(ref _timestampUtc, value))
            {
                OnPropertyChanged(nameof(TimeText));
            }
        }
    }

    // ---- Presentation helpers (not persisted) -------------------------------------------------

    [JsonIgnore]
    public string ImagePath => ImageFile == null ? string.Empty : Path.Combine(AppPaths.ClipboardDir, ImageFile);

    [JsonIgnore]
    public string Preview => _preview ??= BuildPreview();

    [JsonIgnore]
    public string KindGlyph => Kind switch
    {
        ClipboardEntryKind.Image => "",
        ClipboardEntryKind.Files => "",
        _ => "",
    };

    /// <summary>Card title: "Text", "Image" or "Files".</summary>
    [JsonIgnore]
    public string KindTitle => Kind switch
    {
        ClipboardEntryKind.Image => "Image",
        ClipboardEntryKind.Files => Files.Count == 1 ? "File" : "Files",
        _ => "Text",
    };

    /// <summary>Card footer: "57 characters", "1628 × 1588" or "3 files".</summary>
    [JsonIgnore]
    public string Details => Kind switch
    {
        ClipboardEntryKind.Image => ImageWidth > 0 ? $"{ImageWidth} \u00D7 {ImageHeight}" : "Image",
        ClipboardEntryKind.Files => Files.Count == 1 ? "1 file" : $"{Files.Count} files",
        _ => (Text?.Length ?? 0) == 1 ? "1 character" : $"{Text?.Length ?? 0:N0} characters",
    };

    [JsonIgnore]
    public bool IsImage => Kind == ClipboardEntryKind.Image;

    [JsonIgnore]
    public string SearchText => Kind switch
    {
        ClipboardEntryKind.Text => Text ?? string.Empty,
        ClipboardEntryKind.Files => string.Join(' ', Files.Select(f => Path.GetFileName(f))),
        _ => "image",
    };

    [JsonIgnore]
    public string TimeText
    {
        get
        {
            var age = DateTime.UtcNow - _timestampUtc;
            if (age.TotalMinutes < 1) return "Just now";
            if (age.TotalHours < 1) return Ago((int)age.TotalMinutes, "minute");
            if (age.TotalDays < 1) return Ago((int)age.TotalHours, "hour");
            if (age.TotalDays < 7) return Ago((int)age.TotalDays, "day");
            return _timestampUtc.ToLocalTime().ToString("MMM d");
        }
    }

    private static string Ago(int n, string unit) => n == 1 ? $"1 {unit} ago" : $"{n} {unit}s ago";

    /// <summary>Re-raises the relative timestamp (called while the popup is open).</summary>
    public void RefreshTime() => OnPropertyChanged(nameof(TimeText));

    /// <summary>Lazy image thumbnail (Kind = Image only).</summary>
    [JsonIgnore]
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail == null && !_thumbnailRequested && Kind == ClipboardEntryKind.Image)
            {
                _thumbnailRequested = true;
                _ = LoadThumbnailAsync();
            }

            return _thumbnail;
        }
    }

    private async Task LoadThumbnailAsync()
    {
        string path = ImagePath;
        _thumbnail = await Task.Run(() => LoadThumbnail(path));
        OnPropertyChanged(nameof(Thumbnail));
    }

    private static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 160;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private string BuildPreview()
    {
        switch (Kind)
        {
            case ClipboardEntryKind.Text:
                string flat = Whitespace().Replace(Text ?? string.Empty, " ").Trim();
                return flat.Length > 200 ? flat[..200] + "…" : flat;
            case ClipboardEntryKind.Files:
                var names = Files.Select(f => Path.GetFileName(f)).Where(n => !string.IsNullOrEmpty(n)).ToList();
                string head = string.Join(", ", names.Take(3));
                return names.Count > 3 ? $"{head} +{names.Count - 3} more" : head;
            default:
                return ImageWidth > 0 ? $"Image  {ImageWidth} × {ImageHeight}" : "Image";
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
