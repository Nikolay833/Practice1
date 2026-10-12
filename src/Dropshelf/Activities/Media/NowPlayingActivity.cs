using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Dropshelf.Services;

namespace Dropshelf.Activities.Media;

/// <summary>
/// Now playing. On the collapsed island, like the Dynamic Island: rounded album art at the leading edge and the waveform at
/// the trailing edge, shown while something plays and for ten seconds after it is paused. In the expanded island: the "Now Playing" tab, available for
/// as long as there is a media session. Turned off by the "Show now playing" setting.
/// </summary>
internal sealed class NowPlayingActivity : PillActivityBase, IDisposable
{
    private static readonly TimeSpan PausedLinger = TimeSpan.FromSeconds(10);

    private readonly MediaSessionService _media;
    private readonly SettingsService _settings;
    private readonly AudioEndpointService? _audio;
    private readonly DispatcherTimer _lingerTimer;
    private readonly Image _art;
    private readonly Image _miniArt;
    private readonly Border _mini;
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
        _audio = audio;

        _art = new Image { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(_art, BitmapScalingMode.HighQuality);
        var glyph = new TextBlock { Text = "", FontSize = 12 };
        glyph.SetResourceReference(FrameworkElement.StyleProperty, "Glyph");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextDisabledBrush");
        var frame = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), ClipToBounds = true };
        frame.SetResourceReference(Border.BackgroundProperty, "FillBrush");
        _art.Clip = new RectangleGeometry(new Rect(0, 0, 22, 22), 6, 6);
        _artHost = new Grid { Width = 22, Height = 24 };
        frame.Child = new Grid { Children = { glyph, _art } };
        _artHost.Children.Add(frame);

        // The split-island bubble: just the album art, round like on the iPhone.
        _miniArt = new Image { Stretch = Stretch.UniformToFill, Clip = new EllipseGeometry(new Rect(0, 0, 22, 22)) };
        RenderOptions.SetBitmapScalingMode(_miniArt, BitmapScalingMode.HighQuality);
        var miniGlyph = new TextBlock { Text = "\uEC4F", FontSize = 11 };
        miniGlyph.SetResourceReference(FrameworkElement.StyleProperty, "Glyph");
        _mini = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Child = new Grid { Children = { miniGlyph, _miniArt } } };
        _mini.SetResourceReference(Border.BackgroundProperty, "FillBrush");

        _bars = new EqualizerBars(audio) { VerticalAlignment = VerticalAlignment.Center };
        _barsHost = new Grid { Width = 23, Height = 24 };
        _barsHost.Children.Add(_bars);
        _bars.HorizontalAlignment = HorizontalAlignment.Right;

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

    public override FrameworkElement? CreateExpanded() => new NowPlayingView(_media, _audio);

    public override FrameworkElement? CreateMinimal() => _mini;

    /// <summary>The "Show now playing" setting changed.</summary>
    public void ApplySettings() => Evaluate();

    private void OnMediaChanged()
    {
        _art.Source = _media.Art;
        _miniArt.Source = _media.Art;
        _bars.SetTint(_media.ArtTint);
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
