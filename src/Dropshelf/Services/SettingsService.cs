using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Dropshelf.Animation;

namespace Dropshelf.Services;

/// <summary>
/// Accent colour choices: Apple's system colours (dark appearance). <see cref="Windows"/> follows the Windows accent colour
/// live. Saved by name, so new values are appended.
/// </summary>
public enum AccentPreset
{
    Blue,
    Indigo,
    Purple,
    Pink,
    Red,
    Orange,
    Green,
    Teal,
    Windows,
    Yellow,
    Graphite,
}

/// <summary>User-configurable options. Bound directly by the settings window.</summary>
public sealed class AppSettings : NotifyBase
{
    private bool _launchAtStartup = true; // on by default: Dropshelf is meant to be always running
    private bool _shakeToOpenBasket = true;
    private bool _clipboardHistoryEnabled = true;
    private int _clipboardHistorySize = 50;
    private bool _expandOnHover = true;
    private AnimationMode? _animations;
    private bool _showNowPlaying = true;
    private bool _showVolumeHud = true;
    private bool _showBrightnessHud = true;
    private bool _replaceWindowsOsd = true;
    private bool _scrollToChangeVolume = true;
    private bool _showBatteryHud = true;
    private bool _showBluetoothHud = true;
    private bool _hideInFullscreen = true;
    private bool _showVolumeHudInFullscreen;
    private AccentPreset _accent = AccentPreset.Blue;
    private bool _screenshotHotkeyEnabled = true;
    private int _pomodoroWorkMinutes = 25;
    private int _pomodoroBreakMinutes = 5;
    private int _pomodoroLongBreakMinutes = 15;
    private int _pomodoroRounds = 4;
    private bool _timerSound = true;
    private bool _weatherEnabled;
    private bool _weatherUseLocation = true;
    private string _weatherCity = string.Empty;
    private bool _weatherFahrenheit = !System.Globalization.RegionInfo.CurrentRegion.IsMetric;
    private bool _showWeatherOnPill;
    private bool _calendarEnabled = true;
    private bool _calendarUseWindows = true;
    private bool _meetingAlerts = true;
    private bool _notificationsEnabled;
    private bool _hideNotificationContent;
    private bool _notificationsDndFullscreen = true;
    private List<string> _notificationMutedApps = new();
    private Dictionary<string, string> _notificationKnownApps = new();
    private bool _armNoticeShown;
    private bool _armNoticeHidden;
    private bool _portableCleanupOffered;

    public bool LaunchAtStartup { get => _launchAtStartup; set => SetField(ref _launchAtStartup, value); }
    public bool ShakeToOpenBasket { get => _shakeToOpenBasket; set => SetField(ref _shakeToOpenBasket, value); }
    public bool ClipboardHistoryEnabled { get => _clipboardHistoryEnabled; set => SetField(ref _clipboardHistoryEnabled, value); }

    public int ClipboardHistorySize
    {
        get => _clipboardHistorySize;
        set => SetField(ref _clipboardHistorySize, Math.Clamp(value, MinHistorySize, MaxHistorySize));
    }

    /// <summary>Resting the cursor on the island opens it (otherwise a click does). Keeps its old JSON name.</summary>
    [JsonPropertyName("ExpandShelfOnHover")]
    public bool ExpandOnHover { get => _expandOnHover; set => SetField(ref _expandOnHover, value); }

    /// <summary>
    /// Animation level chosen by the user. Null (never chosen) means Full, unless Windows' "Animation effects" is off,
    /// in which case it means Subtle; see <see cref="AnimationSettings"/>.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AnimationMode? Animations { get => _animations; set => SetField(ref _animations, value); }

    // ---- Media & HUD ----
    public bool ShowNowPlaying { get => _showNowPlaying; set => SetField(ref _showNowPlaying, value); }
    public bool ShowVolumeHud { get => _showVolumeHud; set => SetField(ref _showVolumeHud, value); }
    public bool ShowBrightnessHud { get => _showBrightnessHud; set => SetField(ref _showBrightnessHud, value); }

    /// <summary>Best effort: hides the native Windows volume / brightness popup while Dropshelf shows its own.</summary>
    public bool ReplaceWindowsOsd { get => _replaceWindowsOsd; set => SetField(ref _replaceWindowsOsd, value); }

    /// <summary>Mouse wheel over the collapsed pill changes the system volume.</summary>
    public bool ScrollToChangeVolume { get => _scrollToChangeVolume; set => SetField(ref _scrollToChangeVolume, value); }

    public bool ShowBatteryHud { get => _showBatteryHud; set => SetField(ref _showBatteryHud, value); }
    public bool ShowBluetoothHud { get => _showBluetoothHud; set => SetField(ref _showBluetoothHud, value); }

    // ---- Fullscreen ----
    public bool HideInFullscreen { get => _hideInFullscreen; set => SetField(ref _hideInFullscreen, value); }
    public bool ShowVolumeHudInFullscreen { get => _showVolumeHudInFullscreen; set => SetField(ref _showVolumeHudInFullscreen, value); }

    // ---- Appearance ----
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AccentPreset Accent { get => _accent; set => SetField(ref _accent, value); }

    // ---- Screenshot ----
    /// <summary>Ctrl+Shift+S opens the region capture overlay.</summary>
    public bool ScreenshotHotkeyEnabled { get => _screenshotHotkeyEnabled; set => SetField(ref _screenshotHotkeyEnabled, value); }

