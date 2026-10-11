using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Dropshelf.Activities.Media;

/// <summary>
/// The "Now Playing" tab: big art over a soft glow of itself, title, artist, source app, a seekable scrubber and the
/// transport buttons. The scrubber position is refreshed four times a second, and only while the tab is visible.
/// </summary>
public partial class NowPlayingView : UserControl
{
    private const string PlayGlyph = "", PauseGlyph = "";

    private readonly MediaSessionService _media;
    private readonly DispatcherTimer _timer;
    private bool _scrubbing;

    internal NowPlayingView(MediaSessionService media)
    {
        InitializeComponent();
        _media = media;

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
        Refresh();
        if (IsVisible)
        {
            _timer.Start();
        }
    }

    private void Detach()
    {
        _media.Changed -= OnMediaChanged;
        _timer.Stop();
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
        SourceText.Text = _media.SourceApp;
        ArtImage.Source = _media.Art;

        PlayPauseButton.Content = _media.IsPlaying ? PauseGlyph : PlayGlyph;
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
}
