using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dropshelf.Activities.Hud;

namespace Dropshelf.Activities.Timer;

/// <summary>The "Timer" tab: big ring with the time, presets (5, 10, 25 min, Pomodoro), custom mm:ss, start / pause / reset.</summary>
public partial class TimerView : UserControl
{
    private readonly TimerActivity _activity;
    private readonly ProgressRing _ring = new(104, 5);
    private bool _loading = true;
    private TimeSpan _selected = TimeSpan.FromMinutes(25);
    private bool _pomodoro;

    internal TimerView(TimerActivity activity)
    {
        InitializeComponent();
        _activity = activity;
        RingHost.Children.Insert(0, _ring);

        // Show what the engine holds (it may have been restored from disk).
        _pomodoro = _activity.Engine.Mode == TimerMode.Pomodoro;
        _selected = _activity.Engine.LastCountdown;
        SelectChip();
        CustomBox.Text = TimerActivity.Format((int)_selected.TotalSeconds);
        _loading = false;

        Loaded += (_, _) =>
        {
            _activity.Updated += OnUpdated;
            Refresh();
        };
        Unloaded += (_, _) => _activity.Updated -= OnUpdated;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Refresh();
            }
        };
        Refresh();
    }

    private void OnUpdated()
    {
        if (IsVisible)
        {
            Refresh();
        }
    }

    private void SelectChip()
    {
        Preset5.IsChecked = !_pomodoro && _selected == TimeSpan.FromMinutes(5);
        Preset10.IsChecked = !_pomodoro && _selected == TimeSpan.FromMinutes(10);
        Preset25.IsChecked = !_pomodoro && _selected == TimeSpan.FromMinutes(25);
        PresetPomodoro.IsChecked = _pomodoro;
    }

    private void Refresh()
    {
        TimerEngine e = _activity.Engine;
        int seconds = (int)Math.Ceiling(e.Remaining.TotalSeconds - 0.001);
        TimeText.Text = TimerActivity.Format(seconds, e.Total.TotalSeconds >= 3600);
        _ring.SetProgress(e.Elapsed);
        if (_activity.RingTint is { } tint)
        {
            _ring.SetColor(tint);
        }
        else
        {
            _ring.FollowAccent();
        }

        PhaseText.Text = e.Phase switch
        {
            TimerPhase.Idle => "ready",
            TimerPhase.Countdown => e.Running ? "counting down" : "paused",
            TimerPhase.Work => (e.Running ? "focus " : "paused ") + $"{e.Round}/{e.Plan.Rounds}",
            TimerPhase.Break => e.Running ? "break" : "break, paused",
            _ => e.Running ? "long break" : "long break, paused",
        };

        StartButton.Content = e.Running ? "Pause" : e.IsPaused ? "Resume" : "Start";
        ResetButton.IsEnabled = e.Phase != TimerPhase.Idle || e.Remaining != e.Total;
        bool idle = e.Phase == TimerPhase.Idle;
        Presets.IsEnabled = idle;
        CustomBox.IsEnabled = idle;

        PresetPomodoro.Content = $"Pomodoro {e.Plan.Work.TotalMinutes:0}/{e.Plan.Break.TotalMinutes:0}";
        Hint.Text = _pomodoro && idle
            ? $"{e.Plan.Rounds} rounds, then {e.Plan.LongBreak.TotalMinutes:0} min break"
            : string.Empty;
    }

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton rb || _activity.Engine.Phase != TimerPhase.Idle)
        {
            return;
        }

        _loading = true;
        if (ReferenceEquals(rb, PresetPomodoro))
        {
            _pomodoro = true;
            _activity.Prepare(TimerMode.Pomodoro);
        }
        else
        {
            _pomodoro = false;
            _selected = TimeSpan.FromMinutes(ReferenceEquals(rb, Preset5) ? 5 : ReferenceEquals(rb, Preset10) ? 10 : 25);
            CustomBox.Text = TimerActivity.Format((int)_selected.TotalSeconds);
            _activity.Prepare(TimerMode.Countdown, _selected);
        }

        _loading = false;
        Refresh();
    }

    private void CustomBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _activity.Engine.Phase != TimerPhase.Idle || !TryParse(CustomBox.Text, out TimeSpan value))
        {
            return;
        }

        _loading = true;
        _pomodoro = false;
        _selected = value;
        SelectChip();
        _activity.Prepare(TimerMode.Countdown, value);
        _loading = false;
        Refresh();
    }

    private void CustomBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Start_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        TimerEngine engine = _activity.Engine;
        if (engine.Running || engine.IsPaused)
        {
            _activity.PauseOrResume();
        }
        else if (_pomodoro)
        {
            _activity.StartPomodoro();
        }
        else if (TryParse(CustomBox.Text, out TimeSpan custom))
        {
            _activity.StartCountdown(custom);
        }
        else
        {
            _activity.StartCountdown(_selected);
        }

        Keyboard.ClearFocus();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _activity.Reset();
        Keyboard.ClearFocus();
    }

    /// <summary>"7:30" = 7 min 30 s, "45" = 45 min, "1:05:00" = 1 h 5 min. Between 1 second and 24 hours.</summary>
    internal static bool TryParse(string text, out TimeSpan value)
    {
        value = default;
        string[] parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3 || Array.Exists(parts, p => p.Length == 0 || p.Length > 3))
        {
            return false;
        }

        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        double seconds = parts.Length switch
        {
            1 => numbers[0] * 60.0,
            2 => numbers[0] * 60.0 + numbers[1],
            _ => numbers[0] * 3600.0 + numbers[1] * 60.0 + numbers[2],
        };
        if (seconds < 1 || seconds > 24 * 3600)
        {
            return false;
        }

        value = TimeSpan.FromSeconds(seconds);
        return true;
    }
}
