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
/// Applies the accent colour and the pill style by replacing entries in the application resources. Everything that
/// uses the accent refers to these keys with <c>DynamicResource</c>, so a change shows up live in every window:
/// <c>AccentColor</c>, <c>AccentBrush</c>, <c>AccentHoverBrush</c>, <c>AccentSoftBrush</c>, <c>AccentStrokeBrush</c>,
/// <c>AccentWashBrush</c> and the glow colours; <c>PillBrush</c> / <c>PillBorderBrush</c> carry the pill style.
/// "Windows accent" follows <see cref="UISettings"/> (registry fallback) and updates when the user changes it.
/// </summary>
internal sealed class ThemeService : IDisposable
{
    public static readonly Color DefaultAccent = Color.FromRgb(0x3B, 0x82, 0xF6);

    private static readonly Dictionary<AccentPreset, Color> Presets = new()
    {
        [AccentPreset.Blue] = DefaultAccent,
        [AccentPreset.Indigo] = Color.FromRgb(0x63, 0x66, 0xF1),
        [AccentPreset.Purple] = Color.FromRgb(0xA8, 0x55, 0xF7),
        [AccentPreset.Pink] = Color.FromRgb(0xEC, 0x48, 0x99),
        [AccentPreset.Red] = Color.FromRgb(0xEF, 0x44, 0x44),
        [AccentPreset.Orange] = Color.FromRgb(0xF9, 0x73, 0x16),
        [AccentPreset.Green] = Color.FromRgb(0x22, 0xC5, 0x5E),
        [AccentPreset.Teal] = Color.FromRgb(0x14, 0xB8, 0xA6),
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

    /// <summary>The live instance (views such as the mascot use it to follow the accent without extra plumbing).</summary>
    public static ThemeService? Current { get; private set; }

    /// <summary>The effective accent colour.</summary>
    public Color Accent { get; private set; } = DefaultAccent;

    /// <summary>The colour the mascot is tinted with (the accent, or the default blue when it does not follow).</summary>
    public Color MascotAccent => _settings.Current.MascotFollowsAccent ? Accent : DefaultAccent;

    /// <summary>Raised on the UI thread after the resources changed.</summary>
    public event Action? Changed;

    /// <summary>The swatch colour for a preset (the live Windows accent for <see cref="AccentPreset.Windows"/>).</summary>
    public static Color ColorOf(AccentPreset preset) =>
        preset == AccentPreset.Windows ? ReadWindowsAccent() : Presets.GetValueOrDefault(preset, DefaultAccent);

    private void OnSettingChanged(string name)
    {
        if (name is nameof(AppSettings.Accent) or nameof(AppSettings.PillStyle) or nameof(AppSettings.MascotFollowsAccent))
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
        Color glow = ColorUtil.Glow(accent);
        Color deep = ColorUtil.Deep(accent);

        ResourceDictionary r = Application.Current.Resources;
        r["AccentColor"] = accent;
        r["AccentHoverColor"] = hover;
        r["AccentGlowColor"] = glow;
        r["AccentBloomStartColor"] = ColorUtil.WithAlpha(deep, 0x80);
        r["AccentBloomEndColor"] = ColorUtil.WithAlpha(deep, 0x00);
        r["AccentRimTopColor"] = ColorUtil.WithAlpha(accent, 0x00);
        r["AccentBrush"] = ColorUtil.Brush(accent);
        r["AccentHoverBrush"] = ColorUtil.Brush(hover);
        r["AccentSoftBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(accent, 0x33));
        r["AccentStrokeBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(accent, 0x99));
        r["AccentWashBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(accent, 0x14));

        if (s.PillStyle == PillStyle.Glass)
        {
            // The window is layered (AllowsTransparency), so "glass" is a semi-transparent dark fill with a light rim.
            var fill = new LinearGradientBrush(Color.FromArgb(0xB8, 0x24, 0x26, 0x2C), Color.FromArgb(0x9E, 0x0C, 0x0D, 0x10), 90);
            fill.Freeze();
            r["PillBrush"] = fill;
            r["PillBorderBrush"] = ColorUtil.Brush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            r["PillBrush"] = ColorUtil.Brush(Colors.Black);
            r["PillBorderBrush"] = ColorUtil.Brush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        }

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
