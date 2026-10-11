using System;
using System.Windows;
using Dropshelf.Services;

namespace Dropshelf.Activities.Hud;

/// <summary>
/// Volume HUD: when the system volume or mute changes (keys, tray slider, another app, or the wheel over the pill) the pill
/// grows to show a speaker glyph (muted / low / medium / high) and an animated fill bar with the percentage. Also owns the
/// "scroll over the pill" action and hides the native Windows popup while it shows its own.
/// </summary>
internal sealed class VolumeActivity : PillActivityBase
{
    private const float WheelStepPerNotch = 0.02f;

    private readonly AudioEndpointService _audio;
    private readonly SettingsService _settings;
    private readonly OsdSuppressor _osd;
    private readonly System.Windows.Controls.TextBlock _glyph;
    private readonly FrameworkElement _left;
    private readonly HudBar _bar;
    private float _lastVolume = -1;
    private bool _lastMuted;

    public VolumeActivity(AudioEndpointService audio, SettingsService settings, OsdSuppressor osd)
    {
        _audio = audio;
        _settings = settings;
        _osd = osd;

        _glyph = HudParts.Glyph(GlyphFor(audio.Volume, audio.Muted));
        _left = HudParts.Cell(_glyph, 22);
        _bar = new HudBar();
        _lastVolume = audio.Volume;
        _lastMuted = audio.Muted;
        _bar.Set(_lastVolume, Percent(_lastVolume), animate: false);

        _audio.VolumeChanged += OnVolumeChanged;
    }

    public override string Id => "volume";
    public override int Priority => 300;
    public override PillActivityKind Kind => PillActivityKind.Hud;
    public override bool ShowInFullscreen => _settings.Current.ShowVolumeHudInFullscreen;

    public override CompactContent? CreateCompact() => CompactContent.Of(_left, _bar, extraHeight: 4);

    /// <summary>Wheel over the collapsed pill: <paramref name="delta"/> is the raw wheel delta (120 per notch).</summary>
    public void ScrollBy(int delta)
    {
        if (_audio.IsAvailable)
        {
            _audio.AdjustVolume(delta / 120f * WheelStepPerNotch);
        }
    }

    private void OnVolumeChanged(float volume, bool muted)
    {
        bool changed = Math.Abs(volume - _lastVolume) > 0.0005f || muted != _lastMuted;
        _lastVolume = volume;
        _lastMuted = muted;
        if (!changed || !_settings.Current.ShowVolumeHud)
        {
            return;
        }

        _glyph.Text = GlyphFor(volume, muted);
        _bar.Set(muted ? 0 : volume, muted ? "Muted" : Percent(volume), muted ? "TextDisabledBrush" : "TextBrush");
        if (_settings.Current.ReplaceWindowsOsd)
        {
            _osd.Suppress(this);
        }

        RequestHud();
    }

    private static string Percent(float v) => $"{Math.Round(v * 100)}%";

    // Segoe MDL2 / Fluent: E74F mute, E992 no bars, E993 one, E994 two, E995 three.
    private static string GlyphFor(float volume, bool muted)
    {
        if (muted || volume <= 0.001f)
        {
            return muted ? "" : "";
        }

        return volume < 0.34f ? "" : volume < 0.67f ? "" : "";
    }
}
