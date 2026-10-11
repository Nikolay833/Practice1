using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Dropshelf.Basket;
using Dropshelf.Clipboard;
using Dropshelf.Interop;
using Dropshelf.Services;
using Dropshelf.Settings;
using Dropshelf.Shelf;

namespace Dropshelf;

/// <summary>
/// Composition root: owns the services and the (mostly hidden) windows, enforces a single instance and wires
/// settings changes to live behaviour.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\Dropshelf.SingleInstance";

    private Mutex? _mutex;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: a second launch just exits.
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        AppPaths.EnsureDirectories();
        _settings = new SettingsService();
        _shelf = new ShelfStore(_settings);
        _basketStore = new ItemStore();

        _messages = new MessageWindow();
        _hotkeys = new HotkeyService(_messages);
        _clipboard = new ClipboardHistoryService(_messages, _settings);

        _mouseHook = new MouseHook();
        _shake = new ShakeDetector(_mouseHook);
        // The mouse hook runs on its own thread and forwards events to the UI thread, so this runs on the UI thread.
        _shake.Shaken += () =>
        {
            if (_settings.Current.ShakeToOpenBasket)
            {
                ShowBasketAtCursor();
            }
        };

        _shelfWindow = new ShelfWindow(_shelf, _settings, ShowSettings);
        _shelfWindow.Show();

        _tray = new TrayService(
            showShelf: () => _shelfWindow.ShowExpanded(),
            openBasket: ShowBasketAtCursor,
            openClipboard: () => ShowClipboard(fromHotkey: false),
            openSettings: ShowSettings,
            quit: () => Shutdown());

        if (!_hotkeys.Register(HotkeyService.BasketId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, NativeMethods.VK_B, ShowBasketAtCursor))
        {
            _tray.ShowBalloon("Dropshelf", "Ctrl+Shift+B is already used by another app, so the basket hotkey is unavailable.");
        }

        // Keep the registry entry pointing at the current exe, then apply everything else.
        if (_settings.Current.LaunchAtStartup)
        {
            StartupRegistration.Set(true);
        }

        _settings.Changed += OnSettingChanged;
        ApplyShakeSetting();
        ApplyClipboardSetting();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Tear down in reverse order; every step tolerates a half-started app (second instance) and a failing
        // step never stops the later ones (tray icon, settings flush and mutex release must always run).
        TryStep("mouse hook", () => _mouseHook?.Dispose());
        TryStep("shake detector", () => _shake?.Dispose());
        TryStep("hotkeys", () => _hotkeys?.Dispose());
        TryStep("clipboard history", () => _clipboard?.Dispose());
        TryStep("tray icon", () => _tray?.Dispose());
        TryStep("shelf flush", () => _shelf?.Flush());
        TryStep("settings flush", () => _settings?.Flush());
        TryStep("message window", () => _messages?.Dispose());
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
            Log.Error($"Shutdown step '{name}' failed", ex);
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
                ApplyShakeSetting();
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

    /// <summary>The low-level mouse hook is only installed while shake-to-open is enabled.</summary>
    private void ApplyShakeSetting()
    {
        if (_settings!.Current.ShakeToOpenBasket)
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
            // Reflect the real registry state in case it was changed outside the app.
            _settings!.Current.LaunchAtStartup = StartupRegistration.IsEnabled();
            _settingsWindow = new SettingsWindow(_settings);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        _settingsWindow.Activate();
    }
}