    // ---- Timer / Pomodoro ----
    public int PomodoroWorkMinutes { get => _pomodoroWorkMinutes; set => SetField(ref _pomodoroWorkMinutes, Math.Clamp(value, 1, 180)); }
    public int PomodoroBreakMinutes { get => _pomodoroBreakMinutes; set => SetField(ref _pomodoroBreakMinutes, Math.Clamp(value, 1, 60)); }
    public int PomodoroLongBreakMinutes { get => _pomodoroLongBreakMinutes; set => SetField(ref _pomodoroLongBreakMinutes, Math.Clamp(value, 1, 120)); }
    public int PomodoroRounds { get => _pomodoroRounds; set => SetField(ref _pomodoroRounds, Math.Clamp(value, 1, 12)); }

    /// <summary>Play the soft chime when a timer finishes.</summary>
    public bool TimerSound { get => _timerSound; set => SetField(ref _timerSound, value); }

    // ---- Calendar & weather ----
    /// <summary>Off by default: turning it on sends the approximate location (or the city) to open-meteo.com.</summary>
    public bool WeatherEnabled { get => _weatherEnabled; set => SetField(ref _weatherEnabled, value); }

    /// <summary>Ask Windows for the device location first; the city below is the fallback (or the only source when this is off).</summary>
    public bool WeatherUseLocation { get => _weatherUseLocation; set => SetField(ref _weatherUseLocation, value); }

    public string WeatherCity { get => _weatherCity; set => SetField(ref _weatherCity, value ?? string.Empty); }

    /// <summary>False = degrees Celsius. The default follows the region (metric = Celsius).</summary>
    public bool WeatherFahrenheit { get => _weatherFahrenheit; set => SetField(ref _weatherFahrenheit, value); }

    /// <summary>Show the current temperature on the collapsed pill when nothing else is active.</summary>
    public bool ShowWeatherOnPill { get => _showWeatherOnPill; set => SetField(ref _showWeatherOnPill, value); }

    public bool CalendarEnabled { get => _calendarEnabled; set => SetField(ref _calendarEnabled, value); }

    /// <summary>Read the Windows calendars (appointment store). Only works in the installed (MSIX) version.</summary>
    public bool CalendarUseWindows { get => _calendarUseWindows; set => SetField(ref _calendarUseWindows, value); }

    /// <summary>"Meeting in 5 min" on the pill for events starting within 10 minutes.</summary>
    public bool MeetingAlerts { get => _meetingAlerts; set => SetField(ref _meetingAlerts, value); }

    // ---- Notifications in the pill (MSIX build only) ----
    /// <summary>Show Windows toast notifications on the pill. Off by default: turning it on makes Windows ask for access to all notifications.</summary>
    public bool NotificationsEnabled { get => _notificationsEnabled; set => SetField(ref _notificationsEnabled, value); }

    /// <summary>Show only the app name and "New notification" instead of the title and text.</summary>
    public bool HideNotificationContent { get => _hideNotificationContent; set => SetField(ref _hideNotificationContent, value); }

    /// <summary>No notifications on the pill while a fullscreen app is in front (needs "Hide in fullscreen apps", which does the detection).</summary>
    public bool NotificationsDndFullscreen { get => _notificationsDndFullscreen; set => SetField(ref _notificationsDndFullscreen, value); }

    /// <summary>AppUserModelIDs of the apps the user muted. Replaced as a whole on change (use <see cref="SetNotificationMuted"/>).</summary>
    public List<string> NotificationMutedApps { get => _notificationMutedApps; set => SetField(ref _notificationMutedApps, value ?? new List<string>()); }

    /// <summary>Apps that have sent a notification so far: AppUserModelID to display name, for the mute list in Settings.</summary>
    public Dictionary<string, string> NotificationKnownApps
    {
        get => _notificationKnownApps;
        set => SetField(ref _notificationKnownApps, value ?? new Dictionary<string, string>());
    }

    public bool IsNotificationMuted(string appId) => _notificationMutedApps.Contains(appId, StringComparer.OrdinalIgnoreCase);

    public void SetNotificationMuted(string appId, bool muted)
    {
        if (IsNotificationMuted(appId) == muted)
        {
            return;
        }

        var list = _notificationMutedApps.Where(a => !string.Equals(a, appId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (muted)
        {
            list.Add(appId);
        }

        NotificationMutedApps = list;
    }

    /// <summary>Remembers an app that sent a notification. Returns true when it was new or renamed.</summary>
    public bool RememberNotificationApp(string appId, string name)
    {
        if (_notificationKnownApps.TryGetValue(appId, out string? known) && known == name)
        {
            return false;
        }

        if (!_notificationKnownApps.ContainsKey(appId) && _notificationKnownApps.Count >= MaxKnownNotificationApps)
        {
            return false; // a sane cap; the oldest entries stay
        }

        NotificationKnownApps = new Dictionary<string, string>(_notificationKnownApps) { [appId] = name };
        return true;
    }

    // ---- One-time notices ----
    /// <summary>The "you're on an Arm PC" pill notice has been shown once.</summary>
    public bool ArmNoticeShown { get => _armNoticeShown; set => SetField(ref _armNoticeShown, value); }

    /// <summary>"Don't show again": hides the Arm notice in Settings too.</summary>
    public bool ArmNoticeHidden { get => _armNoticeHidden; set => SetField(ref _armNoticeHidden, value); }

    /// <summary>The MSIX build already offered to remove the portable copy's autostart entry.</summary>
    public bool PortableCleanupOffered { get => _portableCleanupOffered; set => SetField(ref _portableCleanupOffered, value); }

    [JsonIgnore] public static int MaxKnownNotificationApps => 100;
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
