using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Dropshelf.Activities;
using Dropshelf.Activities.Calendar;
using Dropshelf.Activities.Devices;
using Dropshelf.Activities.Downloads;
using Dropshelf.Activities.Hud;
using Dropshelf.Activities.Media;
using Dropshelf.Activities.Notifications;
using Dropshelf.Activities.Power;
using Dropshelf.Activities.Terminal;
using Dropshelf.Activities.Weather;
using Dropshelf.Animation;
using Dropshelf.Basket;
using Dropshelf.Clipboard;
using Dropshelf.Interop;
using Dropshelf.Island;
using Dropshelf.Screenshot;
using Dropshelf.Services;
using Dropshelf.Settings;
using Dropshelf.Items;
using Dropshelf.Themes;
using Dropshelf.Tools;

namespace Dropshelf;

/// <summary>
/// Composition root: owns the services and the (mostly hidden) windows, enforces a single instance and wires
/// settings changes to live behaviour. It also keeps the app always running: self-install, autostart, crash relaunch.
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _quitEvent;
    private RegisteredWaitHandle? _quitWait;
    private bool _uninstalling;
    private bool _deleteDataOnUninstall;
    private SettingsService? _settings;
    // The terminal-only build leaves most of these unset; the code that uses them stays null-safe so a feature can come back.
#pragma warning disable CS0649, CS0169
    private ItemStore? _basketStore;
    private MessageWindow? _messages;
    private HotkeyService? _hotkeys;
    private ClipboardHistoryService? _clipboard;
    private MouseHook? _mouseHook;
    private ShakeDetector? _shake;
    private TrayService? _tray;
    private IslandWindow? _island;
    private BasketWindow? _basketWindow;
    private ClipboardWindow? _clipboardWindow;
    private SettingsWindow? _settingsWindow;
    private bool _clipboardHotkeyRegistered;
    private bool _volumeKeysOwned;
    private ThemeService? _theme;
    private ActivityHost? _activityHost;
    private AudioEndpointService? _audio;
    private MediaSessionService? _media;
    private NowPlayingActivity? _nowPlaying;
    private VolumeActivity? _volume;
    private IndicatorActivity? _indicator;
    private BrightnessActivity? _brightness;
    private BatteryActivity? _battery;
    private BluetoothActivity? _bluetooth;
    private OsdSuppressor? _osd;
    private FullscreenWatcher? _fullscreen;
    private ToastActivity? _toast;
    private ProgressActivity? _progress;
    private ToolsService? _tools;
    private TerminalActivity? _terminal;
    private DownloadActivity? _downloads;
    private PrivacyMonitor? _privacy;
    private CalendarService? _calendar;
    private WeatherService? _weather;
    private MeetingActivity? _meeting;
    private WeatherPillActivity? _weatherPill;
    private ScreenshotService? _screenshot;
    private bool _screenshotHotkeyRegistered;
    private NotificationService? _notifications;
    private NotificationActivity? _notificationPill;
    private ArmNotice? _armNotice;
    private DispatcherTimer? _armNoticeTimer;
