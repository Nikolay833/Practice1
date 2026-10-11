using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dropshelf.Services;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Dropshelf.Activities.Media;

/// <summary>
/// Tracks the system's current media session (Windows.Media.Control): title, artist, album art, playback status and the
/// timeline, which is interpolated locally between updates. WinRT raises its events on other threads; they are marshalled
/// to the UI thread, where <see cref="Changed"/> is raised. If the API is not available (or fails to start),
/// <see cref="IsAvailable"/> stays false and the service does nothing.
/// </summary>
internal sealed class MediaSessionService : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _propsVersion;
    private bool _disposed;
    private TimeSpan _timelineStart;
    private TimeSpan _anchorPosition;
    private DateTimeOffset _anchorTime = DateTimeOffset.UtcNow;
    private double _rate = 1;

    public bool IsAvailable { get; private set; }

    public bool HasSession { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Artist { get; private set; } = string.Empty;
    public string Album { get; private set; } = string.Empty;
    public string SourceApp { get; private set; } = string.Empty;
    public GlobalSystemMediaTransportControlsSessionPlaybackStatus Status { get; private set; } = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;

    /// <summary>Full-size art (decoded to at most 300 px wide) and a tiny version (24 px) that is stretched into a soft glow.</summary>
    public ImageSource? Art { get; private set; }
    public ImageSource? ArtTiny { get; private set; }

    /// <summary>A bright colour taken from the artwork for the waveform (like the Dynamic Island), or null for none.</summary>
    public Color? ArtTint { get; private set; }

    public bool CanPlayPause { get; private set; }
    public bool CanNext { get; private set; }
    public bool CanPrevious { get; private set; }
    public bool CanSeek { get; private set; }
    public TimeSpan Duration { get; private set; }

    public bool IsPlaying => Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    /// <summary>Anything about the session changed (properties, art, playback state, timeline). Raised on the UI thread.</summary>
    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed)
            {
                return;
            }

            IsAvailable = true;
            _manager.CurrentSessionChanged += (_, _) => Post(Refresh);
            _manager.SessionsChanged += (_, _) => Post(Refresh);
            Refresh();
        }
        catch (Exception ex)
        {
            Log.Error("Media session manager unavailable; now playing is disabled", ex);
            IsAvailable = false;
        }
    }

    /// <summary>Playback position now, interpolated from the last timeline update while playing.</summary>
    public TimeSpan GetPosition()
    {
        TimeSpan position = _anchorPosition;
        if (IsPlaying)
        {
            position += TimeSpan.FromSeconds((DateTimeOffset.UtcNow - _anchorTime).TotalSeconds * _rate);
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        return Duration > TimeSpan.Zero && position > Duration ? Duration : position;
    }

    // ---- Commands ---------------------------------------------------------------------------------

    public Task<bool> TogglePlayPauseAsync() => Run(s => s.TryTogglePlayPauseAsync());
    public Task<bool> NextAsync() => Run(s => s.TrySkipNextAsync());
    public Task<bool> PreviousAsync() => Run(s => s.TrySkipPreviousAsync());

    public async Task<bool> SeekAsync(TimeSpan position)
    {
        if (!CanSeek)
        {
            return false;
        }

        bool ok = await Run(s => s.TryChangePlaybackPositionAsync((_timelineStart + position).Ticks));
        if (ok)
        {
            _anchorPosition = position;
            _anchorTime = DateTimeOffset.UtcNow;
            Changed?.Invoke();
        }

        return ok;
    }

    private async Task<bool> Run(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> command)
    {
        GlobalSystemMediaTransportControlsSession? session = _session;
        if (session == null)
        {
            return false;
        }

        try
        {
            return await command(session);
        }
        catch (Exception ex)
        {
            Log.Error("Media command failed", ex);
            return false;
        }
    }

    // ---- Session tracking -------------------------------------------------------------------------

    private void Post(Action action)
    {
        if (!_disposed)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (!_disposed)
                {
                    action();
                }
            }));
        }
    }

    /// <summary>Follows the system's current session, unless another one is playing and the current one is not.</summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager == null)
        {
            return null;
        }

        GlobalSystemMediaTransportControlsSession? current = _manager.GetCurrentSession();
        if (current != null && IsPlayingStatus(current))
        {
            return current;
        }

        GlobalSystemMediaTransportControlsSession? playing = _manager.GetSessions().FirstOrDefault(IsPlayingStatus);
        return playing ?? current;
    }

    private static bool IsPlayingStatus(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Refresh()
    {
        GlobalSystemMediaTransportControlsSession? next;
        try
        {
            next = PickSession();
        }
        catch (Exception ex)
        {
            Log.Error("Reading media sessions failed", ex);
            next = null;
        }

        if (!ReferenceEquals(next, _session))
        {
            Detach(_session);
            _session = next;
            Attach(next);
            if (next == null)
            {
                Clear();
                return;
            }

            _ = LoadPropertiesAsync();
        }

        if (_session != null)
        {
            ReadPlayback();
            ReadTimeline();
            Changed?.Invoke();
        }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session == null)
        {
            return;
        }

        session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        session.TimelinePropertiesChanged += OnTimelineChanged;
    }

    private void Detach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session == null)
        {
            return;
        }

        try
        {
            session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        catch (Exception ex)
        {
            Log.Error("Detaching from a media session failed", ex);
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        Post(() =>
        {
            if (ReferenceEquals(sender, _session))
            {
                _ = LoadPropertiesAsync();
            }
        });

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        Post(() =>
        {
            if (!ReferenceEquals(sender, _session))
            {
                return;
            }

            // Another app may have started playing while this one paused: re-pick.
            Refresh();
        });

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        Post(() =>
        {
            if (ReferenceEquals(sender, _session))
            {
                ReadTimeline();
                Changed?.Invoke();
            }
        });

    private void Clear()
    {
        _propsVersion++;
        HasSession = false;
        Title = Artist = Album = SourceApp = string.Empty;
        Status = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        Art = ArtTiny = null;
        ArtTint = null;
        CanPlayPause = CanNext = CanPrevious = CanSeek = false;
        Duration = TimeSpan.Zero;
        _anchorPosition = TimeSpan.Zero;
        Changed?.Invoke();
    }

    private void ReadPlayback()
    {
        GlobalSystemMediaTransportControlsSession? session = _session;
        if (session == null)
        {
            return;
        }

        try
        {
            GlobalSystemMediaTransportControlsSessionPlaybackInfo info = session.GetPlaybackInfo();
            Status = info.PlaybackStatus;
            _rate = info.PlaybackRate ?? 1.0;
            GlobalSystemMediaTransportControlsSessionPlaybackControls c = info.Controls;
            CanPlayPause = c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled;
            CanNext = c.IsNextEnabled;
            CanPrevious = c.IsPreviousEnabled;
            CanSeek = c.IsPlaybackPositionEnabled;
            SourceApp = FriendlyAppName(session.SourceAppUserModelId);
        }
        catch (Exception ex)
        {
            Log.Error("Reading playback info failed", ex);
        }
    }

    private void ReadTimeline()
    {
        GlobalSystemMediaTransportControlsSession? session = _session;
        if (session == null)
        {
            return;
        }

        try
        {
            GlobalSystemMediaTransportControlsSessionTimelineProperties t = session.GetTimelineProperties();
            _timelineStart = t.StartTime;
            Duration = t.EndTime > t.StartTime ? t.EndTime - t.StartTime : TimeSpan.Zero;
            _anchorPosition = t.Position - t.StartTime;
            _anchorTime = t.LastUpdatedTime == default ? DateTimeOffset.UtcNow : t.LastUpdatedTime;
        }
        catch (Exception ex)
        {
            Log.Error("Reading the media timeline failed", ex);
        }
    }

    private async Task LoadPropertiesAsync()
    {
        GlobalSystemMediaTransportControlsSession? session = _session;
        if (session == null)
        {
            return;
        }

        int version = ++_propsVersion;
        try
        {
            GlobalSystemMediaTransportControlsSessionMediaProperties props = await session.TryGetMediaPropertiesAsync();
            string title = props.Title ?? string.Empty;
            string artist = props.Artist ?? string.Empty;
            string album = props.AlbumTitle ?? string.Empty;

            ImageSource? art = null, tiny = null;
            Color? tint = null;
            if (props.Thumbnail != null)
            {
                byte[]? bytes = await ReadBytesAsync(props.Thumbnail);
                if (bytes is { Length: > 0 })
                {
                    (art, tiny, tint) = await Task.Run(() =>
                    {
                        ImageSource? small = Decode(bytes, 24);
                        return (Decode(bytes, 300), small, TintOf(small as BitmapSource));
                    });
                }
            }

            if (_disposed || version != _propsVersion || !ReferenceEquals(session, _session))
            {
                return; // superseded by a newer load or another session
            }

            HasSession = true;
            Title = title;
            Artist = artist;
            Album = album;
            Art = art;
            ArtTiny = tiny;
            ArtTint = tint;
            ReadPlayback();
            ReadTimeline();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Reading media properties failed", ex);
        }
    }

    internal static async Task<byte[]?> ReadBytesAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 8 * 1024 * 1024)
            {
                return null;
            }

            using var reader = new DataReader(stream);
            uint size = (uint)stream.Size;
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception)
        {
            return null; // some apps hand out a thumbnail reference that cannot be opened
        }
    }

    internal static ImageSource? Decode(byte[] bytes, int pixelWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = pixelWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null; // not an image WPF can decode
        }
    }

    /// <summary>
    /// The artwork's dominant vivid colour: pixels are averaged weighted by their saturation, then lifted so the colour reads on
    /// black. Greyscale artwork gives null (the waveform is white then). Runs on a worker thread; the bitmap is frozen.
    /// </summary>
    internal static Color? TintOf(BitmapSource? source)
    {
        if (source == null)
        {
            return null;
        }

        try
        {
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixels = new byte[w * h * 4];
            bgra.CopyPixels(pixels, w * 4, 0);

            double r = 0, g = 0, b = 0, weight = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                double pb = pixels[i] / 255.0, pg = pixels[i + 1] / 255.0, pr = pixels[i + 2] / 255.0;
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                double saturation = max <= 0 ? 0 : (max - min) / max;
                double wgt = saturation * saturation * max + 0.0001;
                r += pr * wgt;
                g += pg * wgt;
                b += pb * wgt;
                weight += wgt;
            }

            var mean = Color.FromRgb((byte)(255 * r / weight), (byte)(255 * g / weight), (byte)(255 * b / weight));
            Themes.ColorUtil.ToHsv(mean, out double hue, out double sat, out _);
            if (sat < 0.18)
            {
                return null;
            }

            return Themes.ColorUtil.FromHsv(hue, Math.Min(0.75, sat * 1.15), 1);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>"Spotify.exe" gives "Spotify", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" the app's display name.</summary>
    private static string FriendlyAppName(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return string.Empty;
        }

        try
        {
            if (aumid.Contains('!') && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                string? name = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(aumid)?.DisplayInfo.DisplayName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }
        catch (Exception)
        {
            // Not a packaged app, or no access: derive a name below.
        }

        string raw = aumid;
        int bang = raw.IndexOf('!');
        if (bang >= 0)
        {
            raw = raw[(bang + 1)..];
        }

        if (raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[..^4];
        }

        int dot = raw.LastIndexOf('.');
        if (dot >= 0 && dot < raw.Length - 1)
        {
            raw = raw[(dot + 1)..];
        }

        return raw.ToLowerInvariant() switch
        {
            "msedge" => "Microsoft Edge",
            "chrome" => "Google Chrome",
            "firefox" => "Firefox",
            "zunemusic" => "Media Player",
            "spotify" => "Spotify",
            _ => raw.Length > 0 ? char.ToUpperInvariant(raw[0]) + raw[1..] : string.Empty,
        };
    }

    public void Dispose()
    {
        _disposed = true;
        Detach(_session);
        _session = null;
    }
}
