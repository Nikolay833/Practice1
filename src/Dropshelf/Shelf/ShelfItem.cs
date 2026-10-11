using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Media;
using Dropshelf.Services;

namespace Dropshelf.Shelf;

public enum ShelfItemKind
{
    File,
    Folder,
    Text,
    Url,
}

/// <summary>
/// One entry on the shelf or in the basket. Files are referenced by their original path (never copied);
/// text and URL items are materialised as .txt / .url files in the Items folder.
/// </summary>
public sealed class ShelfItem : NotifyBase
{
    private ImageSource? _icon;
    private bool _iconRequested;
    private bool _isThumbnail;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ShelfItemKind Kind { get; set; }

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Lazy icon or image thumbnail. Reading it kicks off a background load.</summary>
    [JsonIgnore]
    public ImageSource? Icon
    {
        get
        {
            if (_icon == null && !_iconRequested)
            {
                _iconRequested = true;
                _ = LoadIconAsync();
            }

            return _icon;
        }
    }

    /// <summary>True when <see cref="Icon"/> is a real picture preview rather than a shell icon.</summary>
    [JsonIgnore]
    public bool IsThumbnail
    {
        get => _isThumbnail;
        private set => SetField(ref _isThumbnail, value);
    }

    [JsonIgnore]
    public bool Exists => Kind == ShelfItemKind.Folder ? Directory.Exists(Path) : File.Exists(Path);

    private async Task LoadIconAsync()
    {
        try
        {
            var (image, thumbnail) = await IconLoader.LoadAsync(Path, Kind == ShelfItemKind.Folder);
            _icon = image;
            IsThumbnail = thumbnail;
            OnPropertyChanged(nameof(Icon));
        }
        catch (Exception ex)
        {
            Log.Error($"Icon load failed for {Path}", ex);
        }
    }
}