#pragma warning restore CS0649, CS0169

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Every Dropshelf window (the island and terminal, settings, dialogs) is left out of screen sharing and recordings.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            WindowHelper.ExcludeFromCapture(new System.Windows.Interop.WindowInteropHelper((Window)sender).Handle)));

        // First run or update: copy the exe to %LOCALAPPDATA%\Programs\Dropshelf and continue from there. A running
        // instance is asked to quit first, so launching a newer exe replaces the installed one.
        if (Installer.TryInstallAndRelaunch(e.Args))
        {
            Shutdown();
            return;
        }

        bool restarted = e.Args.Contains(Installer.RestartedArgument, StringComparer.OrdinalIgnoreCase);

        // Single instance: a second launch just exits. A relaunch after a crash, update or Windows restart may start
        // before the old process has let go of the mutex, so those wait a few seconds for it. The MSIX build is relaunched
        // through the shell, which may not carry the argument, so it always waits a little.
        _mutex = new Mutex(false, Installer.InstanceMutexName);
        bool owned;
        try
        {
            owned = _mutex.WaitOne(restarted ? 5000 : PackageIdentity.IsPackaged ? 3000 : 0);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // the previous instance died holding it
        }

        if (!owned)
        {
            _mutex.Dispose();
            _mutex = null;

            // Started by hand while another Dropshelf keeps running (the self-update could not replace it): say so, instead of
            // vanishing and leaving the user looking at the old version. Automatic starts stay silent.
            if (!e.Args.Contains(Installer.AutostartArgument, StringComparer.OrdinalIgnoreCase) && !restarted)
            {
                MessageBox.Show(
                    "Another copy of Dropshelf is already running, so this one (version " + Installer.CurrentVersion + ") did not start.\n\n" +
                    "Quit the running one (right-click the tray icon > Quit, or end Dropshelf.exe in Task Manager). If you installed the " +
                    "MSIX version, uninstall it in Settings > Apps. Then start this file again.",
                    "Dropshelf", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        CrashGuard.Install(this);
        CrashGuard.BeforeExit = FlushPendingSaves;
        SessionEnding += (_, _) => FlushPendingSaves(); // log-off / shutdown may kill the process before OnExit runs
        try
        {
            StartQuitListener();
            StartServices(restarted);
            CrashGuard.MarkStartupComplete();
        }
        catch (Exception ex)
        {
            // CrashGuard treats ordinary exceptions as non-fatal, which here would leave a process without a tray icon
            // that still owns the mutex (so no new launch could start). Give the mutex back and quit.
            Log.Error("Start-up failed", ex);
            TryStep("single-instance mutex", ReleaseInstanceMutex);
            Shutdown();
        }
    }

    /// <summary>Creates the services and windows. Runs after the single-instance mutex is owned; any exception aborts start-up.</summary>
    private void StartServices(bool restarted)
    {
        AppPaths.EnsureDirectories();
        _settings = new SettingsService();
        AnimationSettings.Attach(_settings.Current);
        _theme = new ThemeService(_settings); // accent resources; must exist before any window is created
        IslandWindow.SetTextColor(_settings.Current.IslandTransparent ? IslandWindow.LightGrey : System.Windows.Media.Colors.White);
        _messages = new MessageWindow();
        _hotkeys = new HotkeyService(_messages);

        // Terminal-only build for slow laptops: no HUDs, popup hider, media, battery, Bluetooth, notifications, weather,
        // calendar, clipboard history, basket, screenshots or mouse hook. Nothing runs in the background but the shell and a
        // cheap cursor poll, so the island costs almost no CPU or memory while it is closed.
        _activityHost = new ActivityHost { SplitEnabled = false };
        StartActivities();

        _island = new IslandWindow(_settings, ShowSettings, _activityHost);
        _island.Show();

        _fullscreen = new FullscreenWatcher(_settings);
        _fullscreen.Changed += fullscreen =>
        {
            _activityHost.SetFullscreen(fullscreen);
            _island.SetFullscreen(fullscreen);
        };
        if (_fullscreen.IsFullscreen)
        {
            _activityHost.SetFullscreen(true); // a fullscreen app was already in front at start-up
            _island.SetFullscreen(true);
        }

        _tray = new TrayService(
            showIsland: () => _island.ShowTerminal(),
            openBasket: () => { },
            openClipboard: () => { },
            openSettings: ShowSettings,
            uninstall: PackageIdentity.IsPackaged ? OpenAppsSettings : Uninstall,
            quit: QuitByUser,
            screenshot: () => { },
            openTerminal: () => _island.ShowTerminal());

        if (!_hotkeys.Register(HotkeyService.TerminalId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, NativeMethods.VK_N, () => _island.ShowTerminal()))
        {
            _tray.ShowBalloon("Dropshelf", "Ctrl+Shift+N is already used by another app, so the terminal hotkey is unavailable.");
        }

        // Keep the registry entry pointing at the installed exe (this also writes it on the first run after install),
        // make sure the Start Menu shortcut exists, and ask Windows to bring the app back after update reboots.
        // The MSIX build has a startup task instead: ask for it to be enabled, and read its state for Settings.
        StartupRegistration.PackagedStateChanged += OnStartupTaskStateChanged;
        if (_settings.Current.LaunchAtStartup)
        {
            StartupRegistration.Set(true);
        }
        else if (PackageIdentity.IsPackaged)
        {
            _ = StartupRegistration.RefreshPackagedAsync();
        }

        TryStep("Start Menu shortcut", Installer.EnsureStartMenuShortcut);
        if (!System.Diagnostics.Debugger.IsAttached && !PackageIdentity.IsPackaged)
        {
            TryStep("application restart registration", () => NativeMethods.RegisterApplicationRestart(
                Installer.RestartedArgument, NativeMethods.RESTART_NO_CRASH | NativeMethods.RESTART_NO_HANG));
        }

        if (Installer.UpdateFailedNotice is { } notice)
        {
            _tray.ShowBalloon("Dropshelf", notice);
        }
        else if (restarted)
        {
            _tray.ShowBalloon("Dropshelf", "Dropshelf is running again.");
        }

        _settings.Changed += OnSettingChanged;
        ScheduleOneTimeNotices();
    }

    /// <summary>
    /// Things that are said once, a moment after start-up: the MSIX build offers to remove a portable copy's autostart entry,
    /// the x64 build on an Arm PC points at the Arm version.
    /// </summary>
    private void ScheduleOneTimeNotices()
    {
        if (PackageIdentity.IsPackaged)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                TryStep("portable cleanup offer", () => PortableCleanup.OfferOnce(_settings!))));
        }
        else if (ArmNotice.IsX64OnArm)
        {
            _armNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _armNoticeTimer.Tick += (_, _) =>
            {
                _armNoticeTimer.Stop();
                TryStep("Arm notice", () => _armNotice?.ShowOnce());
            };
            _armNoticeTimer.Start();
        }
    }

    /// <summary>Creates the only activity this build has: the terminal. If it cannot be set up the island says so.</summary>
    private void StartActivities()
    {
        TryStep("terminal", () =>
        {
            _terminal = new TerminalActivity(_settings!);
            _activityHost!.Register(_terminal);
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Tear down in reverse order; every step tolerates a half-started app (second instance) and a failing
        // step never stops the later ones (tray icon, settings flush and mutex release must always run).
        TryStep("fullscreen watcher", () => _fullscreen?.Dispose());
        TryStep("privacy dot", () => _privacy?.Dispose());
        TryStep("downloads", () => _downloads?.Dispose());
        TryStep("notification pill", () => _notificationPill?.Dispose());
        TryStep("notifications", () => _notifications?.Dispose());
        TryStep("meeting alerts", () => _meeting?.Dispose());
        TryStep("calendar", () => _calendar?.Dispose());
        TryStep("weather pill", () => _weatherPill?.Dispose());
        TryStep("weather", () => _weather?.Dispose());
        TryStep("terminal", () => _terminal?.Dispose());
        TryStep("Bluetooth HUD", () => _bluetooth?.Dispose());
        TryStep("indicators", () => _indicator?.Dispose());
        TryStep("popup hider", () => _osd?.Dispose());
        TryStep("battery HUD", () => _battery?.Dispose());
        TryStep("brightness HUD", () => _brightness?.Dispose());
        TryStep("now playing", () =>
        {
            _nowPlaying?.Dispose();
            _media?.Dispose();
        });
        TryStep("audio endpoint", () => _audio?.Dispose());
        TryStep("theme", () => _theme?.Dispose());
        TryStep("mouse hook", () => _mouseHook?.Dispose());
        TryStep("shake detector", () => _shake?.Dispose());
        TryStep("hotkeys", () => _hotkeys?.Dispose());
        TryStep("clipboard history", () => _clipboard?.Dispose());
        TryStep("tray icon", () => _tray?.Dispose());
        FlushPendingSaves();
        TryStep("message window", () => _messages?.Dispose());
        TryStep("quit listener", () =>
        {
            _quitWait?.Unregister(null);
            _quitEvent?.Dispose();
        });
        TryStep("single-instance mutex", ReleaseInstanceMutex);

        if (_uninstalling)
        {
            Installer.ScheduleCleanup(_deleteDataOnUninstall);
        }

        base.OnExit(e);
    }

    /// <summary>
    /// Writes every debounced save that is still pending (notes, clipboard history, settings). Safe to call any number of times
    /// and from any thread: each store only writes when it has a pending change, and the work hops to the UI thread. Runs on
    /// session end (log-off, shutdown), before a crash relaunch and at exit.
    /// </summary>
    private void FlushPendingSaves()
    {
        if (!Dispatcher.CheckAccess())
        {
            TryStep("flush on UI thread", () =>
                Dispatcher.Invoke(FlushPendingSaves, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2)));
            return;
        }

        TryStep("clipboard flush", () => _clipboard?.Flush());
        TryStep("settings flush", () => _settings?.Flush());
    }

    private void ReleaseInstanceMutex()
    {
        if (_mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread; nothing to release.
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }

    private static void TryStep(string name, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Log.Error($"Step '{name}' failed", ex);
        }
    }

    // ---- Settings -> behaviour --------------------------------------------------------------------

    private void OnSettingChanged(string name)
    {
        if (_settings == null)
        {
            return;
        }

        switch (name)
        {
            case nameof(AppSettings.LaunchAtStartup):
                StartupRegistration.Set(_settings.Current.LaunchAtStartup);
                break;
            case nameof(AppSettings.ShakeToOpenBasket):
            case nameof(AppSettings.ScrollToChangeVolume):
                ApplyMouseHookSetting();
                break;
            case nameof(AppSettings.ReplaceWindowsOsd):
            case nameof(AppSettings.ShowVolumeHud):
                if (_osd != null)
                {
                    _osd.Enabled = _settings.Current.ReplaceWindowsOsd;
                }

                ApplyVolumeKeys();

                break;
            case nameof(AppSettings.ShowCapsLockHud):
            case nameof(AppSettings.ShowFocusHud):
                _indicator?.ApplySettings();
                break;
            case nameof(AppSettings.ShowPrivacyDots):
                if (_privacy != null)
                {
                    _privacy.Enabled = _settings.Current.ShowPrivacyDots;
                }

                break;
            case nameof(AppSettings.ShowDownloads):
                _downloads?.ApplySettings();
                break;
            case nameof(AppSettings.SplitIsland):
                _activityHost!.SplitEnabled = _settings.Current.SplitIsland;
                break;
            case nameof(AppSettings.TerminalShell):
                _terminal?.ApplySettings();
                break;
            case nameof(AppSettings.IslandTransparent):
                _island?.ApplyLook();
                break;
            case nameof(AppSettings.ShowNowPlaying):
                _nowPlaying?.ApplySettings();
                break;
            case nameof(AppSettings.HideInFullscreen):
                _fullscreen?.ApplySettings();
                break;
            case nameof(AppSettings.Animations):
                _island?.OnAnimationModeChanged();
                break;
            case nameof(AppSettings.ClipboardHistoryEnabled):
                // Only a real on/off toggle (re)tries the hotkey registration, so a taken hotkey is not retried
                // (and re-announced) on every size change.
                ApplyClipboardSetting();
                break;
            case nameof(AppSettings.ClipboardHistorySize):
                _clipboard?.ApplySettings();
                break;
            case nameof(AppSettings.ScreenshotHotkeyEnabled):
                ApplyScreenshotSetting();
                break;
            case nameof(AppSettings.WeatherEnabled):
                _weather?.ApplySettings();
                _weatherPill?.ApplySettings();
                break;
            case nameof(AppSettings.WeatherUseLocation):
            case nameof(AppSettings.WeatherCity):
                _weather?.LocationSettingsChanged();
                break;
            case nameof(AppSettings.WeatherFahrenheit):
                _weather?.ApplySettings(); // only redraws: temperatures are stored in Celsius
                break;
            case nameof(AppSettings.ShowWeatherOnPill):
                _weatherPill?.ApplySettings();
                break;
            case nameof(AppSettings.CalendarEnabled):
                _calendar?.ApplySettings();
                _meeting?.ApplySettings();
                break;
            case nameof(AppSettings.CalendarUseWindows):
                _calendar?.ApplySettings();
                break;
            case nameof(AppSettings.MeetingAlerts):
                _meeting?.ApplySettings();
                break;
            case nameof(AppSettings.NotificationsEnabled):
                _notifications?.ApplySettings();
                _notificationPill?.ApplySettings();
                break;
        }
    }

    /// <summary>Ctrl+Shift+S is only registered while the screenshot setting is on; a taken combination is announced once.</summary>
    private void ApplyScreenshotSetting()
    {
        if (_screenshot == null)
        {
            return;
        }

        bool enabled = _settings!.Current.ScreenshotHotkeyEnabled;
        if (enabled && !_screenshotHotkeyRegistered)
        {
            _screenshotHotkeyRegistered = _hotkeys!.Register(
                HotkeyService.ScreenshotId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, NativeMethods.VK_S,
                () => _screenshot?.Begin());
            if (!_screenshotHotkeyRegistered)
            {
                _tray?.ShowBalloon("Dropshelf", "Ctrl+Shift+S is already used by another app, so the screenshot hotkey is unavailable.");
            }
        }
        else if (!enabled && _screenshotHotkeyRegistered)
        {
            _hotkeys!.Unregister(HotkeyService.ScreenshotId);
            _screenshotHotkeyRegistered = false;
        }
    }

    /// <summary>
    /// The low-level mouse hook is only installed while it is needed: shake-to-open is enabled, or the wheel over the island
    /// changes the volume (which also needs a working volume control).
    /// </summary>
    private void ApplyMouseHookSetting()
    {
        if (_mouseHook == null)
        {
            return;
        }

        bool wheel = _settings!.Current.ScrollToChangeVolume && _volume != null;
        if (_island != null)
        {
            _island.WheelEnabled = wheel;
        }

        if (_settings.Current.ShakeToOpenBasket || wheel)
        {
            _mouseHook!.Install();
        }
        else
        {
            _mouseHook!.Uninstall();
        }
    }

    /// <summary>
    /// The cleanest way to keep Windows' volume popup away: Dropshelf takes the volume keys itself (RegisterHotKey, no hook),
    /// so Windows never sees them and never shows its popup; Dropshelf changes the volume in the same 2 % steps and the island
    /// shows it. Only while the island is on screen and "Hide the Windows popups" and the volume HUD are on: in a fullscreen
    /// app (island hidden) the keys go back to Windows. Keys another app already owns stay with it (the popup hider covers them).
    /// </summary>
    private void ApplyVolumeKeys()
    {
        if (_hotkeys == null || _settings == null)
        {
            return;
        }

        bool own = _audio is { IsAvailable: true } && _volume != null && _settings.Current.ReplaceWindowsOsd
                   && _settings.Current.ShowVolumeHud && _fullscreen?.IsFullscreen != true;
        if (own == _volumeKeysOwned)
        {
            return;
        }

        _volumeKeysOwned = own;
        if (own)
        {
            bool up = _hotkeys.Register(HotkeyService.VolumeUpId, 0, 0xAF, () => _audio!.StepVolume(+1), repeat: true);
            bool down = _hotkeys.Register(HotkeyService.VolumeDownId, 0, 0xAE, () => _audio!.StepVolume(-1), repeat: true);
            bool mute = _hotkeys.Register(HotkeyService.VolumeMuteId, 0, 0xAD, () => _audio!.SetMuted(!_audio.Muted));
            if (!(up && down && mute))
            {
                Log.Info("Some volume keys are used by another app; Windows keeps those (its popup is hidden instead)");
            }
        }
        else
        {
            _hotkeys.Unregister(HotkeyService.VolumeUpId);
            _hotkeys.Unregister(HotkeyService.VolumeDownId);
            _hotkeys.Unregister(HotkeyService.VolumeMuteId);
        }
    }

    /// <summary>Clipboard listener and Ctrl+Shift+V are only active while history is enabled.</summary>
    private void ApplyClipboardSetting()
    {
        if (_clipboard == null)
        {
            return;
        }

        _clipboard!.ApplySettings();

        bool enabled = _settings!.Current.ClipboardHistoryEnabled;
        if (enabled && !_clipboardHotkeyRegistered)
        {
            _clipboardHotkeyRegistered = _hotkeys!.Register(
                HotkeyService.ClipboardId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, NativeMethods.VK_V,
                () => ShowClipboard(fromHotkey: true));
            if (!_clipboardHotkeyRegistered)
            {
                _tray?.ShowBalloon("Dropshelf", "Ctrl+Shift+V is already used by another app, so the clipboard hotkey is unavailable.");
            }
        }
        else if (!enabled && _clipboardHotkeyRegistered)
        {
            _hotkeys!.Unregister(HotkeyService.ClipboardId);
            _clipboardHotkeyRegistered = false;
        }
    }

    /// <summary>Green dot while an app uses the camera, orange while one uses the microphone; the tooltip names the app.</summary>
    private void ApplyPrivacyDot()
    {
        if (_privacy == null || _island == null)
        {
            return;
        }

        string tip = string.Join("\n",
            new[]
            {
                _privacy.CameraInUse ? $"{_privacy.CameraApp} is using the camera" : null,
                _privacy.MicrophoneInUse ? $"{_privacy.MicrophoneApp} is using the microphone" : null,
            }.Where(t => t != null));
        _island.SetPrivacy(_privacy.CameraInUse, _privacy.MicrophoneInUse, tip);
    }

    // ---- Window entry points ----------------------------------------------------------------------

    private void ShowBasketAtCursor()
    {
        _basketWindow ??= new BasketWindow(_basketStore!);
        _basketWindow.ShowNearCursor();
    }

    private void ShowClipboard(bool fromHotkey)
    {
        _clipboardWindow ??= new ClipboardWindow(_clipboard!);
        _clipboardWindow.Toggle(fromHotkey);
    }

    private void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            // Reflect the real registry state in case it was changed outside the app (not in dev runs, where nothing is registered).
            // For the MSIX build the startup task is the truth; Task Manager can change it, so read it again too.
            if (PackageIdentity.IsPackaged)
            {
                if (StartupRegistration.PackagedState != null)
                {
                    _settings!.Current.LaunchAtStartup = StartupRegistration.IsEnabled();
                }

                _ = StartupRegistration.RefreshPackagedAsync();
            }
            else if (Installer.AutostartTarget() != null)
            {
                _settings!.Current.LaunchAtStartup = StartupRegistration.IsEnabled();
            }

            _settingsWindow = new SettingsWindow(_settings!);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        _settingsWindow.Activate();
    }

    /// <summary>
    /// The startup task changed (Task Manager, or a request that Windows refused because the user had turned it off): while Settings is open,
    /// the checkbox follows reality instead of claiming something that is not true.
    /// </summary>
    private void OnStartupTaskStateChanged()
    {
        if (_settingsWindow != null && _settings != null)
        {
            _settings.Current.LaunchAtStartup = StartupRegistration.IsEnabled();
        }
    }

    // ---- Always-on plumbing -----------------------------------------------------------------------

    /// <summary>
    /// Lets a newer exe launched from elsewhere ask this instance to quit (named event), so it can overwrite the installed
    /// copy. The wait runs on a thread-pool thread and hops to the UI thread to shut down cleanly.
    /// </summary>
    private void StartQuitListener()
    {
        try
        {
            _quitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, Installer.QuitEventName);
            _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitEvent, (_, timedOut) =>
            {
                if (!timedOut)
                {
                    Log.Info("Quit requested by a newer Dropshelf launch");
                    Dispatcher.BeginInvoke(new Action(() => Shutdown()));
                }
            }, null, Timeout.Infinite, executeOnlyOnce: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException or ArgumentException)
        {
            Log.Error("Could not create the quit event", ex);
        }
    }

    /// <summary>Tray item of the MSIX build: Windows uninstalls it, from Settings > Apps > Installed apps.</summary>
    private static void OpenAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Error("Could not open Settings > Apps", ex);
        }
    }

    /// <summary>Tray "Uninstall...": confirm, remove autostart + shortcut, quit, then a hidden cmd deletes the exe (and optionally the data).</summary>
    private void Uninstall()
    {
        var dialog = new UninstallWindow();
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _uninstalling = true;
        _deleteDataOnUninstall = dialog.DeleteData;
        try
        {
            Installer.RemoveIntegration();
            UnregisterRestart();
        }
        finally
        {
            Shutdown(); // whatever failed above, the uninstall must still quit so the cleanup can run
        }
    }

    /// <summary>Tray "Quit": a deliberate quit must not be undone by Windows restarting registered apps after an update.</summary>
    private void QuitByUser()
    {
        UnregisterRestart();
        Shutdown();
    }

    private static void UnregisterRestart() =>
        TryStep("application restart unregistration", () => NativeMethods.UnregisterApplicationRestart());
}
