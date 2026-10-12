using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Dropshelf.Activities.Timer;

/// <summary>The Alarm page of the Clock tab: pick a time and add it; switch alarms on and off; Snooze / Stop while one rings.</summary>
public partial class AlarmView : UserControl
{
    private readonly AlarmActivity _alarms;
    private int _hour;
    private int _minute;

    internal AlarmView(AlarmActivity alarms)
    {
        InitializeComponent();
        _alarms = alarms;
        List.ItemsSource = alarms.Alarms;

        // Start at the next quarter hour.
        DateTime start = DateTime.Now.AddMinutes(15 - DateTime.Now.Minute % 15);
        _hour = start.Hour;
        _minute = start.Minute;
        ShowPicker();

        Loaded += (_, _) => _alarms.Changed += Refresh;
        Unloaded += (_, _) => _alarms.Changed -= Refresh;
        Refresh();
    }

    private void Refresh()
    {
        EmptyText.Visibility = _alarms.Alarms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_alarms.Ringing is { } ringing)
        {
            RingingTime.Text = DateTime.Today.AddHours(ringing.Hour).AddMinutes(ringing.Minute).ToString("t", CultureInfo.CurrentCulture);
            RingingLabel.Text = ringing.Label.Length > 0 ? ringing.Label : "Alarm";
            RingingPanel.Visibility = Visibility.Visible;
            Editor.Visibility = Visibility.Hidden;
        }
        else
        {
            RingingPanel.Visibility = Visibility.Collapsed;
            Editor.Visibility = Visibility.Visible;
        }
    }

    private void ShowPicker()
    {
        string time = AlarmActivity.FormatTime(_hour, _minute, out string suffix);
        HourText.Text = time[..time.IndexOf(':')];
        MinuteText.Text = _minute.ToString("00", CultureInfo.CurrentCulture);
        AmPmButton.Content = suffix;
        AmPmButton.Visibility = suffix.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StepHour(int step)
    {
        _hour = (_hour + step + 24) % 24;
        ShowPicker();
    }

    private void StepMinute(int step)
    {
        _minute = (_minute + step + 60) % 60;
        ShowPicker();
    }

    private void Hour_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        StepHour(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void Minute_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 5;
        StepMinute(e.Delta > 0 ? step : -step);
        e.Handled = true;
    }

    private void HourUp_Click(object sender, RoutedEventArgs e) => StepHour(1);

    private void HourDown_Click(object sender, RoutedEventArgs e) => StepHour(-1);

    private void MinuteUp_Click(object sender, RoutedEventArgs e) => StepMinute(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 5);

    private void MinuteDown_Click(object sender, RoutedEventArgs e) => StepMinute(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : -5);

    private void AmPm_Click(object sender, RoutedEventArgs e) => StepHour(12);

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        _alarms.Add(_hour, _minute);
        Refresh();
    }

    private void Repeat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Alarm alarm })
        {
            alarm.Daily = !alarm.Daily;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Alarm alarm })
        {
            _alarms.Remove(alarm);
            Refresh();
        }
    }

    private void Snooze_Click(object sender, RoutedEventArgs e) => _alarms.Snooze();

    private void Stop_Click(object sender, RoutedEventArgs e) => _alarms.Stop();
}
