using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Dropshelf.Services;

namespace Dropshelf.Activities.Media;

/// <summary>
/// The "Now Playing" tab: art, title, artist and a live waveform; a seekable progress bar with elapsed / remaining time;
/// shuffle, previous, play-pause, next and repeat; and on the right where it plays from and a volume slider. The progress
/// is refreshed four times a second, and only while the tab is visible.
/// </summary>
public partial class NowPlayingView : UserControl
{
    private readonly MediaSessionService _media;
    private readonly AudioEndpointService? _audio;
    private readonly EqualizerBars _wave;
    private bool _settingVolume;
    private readonly DispatcherTimer _timer;
    private bool _scrubbing;

    internal NowPlayingView(MediaSessionService media, AudioEndpointService? audio)
    {
        InitializeComponent();
        _media = media;
        _audio = audio;
        _wave = new EqualizerBars(audio) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        WaveHost.Children.Add(_wave);

        if (audio is { IsAvailable: true })
        {
            VolumeSlider.Value = audio.Muted ? 0 : audio.Volume;
            VolumeSlider.ValueChanged += (_, e) =>
            {
                if (!_settingVolume)
                {
                    audio.SetVolume((float)e.NewValue);
                }
            };
        }
        else
        {
            VolumeRow.IsEnabled = false;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => UpdatePosition();

        Scrubber.PreviewMouseLeftButtonDown += (_, _) => _scrubbing = true;
        Scrubber.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnScrubberReleased), handledEventsToo: true);
        Scrubber.ValueChanged += (_, _) =>
        {
            if (_scrubbing)
            {
                ElapsedText.Text = Format(TimeSpan.FromSeconds(Scrubber.Value));
            }
        };

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Refresh();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        };
    }

    private void Attach()
    {
        _media.Changed -= OnMediaChanged;
        _media.Changed += OnMediaChanged;
        if (_audio != null)
        {
            _audio.VolumeChanged -= OnVolumeChanged;
            _audio.VolumeChanged += OnVolumeChanged;
        }

        Refresh();
        if (IsVisible)
        {
            _timer.Start();
        }
    }

    private void Detach()
    {
        _media.Changed -= OnMediaChanged;
        if (_audio != null)
        {
            _audio.VolumeChanged -= OnVolumeChanged;
        }

        _timer.Stop();
    }

    private void OnVolumeChanged(float volume, bool muted)
    {
        _settingVolume = true;
        VolumeSlider.Value = muted ? 0 : volume;
        _settingVolume = false;
    }

    private void OnMediaChanged()
    {
        if (IsVisible)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        TitleText.Text = string.IsNullOrEmpty(_media.Title) ? "Nothing playing" : _media.Title;
        ArtistText.Text = string.IsNullOrEmpty(_media.Artist) ? _media.Album : _media.Artist;
        SourceText.Text = string.IsNullOrEmpty(_media.SourceApp) ? "Media" : _media.SourceApp;
        SourceInitial.Text = SourceText.Text.Substring(0, 1).ToUpperInvariant();
        AlbumText.Text = _media.Album;
        ArtImage.Source = _media.Art;
        _wave.SetTint(_media.ArtTint);
        _wave.Playing = _media.IsPlaying;

        PauseIcon.Visibility = _media.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        PlayIcon.Visibility = _media.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        ShuffleButton.IsEnabled = _media.CanShuffle;
        ShuffleButton.IsChecked = _media.ShuffleActive;
        RepeatButton.IsEnabled = _media.CanRepeat;
        RepeatButton.IsChecked = _media.RepeatMode != Windows.Media.MediaPlaybackAutoRepeatMode.None;
        RepeatButton.Content = _media.RepeatMode == Windows.Media.MediaPlaybackAutoRepeatMode.Track ? "\uE8ED" : "\uE8EE";
        PlayPauseButton.IsEnabled = _media.CanPlayPause;
        PreviousButton.IsEnabled = _media.CanPrevious;
        NextButton.IsEnabled = _media.CanNext;
        Scrubber.IsEnabled = _media.CanSeek && _media.Duration > TimeSpan.Zero;
        Scrubber.Maximum = Math.Max(1, _media.Duration.TotalSeconds);
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        if (_scrubbing)
        {
            return;
        }

        TimeSpan position = _media.GetPosition();
        Scrubber.Value = Math.Min(position.TotalSeconds, Scrubber.Maximum);
        ElapsedText.Text = Format(position);
        RemainingText.Text = _media.Duration > TimeSpan.Zero ? "-" + Format(_media.Duration - position) : string.Empty;
    }

    private async void OnScrubberReleased(object sender, MouseButtonEventArgs e)
    {
        if (!_scrubbing)
        {
            return;
        }

        // Let the slider apply the click position (move-to-point) before reading the value.
        await Dispatcher.Yield(DispatcherPriority.Input);
        double seconds = Scrubber.Value;
        _scrubbing = false;
        await _media.SeekAsync(TimeSpan.FromSeconds(seconds));
        UpdatePosition();
    }

    private static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();

    private async void Previous_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();

    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        ShuffleButton.IsChecked = _media.ShuffleActive; // the player decides; Changed brings the new state
        await _media.ToggleShuffleAsync();
    }

    private async void Repeat_Click(object sender, RoutedEventArgs e)
    {
        RepeatButton.IsChecked = _media.RepeatMode != Windows.Media.MediaPlaybackAutoRepeatMode.None;
        await _media.CycleRepeatAsync();
    }
}
