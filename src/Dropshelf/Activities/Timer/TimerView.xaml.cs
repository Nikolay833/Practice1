using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Dropshelf.Animation;

namespace Dropshelf.Activities.Timer;

/// <summary>
/// The "Timer" tab, in the style of the iOS StandBy timer: an orange minute ruler (drag it, scroll over it or click a minute;
/// 1 to 120 minutes) with a marker under the chosen minute, a "Start Timer" capsule and the time in large orange digits.
/// While it runs the ruler follows the remaining time and the buttons become Pause / Resume and Cancel. "Pomodoro" switches
/// to focus and break rounds (lengths in Settings).
/// </summary>
public partial class TimerView : UserControl
{
    private const int MaxMinutes = 120;
    private const double Spacing = 11;         // pixels per minute on the ruler
    private const double TickTop = 24, TickHeight = 34, TickWidth = 3;

    private static readonly Brush StartFill = Frozen(Color.FromArgb(0x40, 0xFF, 0x9F, 0x0A));
    private static readonly Brush BreakFill = Frozen(Color.FromArgb(0x40, 0x30, 0xD1, 0x58));

    private readonly TimerActivity _activity;
    private readonly List<Rectangle> _ticks = new();
    private readonly Spring _position = new(25, 520, 1.0);  // the minute under the marker (fractional while moving)
    private readonly FrameLoop _loop;
    private int _selectedMinutes = 25;
    private bool _dragging;
    private double _dragStartX, _dragStartPosition;
    private bool _pomodoro;

    internal TimerView(TimerActivity activity)
    {
        InitializeComponent();
        _activity = activity;
        _loop = new FrameLoop(OnFrame);
        BuildRuler();

        // Show what the engine holds (it may have been restored from disk).
        _pomodoro = _activity.Engine.Mode == TimerMode.Pomodoro;
        _selectedMinutes = Math.Clamp((int)Math.Round(_activity.Engine.LastCountdown.TotalMinutes), 1, MaxMinutes);
        _position.Snap(_selectedMinutes);

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

    // ---- Ruler ------------------------------------------------------------------------------------

    /// <summary>One tick per minute (a label every five), drawn once; moving the ruler only shifts the canvas.</summary>
    private void BuildRuler()
    {
        for (int m = 0; m <= MaxMinutes; m++)
        {
            var tick = new Rectangle { Width = TickWidth, Height = TickHeight, RadiusX = 1.5, RadiusY = 1.5 };
            tick.SetResourceReference(Shape.FillProperty, "WarnBrush");
            Canvas.SetLeft(tick, m * Spacing - TickWidth / 2);
            Canvas.SetTop(tick, TickTop);
            Ruler.Children.Add(tick);
            _ticks.Add(tick);

            if (m % 5 == 0)
            {
                var label = new TextBlock
                {
                    Text = m.ToString(CultureInfo.CurrentCulture),
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Width = 30,
                    TextAlignment = TextAlignment.Center,
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
                label.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
                Canvas.SetLeft(label, m * Spacing - 15);
                Canvas.SetTop(label, 0);
                Ruler.Children.Add(label);
            }
        }
    }

    /// <summary>Puts minute <paramref name="position"/> under the marker and dims the ticks past it.</summary>
    private void ApplyRuler(double position)
    {
        RulerShift.X = RulerHost.ActualWidth / 2 - position * Spacing;
        for (int m = 0; m < _ticks.Count; m++)
        {
            double opacity = m <= position + 0.001 ? 1 : 0.35;
            if (_ticks[m].Opacity != opacity)
            {
                _ticks[m].Opacity = opacity;
            }
        }
    }

    private void MoveRulerTo(double minutes, bool animate)
    {
        _position.Target = minutes;
        if (!animate || AnimationSettings.IsOff || !IsVisible)
        {
            _loop.Stop();
            _position.Snap(minutes);
            ApplyRuler(minutes);
            return;
        }

        AnimationSettings.Configure(_position, 520, 1.0);
        _loop.Start();
    }

    private bool OnFrame(double dt)
    {
        _position.Step(dt);
        ApplyRuler(_position.Value);
        return !_position.SettleIfClose(0.002, 0.02);
    }

    private bool CanPick => _activity.Engine.Phase == TimerPhase.Idle && !_pomodoro;

    private void Pick(int minutes, bool animate)
    {
        _selectedMinutes = Math.Clamp(minutes, 1, MaxMinutes);
        _activity.Prepare(TimerMode.Countdown, TimeSpan.FromMinutes(_selectedMinutes));
        MoveRulerTo(_selectedMinutes, animate);
        Refresh();
    }

    private void Ruler_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyRuler(_position.Value);

    private void Ruler_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanPick)
        {
            return;
        }

        _dragging = true;
        _dragStartX = e.GetPosition(RulerHost).X;
        _dragStartPosition = _position.Value;
        RulerHost.CaptureMouse();
        e.Handled = true;
    }

