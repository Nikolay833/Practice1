using System.Windows;
using System.Windows.Controls;
using Dropshelf.Services;

namespace Dropshelf.Activities.Timer;

/// <summary>The Clock tab: switches between the Alarm, Stopwatch and Timer pages and remembers the last one.</summary>
public partial class ClockView : UserControl
{
    private readonly SettingsService _settings;
    private readonly AlarmView _alarm;
    private readonly StopwatchView _stopwatch;
    private readonly TimerView _timer;

    internal ClockView(TimerActivity timer, StopwatchActivity stopwatch, AlarmActivity alarms, SettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        _alarm = new AlarmView(alarms);
        _stopwatch = new StopwatchView(stopwatch);
        _timer = new TimerView(timer);
        Pages.Children.Add(_alarm);
        Pages.Children.Add(_stopwatch);
        Pages.Children.Add(_timer);
        stopwatch.RevealRequested += () => Show(ClockPage.Stopwatch);
        alarms.RevealRequested += () => Show(ClockPage.Alarm);
        Show(settings.Current.ClockPage);
    }

    /// <summary>The timer runs: opening the island from it shows the Timer page.</summary>
    internal void ShowTimer() => Show(ClockPage.Timer);

    private void Show(ClockPage page)
    {
        _alarm.Visibility = page == ClockPage.Alarm ? Visibility.Visible : Visibility.Collapsed;
        _stopwatch.Visibility = page == ClockPage.Stopwatch ? Visibility.Visible : Visibility.Collapsed;
        _timer.Visibility = page == ClockPage.Timer ? Visibility.Visible : Visibility.Collapsed;
        AlarmOption.IsChecked = page == ClockPage.Alarm;
        StopwatchOption.IsChecked = page == ClockPage.Stopwatch;
        TimerOption.IsChecked = page == ClockPage.Timer;
        _settings.Current.ClockPage = page;
    }

    private void Page_Click(object sender, RoutedEventArgs e) =>
        Show(ReferenceEquals(sender, AlarmOption) ? ClockPage.Alarm : ReferenceEquals(sender, StopwatchOption) ? ClockPage.Stopwatch : ClockPage.Timer);
}
