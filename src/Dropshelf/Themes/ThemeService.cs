using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Dropshelf.Services;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace Dropshelf.Themes;

/// <summary>
/// Applies the accent colour by replacing entries in the application resources. Everything that uses the accent refers to
/// these keys with <c>DynamicResource</c>, so a change shows up live in every window: <c>AccentColor</c>,
/// <c>AccentBrush</c>, <c>AccentHoverBrush</c>, <c>AccentSoftBrush</c>, <c>AccentStrokeBrush</c>, <c>AccentWashBrush</c>
/// and <c>AccentTextBrush</c> (black on light accents such as yellow, white otherwise).
/// "Windows accent" follows <see cref="UISettings"/> (registry fallback) and updates when the user changes it.
/// </summary>
internal sealed class ThemeService : IDisposable
{
    /// <summary>Apple's systemBlue (dark appearance).</summary>
    public static readonly Color DefaultAccent = Color.FromRgb(0x0A, 0x84, 0xFF);

    // Apple system colours, dark appearance (macOS / iOS Human Interface Guidelines).
    private static readonly Dictionary<AccentPreset, Color> Presets = new()
    {
        [AccentPreset.Blue] = DefaultAccent,
        [AccentPreset.Purple] = Color.FromRgb(0xBF, 0x5A, 0xF2),
        [AccentPreset.Pink] = Color.FromRgb(0xFF, 0x37, 0x5F),
        [AccentPreset.Red] = Color.FromRgb(0xFF, 0x45, 0x3A),
        [AccentPreset.Orange] = Color.FromRgb(0xFF, 0x9F, 0x0A),
        [AccentPreset.Yellow] = Color.FromRgb(0xFF, 0xD6, 0x0A),
        [AccentPreset.Green] = Color.FromRgb(0x30, 0xD1, 0x58),
        [AccentPreset.Teal] = Color.FromRgb(0x40, 0xC8, 0xE0),
        [AccentPreset.Indigo] = Color.FromRgb(0x5E, 0x5C, 0xE6),
        [AccentPreset.Graphite] = Color.FromRgb(0x8E, 0x8E, 0x93),
    };

    /// <summary>The order the swatches appear in Settings (the macOS order, then the extra iOS colours).</summary>
    public static readonly AccentPreset[] SwatchOrder =
    {
        AccentPreset.Blue, AccentPreset.Purple, AccentPreset.Pink, AccentPreset.Red, AccentPreset.Orange,
        AccentPreset.Yellow, AccentPreset.Green, AccentPreset.Teal, AccentPreset.Indigo, AccentPreset.Graphite,
        AccentPreset.Windows,
    };

    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private UISettings? _uiSettings;

    public ThemeService(SettingsService settings)
    {
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Current = this;
        _settings.Changed += OnSettingChanged;
        TryHookWindowsAccent();
        Apply();
    }

    /// <summary>The live instance (views use it to follow the accent without extra plumbing).</summary>
    public static ThemeService? Current { get; private set; }

    /// <summary>The effective accent colour.</summary>
    public Color Accent { get; private set; } = DefaultAccent;

    /// <summary>Raised on the UI thread after the resources changed.</summary>
    public event Action? Changed;

    /// <summary>The swatch colour for a preset (the live Windows accent for <see cref="AccentPreset.Windows"/>).</summary>
    public static Color ColorOf(AccentPreset preset) =>
        preset == AccentPreset.Windows ? ReadWindowsAccent() : Presets.GetValueOrDefault(preset, DefaultAccent);

    private void OnSettingChanged(string name)
    {
        if (name == nameof(AppSettings.Accent))
        {
            Apply();
        }
    }

    private void Apply()
    {
        AppSettings s = _settings.Current;
        Color accent = ColorOf(s.Accent);
        Accent = accent;

        Color hover = ColorUtil.Hover(accent);

        ResourceDictionary r = Application.Current.Resources;
        r["AccentColor"] = accent;
        r["AccentHoverColor"] = hover;
        r["AccentBrush"] = ColorUtil.Brush(accent);
        r["AccentHoverBrush"] = ColorUtil.Brush(hover);
        r["AccentSoftBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(accent, 0x33));
        r["AccentStrokeBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(accent, 0x99));
        r["AccentWashBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(accent, 0x14));
        r["AccentTextBrush"] = ColorUtil.Brush(ColorUtil.Luminance(accent) > 0.6 ? Colors.Black : Colors.White);

        Changed?.Invoke();
    }

    // ---- Windows accent ---------------------------------------------------------------------------

    private void TryHookWindowsAccent()
    {
        try
        {
            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += (_, _) => _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_settings.Current.Accent == AccentPreset.Windows)
                {
                    Apply();
                }
            }));
        }
        catch (Exception ex)
        {
            Log.Error("UISettings unavailable; Windows accent is read from the registry", ex);
            _uiSettings = null;
        }
    }

    private static Color ReadWindowsAccent()
    {
        try
        {
            var ui = new UISettings();
            Windows.UI.Color c = ui.GetColorValue(UIColorType.Accent);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch (Exception)
        {
            // Fall through to the registry.
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // Use the default below.
        }

        return DefaultAccent;
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingChanged;
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }
}