    private void Ruler_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        double position = Math.Clamp(_dragStartPosition - (e.GetPosition(RulerHost).X - _dragStartX) / Spacing, 1, MaxMinutes);
        _loop.Stop();
        _position.Snap(position);
        ApplyRuler(position);
        int minutes = (int)Math.Round(position);
        if (minutes != _selectedMinutes)
        {
            _selectedMinutes = minutes;
            TimeText.Text = TimerActivity.Format(minutes * 60);
        }
    }

    private void Ruler_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        RulerHost.ReleaseMouseCapture();
        double moved = Math.Abs(e.GetPosition(RulerHost).X - _dragStartX);
        if (moved < 3)
        {
            // A click: pick the minute that was clicked.
            double x = e.GetPosition(RulerHost).X - RulerShift.X;
            Pick((int)Math.Round(x / Spacing), animate: true);
        }
        else
        {
            Pick((int)Math.Round(_position.Value), animate: true); // settle on the nearest whole minute
        }
    }

    private void Ruler_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CanPick)
        {
            Pick(_selectedMinutes + (e.Delta > 0 ? 1 : -1), animate: true);
            e.Handled = true;
        }
    }

    // ---- State ------------------------------------------------------------------------------------

    private void Refresh()
    {
        TimerEngine e = _activity.Engine;
        bool idle = e.Phase == TimerPhase.Idle;
        bool onBreak = e.Phase is TimerPhase.Break or TimerPhase.LongBreak;

        if (!_dragging)
        {
            int seconds = (int)Math.Ceiling(e.Remaining.TotalSeconds - 0.001);
            TimeText.Text = TimerActivity.Format(seconds, e.Total.TotalSeconds >= 3600);

            // Idle: the chosen minute. Running: the ruler follows the remaining time.
            double minutes = idle && !_pomodoro ? _selectedMinutes : Math.Min(MaxMinutes, e.Remaining.TotalMinutes);
            if (Math.Abs(_position.Target - minutes) > 0.001)
            {
                MoveRulerTo(minutes, animate: idle);
            }
        }

        string brush = onBreak ? "SuccessBrush" : "WarnBrush";
        TimeText.SetResourceReference(TextBlock.ForegroundProperty, brush);
        Marker.SetResourceReference(Shape.FillProperty, brush);
        RulerHost.Opacity = idle && _pomodoro ? 0.4 : 1;
        RulerHost.Cursor = CanPick ? Cursors.SizeWE : Cursors.Arrow;

        PhaseText.Text = e.Phase switch
        {
            TimerPhase.Idle => _pomodoro ? $"{e.Plan.Rounds} rounds" : string.Empty,
            TimerPhase.Countdown => e.Running ? string.Empty : "Paused",
            TimerPhase.Work => $"Focus {e.Round}/{e.Plan.Rounds}" + (e.Running ? string.Empty : " · Paused"),
            TimerPhase.Break => "Break" + (e.Running ? string.Empty : " · Paused"),
            _ => "Long break" + (e.Running ? string.Empty : " · Paused"),
        };

        StartButton.Content = e.Running ? "Pause" : e.IsPaused ? "Resume" : "Start Timer";
        StartButton.Background = onBreak ? BreakFill : StartFill;
        StartButton.SetResourceReference(ForegroundProperty, brush);
        ResetButton.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        PomodoroToggle.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        PomodoroToggle.IsChecked = _pomodoro;
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
        else
        {
            _activity.StartCountdown(TimeSpan.FromMinutes(_selectedMinutes));
        }

        Keyboard.ClearFocus();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _activity.Reset();
        if (!_pomodoro)
        {
            _activity.Prepare(TimerMode.Countdown, TimeSpan.FromMinutes(_selectedMinutes));
        }

        Keyboard.ClearFocus();
        Refresh();
    }

    private void Pomodoro_Click(object sender, RoutedEventArgs e)
    {
        if (_activity.Engine.Phase != TimerPhase.Idle)
        {
            return;
        }

        _pomodoro = PomodoroToggle.IsChecked == true;
        if (_pomodoro)
        {
            _activity.Prepare(TimerMode.Pomodoro);
        }
        else
        {
            _activity.Prepare(TimerMode.Countdown, TimeSpan.FromMinutes(_selectedMinutes));
        }

        Refresh();
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
