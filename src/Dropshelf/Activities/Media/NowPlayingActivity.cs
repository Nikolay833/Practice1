using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Dropshelf.Services;

namespace Dropshelf.Activities.Media;

/// <summary>
/// Now playing. On the collapsed pill: rounded album art left of the mascot and dancing equalizer bars right of it, shown
/// while something plays and for ten seconds after it is paused. In the expanded shelf: the "Now Playing" tab, available for
/// as long as there is a media session. Turned off by the "Show now playing" setting.
/// </summary>
internal sealed class NowPlayingActivity : PillActivityBase, IDisposable
{
    private static readonly TimeSpan PausedLinger = TimeSpan.FromSeconds(10);

    private readonly MediaSessionService _media;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _lingerTimer;
    private readonly Image _art;
    private readonly Grid _artHost;
    private readonly Grid _barsHost;
    private readonly EqualizerBars _bars;
    private bool _active;
    private bool _hasExpanded;
    private bool _wasPlaying;
    private DateTime _pausedSince = DateTime.MinValue;

    public NowPlayingActivity(MediaSessionService media, AudioEndpointService? audio, SettingsService settings)
    {
        _media = media;
        _settings = settings;

        _art = new Image { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(_art, BitmapScalingMode.HighQuality);
        var glyph = new TextBlock { Text = "", FontSize = 12 };
        glyph.SetResourceReference(FrameworkElement.StyleProperty, "Glyph");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextDisabledBrush");
        var frame = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(6), ClipToBounds = true };
        frame.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _artHost = new Grid { Width = 24, Height = 24 };
        frame.Child = new Grid { Children = { glyph, _art } };
        _artHost.Children.Add(frame);

        _bars = new EqualizerBars(audio) { VerticalAlignment = VerticalAlignment.Center };
        _barsHost = new Grid { Width = 24, Height = 24 };
        _barsHost.Children.Add(_bars);
        _bars.HorizontalAlignment = HorizontalAlignment.Left;

        _lingerTimer = new DispatcherTimer { Interval = PausedLinger };
        _lingerTimer.Tick += (_, _) =>
        {
            _lingerTimer.Stop();
            Evaluate();
        };

        _media.Changed += OnMediaChanged;
        Evaluate();
    }

    public override string Id => "media";
    public override int Priority => 100;
    public override bool IsActive => _active;
    public override string? TabTitle => "Now Playing";
    public override bool HasExpanded => _hasExpanded;

    public override CompactContent? CreateCompact() => CompactContent.Of(_artHost, _barsHost);

    public override FrameworkElement? CreateExpanded() => new NowPlayingView(_media);

    /// <summary>The "Show now playing" setting changed.</summary>
    public void ApplySettings() => Evaluate();

    private void OnMediaChanged()
    {
        _art.Source = _media.Art;
        _bars.Playing = _media.IsPlaying;

        if (_media.IsPlaying)
        {
            _pausedSince = DateTime.MinValue;
            _lingerTimer.Stop();
        }
        else if (_wasPlaying)
        {
            _pausedSince = DateTime.UtcNow;
            _lingerTimer.Stop();
            _lingerTimer.Start();
        }

        _wasPlaying = _media.IsPlaying;
        Evaluate();
    }

    private void Evaluate()
    {
        bool enabled = _settings.Current.ShowNowPlaying && _media.IsAvailable;
        bool hasSession = enabled && _media.HasSession;
        bool recentlyPaused = _media.Status == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
                              && _pausedSince != DateTime.MinValue && DateTime.UtcNow - _pausedSince < PausedLinger;
        bool active = hasSession && (_media.IsPlaying || recentlyPaused);

        if (active != _active || hasSession != _hasExpanded)
        {
            _active = active;
            _hasExpanded = hasSession;
            RaiseStateChanged();
        }
    }

    public void Dispose()
    {
        _media.Changed -= OnMediaChanged;
        _lingerTimer.Stop();
    }
}
