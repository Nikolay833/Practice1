using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Dropshelf.Activities;
using Dropshelf.Activities.Devices;
using Dropshelf.Activities.Hud;
using Dropshelf.Activities.Media;
using Dropshelf.Activities.Power;
using Dropshelf.Animation;
using Dropshelf.Basket;
using Dropshelf.Clipboard;
using Dropshelf.Interop;
using Dropshelf.Services;
using Dropshelf.Settings;
using Dropshelf.Shelf;
using Dropshelf.Themes;

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
    private ShelfStore? _shelf;
    private ItemStore? _basketStore;
    private MessageWindow? _messages;
    private HotkeyService? _hotkeys;
    private ClipboardHistoryService? _clipboard;
    private MouseHook? _mouseHook;
    private ShakeDetector? _shake;
    private TrayService? _tray;
    private ShelfWindow? _shelfWindow;
    private BasketWindow? _basketWindow;
    private ClipboardWindow? _clipboardWindow;
    private SettingsWindow? _settingsWindow;
    private bool _clipboardHotkeyRegistered;
    private ThemeService? _theme;
    private ActivityHost? _activityHost;
    private AudioEndpointService? _audio;
    private MediaSessionService? _media;
    private NowPlayingActivity? _nowPlaying;
    private VolumeActivity? _volume;
    private BrightnessActivity? _brightness;
    private BatteryActivity? _battery;
    private BluetoothActivity? _bluetooth;
    private OsdSuppressor? _osd;
    private FullscreenWatcher? _fullscreen;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // First run or update: copy the exe to %LOCALAPPDATA%\Programs\Dropshelf and continue from there. A running
        // instance is asked to quit first, so launching a newer exe replaces the installed one.
        if (Installer.TryInstallAndRelaunch(e.Args))
        {
            Shutdown();
            return;
        }

        bool restarted = e.Args.Contains(Installer.RestartedArgument, StringComparer.OrdinalIgnoreCase);

        // Single instance: a second launch just exits. A relaunch after a crash, update or Windows restart may start
        // before the old process has let go of the mutex, so those wait a few seconds for it.
        _mutex = new Mutex(false, Installer.InstanceMutexName);
        bool owned;
        try
        {
            owned = _mutex.WaitOne(restarted ? 5000 : 0);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // the previous instance died holding it
        }

        if (!owned)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        CrashGuard.Install(this);
        StartQuitListener();

        AppPaths.EnsureDirectories();
        _settings = new SettingsService();
        AnimationSettings.Attach(_settings.Current);
        _theme = new ThemeService(_settings); // accent + pill style resources; must exist before any window is created
        _shelf = new ShelfStore(_settings);
        _basketStore = new ItemStore();

        _messages = new MessageWindow();
        _hotkeys = new HotkeyService(_messages);
        _clipboard = new ClipboardHistoryService(_messages, _settings);

        _mouseHook = new MouseHook();
        _mouseHook.Wheel += (_, _, delta) => _volume?.ScrollBy(delta);
        _shake = new ShakeDetector(_mouseHook);
        // The mouse hook runs on its own thread and forwards events to the UI thread, so this runs on the UI thread.
        _shake.Shaken += () =>
        {
            if (_settings.Current.ShakeToOpenBasket)
            {
                ShowBasketAtCursor();
            }
        };

        _activityHost = new ActivityHost();
        StartActivities();

        _shelfWindow = new ShelfWindow(_shelf, _settings, ShowSettings, _activityHost);
        _shelfWindow.WheelRegionSink = region => _mouseHook.SetWheelRegion(region);
        _shelfWindow.Show();

        _fullscreen = new FullscreenWatcher(_settings);
        _fullscreen.Changed += fullscreen =>
        {
            _activityHost.SetFullscreen(fullscreen);
            _shelfWindow.SetFullscreen(fullscreen);
        };
        if (_fullscreen.IsFullscreen)
        {
            _activityHost.SetFullscreen(true); // a fullscreen app was already in front at start-up
            _shelfWindow.SetFullscreen(true);
        }

        _tray = new TrayService(
            showShelf: () => _shelfWindow.ShowExpanded(),
            openBasket: ShowBasketAtCursor,
            openClipboard: () => ShowClipboard(fromHotkey: false),
            openSettings: ShowSettings,
            uninstall: Uninstall,
            quit: () => Shutdown());

        if (!_hotkeys.Register(HotkeyService.BasketId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, NativeMethods.VK_B, ShowBasketAtCursor))
        {
            _tray.ShowBalloon("Dropshelf", "Ctrl+Shift+B is already used by another app, so the basket hotkey is unavailable.");
        }

        // Keep the registry entry pointing at the installed exe (this also writes it on the first run after install),
        // make sure the Start Menu shortcut exists, and ask Windows to bring the app back after update reboots.
        if (_settings.Current.LaunchAtStartup)
        {
            StartupRegistration.Set(true);
        }

        TryStep("Start Menu shortcut", Installer.EnsureStartMenuShortcut);
        if (!System.Diagnostics.Debugger.IsAttached)
        {
            TryStep("application restart registration", () => NativeMethods.RegisterApplicationRestart(
                Installer.RestartedArgument, NativeMethods.RESTART_NO_CRASH | NativeMethods.RESTART_NO_HANG));
        }

        if (restarted)
        {
            _tray.ShowBalloon("Dropshelf", "Dropshelf is running again.");
        }

        _settings.Changed += OnSettingChanged;
        ApplyMouseHookSetting();
        ApplyClipboardSetting();
    }

    /// <summary>
    /// Creates the pill activities. Each one is set up on its own: if Core Audio, WinRT or WMI is missing on this machine the
    /// matching feature stays off and the rest of the app is unaffected.
    /// </summary>
    private void StartActivities()
    {
        _osd = new OsdSuppressor { Gate = hud => _activityHost!.CanShowHud(hud) };

        TryStep("audio endpoint", () => _audio = new AudioEndpointService());
        if (_audio is { IsAvailable: true })
        {
            TryStep("volume HUD", () =>
            {
                _volume = new VolumeActivity(_audio, _settings!, _osd);
                _activityHost!.Register(_volume);
            });
        }

        TryStep("now playing", () =>
        {
            _media = new MediaSessionService();
            _nowPlaying = new NowPlayingActivity(_media, _audio, _settings!);
            _activityHost!.Register(_nowPlaying);
            _ = _media.StartAsync();
        });

        TryStep("brightness HUD", () =>
        {
            _brightness = new BrightnessActivity(_settings!, _osd);
            _activityHost!.Register(_brightness);
            _ = _brightness.StartAsync();
        });

        TryStep("battery HUD", () =>
        {
            _battery = new BatteryActivity(_settings!);
            _activityHost!.Register(_battery);
        });

        TryStep("Bluetooth HUD", () =>
        {
            _bluetooth = new BluetoothActivity(_settings!);
            _activityHost!.Register(_bluetooth);
            _bluetooth.Start();
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Tear down in reverse order; every step tolerates a half-started app (second instance) and a failing
        // step never stops the later ones (tray icon, settings flush and mutex release must always run).
        TryStep("fullscreen watcher", () => _fullscreen?.Dispose());
        TryStep("Bluetooth HUD", () => _bluetooth?.Dispose());
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
        TryStep("shelf flush", () => _shelf?.Flush());
        TryStep("settings flush", () => _settings?.Flush());
        TryStep("message window", () => _messages?.Dispose());
        TryStep("quit listener", () =>
        {
            _quitWait?.Unregister(null);
            _quitEvent?.Dispose();
        });
        TryStep("single-instance mutex", () =>
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
            }
        });

        if (_uninstalling)
        {
            Installer.ScheduleCleanup(_deleteDataOnUninstall);
        }

        base.OnExit(e);
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
            case nameof(AppSettings.RememberShelf):
                _shelf?.OnRememberChanged();
                break;
            case nameof(AppSettings.ShakeToOpenBasket):
            case nameof(AppSettings.ScrollToChangeVolume):
                ApplyMouseHookSetting();
                break;
            case nameof(AppSettings.ShowNowPlaying):
                _nowPlaying?.ApplySettings();
                break;
            case nameof(AppSettings.HideInFullscreen):
                _fullscreen?.ApplySettings();
                break;
            case nameof(AppSettings.Animations):
                _shelfWindow?.OnAnimationModeChanged();
                break;
            case nameof(AppSettings.ClipboardHistoryEnabled):
                // Only a real on/off toggle (re)tries the hotkey registration, so a taken hotkey is not retried
                // (and re-announced) on every size change.
                ApplyClipboardSetting();
                break;
            case nameof(AppSettings.ClipboardHistorySize):
                _clipboard!.ApplySettings();
                break;
        }
    }

    /// <summary>
    /// The low-level mouse hook is only installed while it is needed: shake-to-open is enabled, or the wheel over the pill
    /// changes the volume (which also needs a working volume control).
    /// </summary>
    private void ApplyMouseHookSetting()
    {
        bool wheel = _settings!.Current.ScrollToChangeVolume && _volume != null;
        if (_shelfWindow != null)
        {
            _shelfWindow.WheelEnabled = wheel;
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

    /// <summary>Clipboard listener and Ctrl+Shift+V are only active while history is enabled.</summary>
    private void ApplyClipboardSetting()
    {
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

    // ---- Window entry points ----------------------------------------------------------------------

    private void ShowBasketAtCursor()
    {
        _basketWindow ??= new BasketWindow(_basketStore!, _shelf!);
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
            if (Installer.AutostartTarget() != null)
            {
                _settings!.Current.LaunchAtStartup = StartupRegistration.IsEnabled();
            }

            _settingsWindow = new SettingsWindow(_settings!);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        _settingsWindow.Activate();
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
        Installer.RemoveIntegration();
        Shutdown();
    }
}
