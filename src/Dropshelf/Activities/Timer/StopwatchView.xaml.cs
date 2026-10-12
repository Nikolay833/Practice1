using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Dropshelf.Activities.Timer;

/// <summary>
/// The Stopwatch page of the Clock tab. The digits are redrawn about 30 times a second, and only while the page is visible and
/// the stopwatch runs. With three or more laps the fastest is green and the slowest red, as on the iPhone.
/// </summary>
public partial class StopwatchView : UserControl
{
    private static readonly Brush GreenFill = Frozen(Color.FromArgb(0x40, 0x30, 0xD1, 0x58));
    private static readonly Brush RedFill = Frozen(Color.FromArgb(0x40, 0xFF, 0x45, 0x3A));

    private readonly StopwatchActivity _stopwatch;
    private readonly DispatcherTimer _frame;
    private TextBlock? _currentLapTime;

    internal StopwatchView(StopwatchActivity stopwatch)
    {
        InitializeComponent();
        _stopwatch = stopwatch;
        _frame = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _frame.Tick += (_, _) => UpdateTime();

        Loaded += (_, _) => _stopwatch.Changed += Refresh;
        Unloaded += (_, _) =>
        {
            _stopwatch.Changed -= Refresh;
            _frame.Stop();
        };
        IsVisibleChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        bool running = _stopwatch.IsRunning;
        bool zero = _stopwatch.Elapsed == TimeSpan.Zero;
        StartButton.Content = running ? "Stop" : "Start";
        StartButton.Background = running ? RedFill : GreenFill;
        StartButton.SetResourceReference(ForegroundProperty, running ? "DangerBrush" : "SuccessBrush");
        LapButton.Content = running || zero ? "Lap" : "Reset";
        LapButton.IsEnabled = running || !zero;
        BuildLaps();
        UpdateTime();

        if (running && IsVisible)
        {
            _frame.Start();
        }
        else
        {
            _frame.Stop();
        }
    }

    private void UpdateTime()
    {
        TimeText.Text = StopwatchActivity.Format(_stopwatch.Elapsed);
        if (_currentLapTime != null)
        {
            _currentLapTime.Text = StopwatchActivity.Format(_stopwatch.CurrentLap);
        }
    }

    private void BuildLaps()
    {
        LapList.Children.Clear();
        _currentLapTime = null;
        var laps = _stopwatch.Laps;
        if (laps.Count == 0 && _stopwatch.Elapsed == TimeSpan.Zero)
        {
            return;
        }

        TimeSpan fastest = laps.Count >= 2 ? laps.Min() : TimeSpan.MinValue;
        TimeSpan slowest = laps.Count >= 2 ? laps.Max() : TimeSpan.MaxValue;
        _currentLapTime = AddRow(laps.Count + 1, _stopwatch.CurrentLap, "TextBrush");
        for (int i = laps.Count - 1; i >= 0; i--)
        {
            string brush = laps[i] == fastest ? "SuccessBrush" : laps[i] == slowest ? "DangerBrush" : "TextBrush";
            AddRow(i + 1, laps[i], brush);
        }
    }

    private TextBlock AddRow(int number, TimeSpan time, string brush)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        var name = new TextBlock { Text = $"Lap {number}", FontSize = 12, FontWeight = FontWeights.Medium };
        var value = new TextBlock { Text = StopwatchActivity.Format(time), FontSize = 12, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Right };
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        name.SetResourceReference(TextBlock.ForegroundProperty, brush);
        value.SetResourceReference(TextBlock.ForegroundProperty, brush);
        row.Children.Add(name);
        row.Children.Add(value);
        LapList.Children.Add(row);
        if (LapList.Children.Count > 1)
        {
            LapList.Children.Insert(LapList.Children.Count - 1, new Border { Height = 1, Margin = new Thickness(0, 0, 0, 3), Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)) });
        }

        return value;
    }

    private void Start_Click(object sender, RoutedEventArgs e) => _stopwatch.StartOrStop();

    private void Lap_Click(object sender, RoutedEventArgs e) => _stopwatch.LapOrReset();

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
