using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Dropshelf.Activities.Hud;
using Dropshelf.Services;

namespace Dropshelf.Activities.Downloads;

/// <summary>
/// Browser downloads on the island. Browsers write a download into the Downloads folder under a temporary name
/// (Chrome / Edge / Brave: <c>.crdownload</c>, Firefox: <c>.part</c>, Opera: <c>.opdownload</c>) and rename it when it is
/// done, so a <see cref="FileSystemWatcher"/> on that folder sees both. While something downloads the island shows a spinning
/// ring with a download arrow and how much has arrived (browsers do not write the total size anywhere, so the ring spins
/// instead of filling). When it finishes, a HUD shows the file: drag its icon out of the island, or click it to open it; it is
/// also put on the shelf when that setting is on.
/// </summary>
internal sealed class DownloadActivity : PillActivityBase, IDisposable
{
    private static readonly string[] TempExtensions = { ".crdownload", ".part", ".partial", ".opdownload", ".download" };

    private sealed class Entry
    {
        public string Name = string.Empty;
        public long Bytes;
        public long LastBytes;
        public DateTime LastSeen = DateTime.UtcNow;
        public double BytesPerSecond;
    }

    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, Entry> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _poll;
    private readonly ProgressRing _ring;
    private readonly ProgressRing _miniRing;
    private readonly TextBlock _amount;
    private readonly FrameworkElement _left;
    private readonly FrameworkElement _right;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public DownloadActivity(SettingsService settings)
    {
        _settings = settings;
        _ring = new ProgressRing(22, 2.2, "", 9);
        _ring.SetColor(System.Windows.Media.Color.FromRgb(0x0A, 0x84, 0xFF));
        _ring.SetIndeterminate();
        _left = HudParts.Cell(_ring, 26);
        _amount = HudParts.Label(string.Empty, 12.5, "TextBrush", FontWeights.SemiBold);
        _amount.TextTrimming = TextTrimming.None;
        _amount.MinWidth = 64;
        _amount.TextAlignment = TextAlignment.Right;
        _right = _amount;

        _miniRing = new ProgressRing(20, 2, "", 8);
        _miniRing.SetColor(System.Windows.Media.Color.FromRgb(0x0A, 0x84, 0xFF));
        _miniRing.SetIndeterminate();

        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        _poll.Tick += (_, _) => Refresh();
        ApplySettings();
    }

    public override string Id => "download";
    public override int Priority => 150;
    public override bool IsActive => _active.Count > 0;

    /// <summary>The "Downloaded" HUD (its own activity, so it can show while other downloads keep running).</summary>
    public DownloadDoneActivity Done { get; } = new();

    /// <summary>A download finished (the final file path).</summary>
    public event Action<string>? Finished;

    public override CompactContent? CreateCompact() => _active.Count > 0 ? CompactContent.Of(_left, _right) : null;

    public override FrameworkElement? CreateMinimal() => _active.Count > 0 ? _miniRing : null;

    /// <summary>Starts or stops watching the Downloads folder to match the setting.</summary>
    public void ApplySettings()
    {
        if (_settings.Current.ShowDownloads)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    private void Start()
    {
        if (_watcher != null)
        {
            return;
        }

        string? folder = DownloadsFolder();
        if (folder == null || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                InternalBufferSize = 32 * 1024,
            };
            watcher.Created += (_, e) => Post(() => OnTouched(e.FullPath));
            watcher.Changed += (_, e) => Post(() => OnTouched(e.FullPath));
            watcher.Renamed += (_, e) => Post(() => OnRenamed(e.OldFullPath, e.FullPath));
            watcher.Deleted += (_, e) => Post(() => OnDeleted(e.FullPath));
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Log.Error("Could not watch the Downloads folder", ex);
        }
    }

