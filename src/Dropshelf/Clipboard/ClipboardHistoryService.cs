using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dropshelf.Interop;
using Dropshelf.Services;

namespace Dropshelf.Clipboard;

/// <summary>
/// Records what is copied (text, images, file lists) via AddClipboardFormatListener and keeps the newest N entries.
/// Pinned entries are never evicted. Skips content flagged by password managers and our own clipboard writes.
/// </summary>
public sealed class ClipboardHistoryService : IDisposable
{
    private static readonly uint ExcludeFormat = NativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint ViewerIgnoreFormat = NativeMethods.RegisterClipboardFormat("Clipboard Viewer Ignore");
    private static readonly uint CanIncludeFormat = NativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory");

    // Larger text is not recorded: it would bloat the in-memory list and the encrypted history file.
    private const int MaxTextChars = 1_000_000;

    private readonly MessageWindow _window;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _saveTimer;
    private bool _listening;
    private bool _capturing;
    private bool _recapture;

    public ClipboardHistoryService(MessageWindow window, SettingsService settings)
    {
        _window = window;
        _settings = settings;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };

        Load();
        Entries.CollectionChanged += OnEntriesChanged;
        _window.ClipboardUpdated += OnClipboardUpdated;
    }

    /// <summary>Newest first.</summary>
    public ObservableCollection<ClipboardEntry> Entries { get; } = new();

    public bool IsEnabled => _settings.Current.ClipboardHistoryEnabled;

    /// <summary>Starts/stops listening and trims to the configured size. Call after settings change.</summary>
    public void ApplySettings()
    {
        if (IsEnabled && !_listening)
        {
            _listening = NativeMethods.AddClipboardFormatListener(_window.Handle);
            if (!_listening)
            {
                Log.Error("AddClipboardFormatListener failed");
            }
        }
        else if (!IsEnabled && _listening)
        {
            NativeMethods.RemoveClipboardFormatListener(_window.Handle);
            _listening = false;
        }

        Trim();
    }

    // ---- Capture ----------------------------------------------------------------------------------

    private async void OnClipboardUpdated()
    {
        if (!IsEnabled)
        {
            return;
        }

        // Coalesce bursts of notifications into at most one follow-up capture.
        if (_capturing)
        {
            _recapture = true;
            return;
        }

        _capturing = true;
        try
        {
            do
            {
                _recapture = false;
                await CaptureAsync();
            }
            while (_recapture);
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard capture failed", ex);
        }
        finally
        {
            _capturing = false;
        }
    }

    private async Task CaptureAsync()
    {
        if (ClipboardAccess.IsSelfChange || IsOwnedByThisProcess() || await IsMarkedPrivateAsync())
        {
            return;
        }

        ClipboardEntry? entry = await ClipboardAccess.ReadAsync(ReadEntry);
        if (entry == null)
        {
            return;
        }

        // Encode images off the UI thread, and only once we know the entry is not a duplicate.
        if (entry.Kind == ClipboardEntryKind.Image && _pendingImage != null)
        {
            var frozen = _pendingImage;
            _pendingImage = null;
            var (bytes, hash) = await Task.Run(() => EncodePng(frozen));
            entry.Hash = hash;
            if (IsDuplicateOfNewest(entry))
            {
                return;
            }

            string fileName = entry.Id.ToString("N") + ".png";
            try
            {
                await File.WriteAllBytesAsync(Path.Combine(AppPaths.ClipboardDir, fileName), bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Could not save clipboard image", ex);
                return;
            }

            entry.ImageFile = fileName;
        }
        else if (IsDuplicateOfNewest(entry))
        {
            return;
        }

        Entries.Insert(0, entry);
        Trim();
    }

    private BitmapSource? _pendingImage;

    /// <summary>Reads the current clipboard (UI thread). Throws COMException while the clipboard is locked.</summary>
    private ClipboardEntry? ReadEntry()
    {
        _pendingImage = null;

        if (System.Windows.Clipboard.ContainsFileDropList())
        {
            var files = System.Windows.Clipboard.GetFileDropList().Cast<string>().ToList();
            if (files.Count == 0)
            {
                return null;
            }

            return new ClipboardEntry { Kind = ClipboardEntryKind.Files, Files = files, Hash = HashOf(string.Join('|', files)) };
        }

        if (System.Windows.Clipboard.ContainsText(TextDataFormat.UnicodeText))
        {
            string text = System.Windows.Clipboard.GetText(TextDataFormat.UnicodeText);
            if (text.Length > MaxTextChars)
            {
                Log.Info($"Clipboard text of {text.Length} chars is over the {MaxTextChars} limit; not recorded");
                return null;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                text = ReplaceLoneSurrogates(text);
                return new ClipboardEntry { Kind = ClipboardEntryKind.Text, Text = text, Hash = HashOf(text) };
            }
        }

        if (System.Windows.Clipboard.ContainsImage())
        {
            BitmapSource? source = System.Windows.Clipboard.GetImage();
            if (source != null)
            {
                // Copy into a freezable bitmap so the PNG can be encoded on a worker thread.
                var copy = new WriteableBitmap(source);
                copy.Freeze();
                _pendingImage = copy;
                return new ClipboardEntry { Kind = ClipboardEntryKind.Image, ImageWidth = copy.PixelWidth, ImageHeight = copy.PixelHeight };
            }
        }

        return null;
    }

    private static (byte[] Bytes, string Hash) EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        byte[] bytes = ms.ToArray();
        return (bytes, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    /// <summary>
    /// Replaces unpaired UTF-16 surrogates with U+FFFD. They cannot be serialized to JSON (the save would throw)
    /// and would be silently rewritten by UTF-8 hashing anyway. One cheap scan; the string is only copied when needed.
    /// </summary>
    private static string ReplaceLoneSurrogates(string text)
    {
        char[]? fixedChars = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++; // valid pair
                continue;
            }

            if (char.IsSurrogate(c))
            {
                fixedChars ??= text.ToCharArray();
                fixedChars[i] = '\uFFFD';
            }
        }

        return fixedChars == null ? text : new string(fixedChars);
    }

    private static string HashOf(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private bool IsDuplicateOfNewest(ClipboardEntry entry)
    {
        var newest = Entries.FirstOrDefault();
        return newest != null && newest.Kind == entry.Kind && newest.Hash == entry.Hash;
    }

    /// <summary>True when the clipboard owner window belongs to this process (we copied something ourselves).</summary>
    private static bool IsOwnedByThisProcess() => WindowHelper.IsOwnWindow(NativeMethods.GetClipboardOwner());

    /// <summary>
    /// Honours the clipboard-history opt-out formats set by password managers and similar tools:
    /// ExcludeClipboardContentFromMonitorProcessing, "Clipboard Viewer Ignore" and CanIncludeInClipboardHistory = 0.
    /// </summary>
    private async Task<bool> IsMarkedPrivateAsync()
    {
        if (NativeMethods.IsClipboardFormatAvailable(ExcludeFormat) || NativeMethods.IsClipboardFormatAvailable(ViewerIgnoreFormat))
        {
            return true;
        }

        if (!NativeMethods.IsClipboardFormatAvailable(CanIncludeFormat))
        {
            return false;
        }

        // Format present: it holds a DWORD, 0 = "do not include". If we cannot read it, err on the side of privacy.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.OpenClipboard(_window.Handle))
            {
                try
                {
                    IntPtr handle = NativeMethods.GetClipboardData(CanIncludeFormat);
                    if (handle == IntPtr.Zero || NativeMethods.GlobalSize(handle) < 4)
                    {
                        return true;
                    }

                    IntPtr ptr = NativeMethods.GlobalLock(handle);
                    if (ptr == IntPtr.Zero)
                    {
                        return true;
                    }

                    try
                    {
                        return Marshal.ReadInt32(ptr) == 0;
                    }
                    finally
                    {
                        NativeMethods.GlobalUnlock(handle);
                    }
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }

            await Task.Delay(15);
        }

        return true;
    }

    // ---- Maintenance -------------------------------------------------------------------------------

    /// <summary>Evicts the oldest unpinned entries beyond the configured size.</summary>
    public void Trim()
    {
        int max = _settings.Current.ClipboardHistorySize;
        while (Entries.Count > max)
        {
            var victim = Entries.LastOrDefault(e => !e.IsPinned);
            if (victim == null)
            {
                break;
            }

            Remove(victim);
        }
    }

    public void Remove(ClipboardEntry entry)
    {
        if (Entries.Remove(entry) && entry.Kind == ClipboardEntryKind.Image)
        {
            try
            {
                File.Delete(entry.ImagePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Could not delete {entry.ImagePath}", ex);
            }
        }
    }

    /// <summary>Marks the entry as most recent (after it was pasted again).</summary>
    public void Touch(ClipboardEntry entry)
    {
        entry.TimestampUtc = DateTime.UtcNow;
        int index = Entries.IndexOf(entry);
        if (index > 0)
        {
            Entries.Move(index, 0);
        }
    }

    public void NotifyPinChanged() => ScheduleSave();

    /// <summary>Puts an entry back on the clipboard (flagged so it is not re-recorded). Returns false if the clipboard stayed locked.</summary>
    public async Task<bool> SetClipboardAsync(ClipboardEntry entry)
    {
        var data = new DataObject();
        switch (entry.Kind)
        {
            case ClipboardEntryKind.Text:
                data.SetText(entry.Text ?? string.Empty, TextDataFormat.UnicodeText);
                break;
            case ClipboardEntryKind.Files:
                var existing = new StringCollection();
                existing.AddRange(entry.Files.Where(f => File.Exists(f) || Directory.Exists(f)).ToArray());
                if (existing.Count == 0)
                {
                    return false;
                }

                data.SetFileDropList(existing);
                break;
            case ClipboardEntryKind.Image:
                BitmapSource? image = await Task.Run(() => LoadFullImage(entry.ImagePath));
                if (image == null)
                {
                    return false;
                }

                data.SetImage(image);
                break;
        }

        bool ok = await ClipboardAccess.TrySetDataObjectAsync(data);
        if (ok)
        {
            Touch(entry);
        }

        return ok;
    }

    private static BitmapSource? LoadFullImage(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            Log.Error($"Could not load clipboard image {path}", ex);
            return null;
        }
    }

    // ---- Persistence -------------------------------------------------------------------------------

    private void Load()
    {
        var saved = JsonFile.LoadProtected<System.Collections.Generic.List<ClipboardEntry>>(AppPaths.ClipboardFile, out bool wasPlain);
        if (saved == null)
        {
            // The file exists but could not be read (DPAPI failure, empty or truncated): keep it before any save overwrites it.
            JsonFile.Quarantine(AppPaths.ClipboardFile);
            return;
        }

        foreach (var entry in saved)
        {
            if (entry.Kind == ClipboardEntryKind.Image && !File.Exists(entry.ImagePath))
            {
                continue; // the PNG vanished
            }

            Entries.Add(entry);
        }

        if (wasPlain)
        {
            ScheduleSave(); // rewrite the legacy plain-text file encrypted
        }
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleSave();

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>The history holds copied text, so the file is encrypted with DPAPI (current user).</summary>
    public void Save() => JsonFile.SaveProtected(AppPaths.ClipboardFile, Entries.ToList());

    /// <summary>Writes pending changes immediately (called on exit).</summary>
    public void Flush()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            Save();
        }
    }

    public void Dispose()
    {
        _window.ClipboardUpdated -= OnClipboardUpdated;
        if (_listening)
        {
            NativeMethods.RemoveClipboardFormatListener(_window.Handle);
            _listening = false;
        }

        Flush();
    }
}
