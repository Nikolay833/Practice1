using System;
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Dropshelf.Services;

/// <summary>User-configurable options. Bound directly by the settings window.</summary>
public sealed class AppSettings : NotifyBase
{
    private bool _launchAtStartup;
    private bool _rememberShelf = true;
    private bool _shakeToOpenBasket = true;
    private bool _clipboardHistoryEnabled = true;
    private int _clipboardHistorySize = 50;
    private bool _expandShelfOnHover = true;

    public bool LaunchAtStartup { get => _launchAtStartup; set => SetField(ref _launchAtStartup, value); }
    public bool RememberShelf { get => _rememberShelf; set => SetField(ref _rememberShelf, value); }
    public bool ShakeToOpenBasket { get => _shakeToOpenBasket; set => SetField(ref _shakeToOpenBasket, value); }
    public bool ClipboardHistoryEnabled { get => _clipboardHistoryEnabled; set => SetField(ref _clipboardHistoryEnabled, value); }

    public int ClipboardHistorySize
    {
        get => _clipboardHistorySize;
        set => SetField(ref _clipboardHistorySize, Math.Clamp(value, MinHistorySize, MaxHistorySize));
    }

    public bool ExpandShelfOnHover { get => _expandShelfOnHover; set => SetField(ref _expandShelfOnHover, value); }

    [JsonIgnore] public static int MinHistorySize => 10;
    [JsonIgnore] public static int MaxHistorySize => 500;
}

/// <summary>Loads/saves <see cref="AppSettings"/> and tells the app when something changed.</summary>
public sealed class SettingsService
{
    private readonly DispatcherTimer _saveTimer;

    public SettingsService()
    {
        Current = JsonFile.Load<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
        Current.PropertyChanged += OnSettingChanged;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };
    }

    public AppSettings Current { get; }

    /// <summary>Raised on the UI thread with the name of the changed property.</summary>
    public event Action<string>? Changed;

    public void Save() => JsonFile.Save(AppPaths.SettingsFile, Current);

    /// <summary>Writes any pending change immediately (called on exit).</summary>
    public void Flush()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            Save();
        }
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke(e.PropertyName ?? string.Empty);
    }
}