    private void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _poll.Stop();
        if (_active.Count > 0)
        {
            _active.Clear();
            RaiseStateChanged();
        }
    }

    private void Post(Action action)
    {
        if (!_disposed)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed)
                {
                    action();
                }
            }));
        }
    }

    private static bool IsTemp(string path) =>
        TempExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>"report.pdf.crdownload" -> "report.pdf"; Chrome's first name "Unconfirmed 123.crdownload" -> "".</summary>
    private static string NameOf(string tempPath)
    {
        string name = Path.GetFileName(tempPath);
        string ext = TempExtensions.First(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        name = name[..^ext.Length];
        return name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) ? string.Empty : name;
    }

    private void OnTouched(string path)
    {
        if (!IsTemp(path))
        {
            return;
        }

        if (!_active.ContainsKey(path))
        {
            _active[path] = new Entry { Name = NameOf(path) };
            _poll.Start();
            Refresh();
            RaiseStateChanged();
        }
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        bool wasTracked = _active.Remove(oldPath, out Entry? entry);
        if (IsTemp(newPath))
        {
            entry ??= new Entry();
            entry.Name = NameOf(newPath);
            _active[newPath] = entry;
            _poll.Start();
            if (!wasTracked)
            {
                RaiseStateChanged();
            }

            return;
        }

        if (wasTracked)
        {
            Complete(newPath);
        }
    }

    private void OnDeleted(string path)
    {
        if (_active.Remove(path))
        {
            // Cancelled, or Firefox replacing its placeholder: the rename (if any) arrives separately.
            UpdateText();
            RaiseStateChanged();
        }
    }

    /// <summary>Reads the sizes, works out the speed, drops downloads that vanished without a word.</summary>
    private void Refresh()
    {
        DateTime now = DateTime.UtcNow;
        foreach ((string path, Entry entry) in _active.ToList())
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    if ((now - entry.LastSeen).TotalSeconds > 5)
                    {
                        _active.Remove(path);
                    }

                    continue;
                }

                double seconds = Math.Max(0.2, (now - entry.LastSeen).TotalSeconds);
                long bytes = info.Length;
                entry.BytesPerSecond = 0.6 * entry.BytesPerSecond + 0.4 * Math.Max(0, bytes - entry.LastBytes) / seconds;
                entry.LastBytes = bytes;
                entry.Bytes = bytes;
                entry.LastSeen = now;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked for a moment: try again next time.
            }
        }

        if (_active.Count == 0)
        {
            _poll.Stop();
            RaiseStateChanged();
            return;
        }

        UpdateText();
    }

    private void UpdateText()
    {
        long total = _active.Values.Sum(e => e.Bytes);
        _amount.Text = _active.Count == 1 ? FormatSize(total) : $"{_active.Count} · {FormatSize(total)}";
    }

    private void Complete(string path)
    {
        if (_active.Count == 0)
        {
            _poll.Stop();
        }

        UpdateText();
        RaiseStateChanged();
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Finished?.Invoke(path);
        Done.Show(path);
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "KB", "MB", "GB" };
        double value = bytes / 1000.0;
        int unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return value < 10 ? $"{value.ToString("0.0", CultureInfo.CurrentCulture)} {units[unit]}" : $"{Math.Round(value)} {units[unit]}";
    }

    private static string? DownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr path) == 0 && path != IntPtr.Zero)
        {
            try
            {
                return Marshal.PtrToStringUni(path);
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }

        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(fallback) ? fallback : null;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}

/// <summary>"Downloaded": the file's icon (drag it out, or click to open) and its name, for six seconds.</summary>
internal sealed class DownloadDoneActivity : PillActivityBase
{
    private CompactContent? _content;

    public override string Id => "download-done";
    public override int Priority => 260;
    public override PillActivityKind Kind => PillActivityKind.Hud;
    public override TimeSpan HudDuration => TimeSpan.FromSeconds(6);
    public override bool QueueHudWhileExpanded => true;

    public override CompactContent? CreateCompact() => _content;

    public void Show(string path)
    {
        var tile = new DragOutTile(path);
        var name = HudParts.Label(Path.GetFileName(path), 12.5, "TextBrush", FontWeights.SemiBold);
        name.MaxWidth = 150;
        var done = HudParts.Label("Downloaded", 10.5, "SuccessBrush", FontWeights.SemiBold);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, done } };
        _content = CompactContent.Of(tile, text, extraHeight: 12, interactive: true);
        RequestHud();
    }
}
