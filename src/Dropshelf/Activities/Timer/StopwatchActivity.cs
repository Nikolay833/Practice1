using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Dropshelf.Activities.Hud;
using Dropshelf.Services;

namespace Dropshelf.Activities.Timer;

/// <summary>What is saved of the stopwatch, so it keeps running across restarts like the one on the iPhone.</summary>
internal sealed class StopwatchSnapshot
{
    public DateTime? StartedUtc { get; set; }
    public long BankedTicks { get; set; }
    public List<long> LapTicks { get; set; } = new();
}

/// <summary>
/// The stopwatch: start, stop, lap, reset. It runs on the wall clock (a start timestamp plus the time banked before the last
/// stop), so it keeps counting through sleep and restarts. While it runs the collapsed island shows a stopwatch glyph and the
/// time; opening the island goes to the Stopwatch page of the Clock tab.
/// </summary>
internal sealed class StopwatchActivity : PillActivityBase, IOpensTab, IDisposable
{
    private readonly DispatcherTimer _tick;
    private readonly TextBlock _time;
    private readonly CompactContent _content;
    private readonly List<TimeSpan> _laps = new();
    private DateTime? _startedUtc;
    private TimeSpan _banked;

    public StopwatchActivity()
    {
        StopwatchSnapshot? saved = JsonFile.Load<StopwatchSnapshot>(AppPaths.StopwatchFile);
        if (saved != null)
        {
            _startedUtc = saved.StartedUtc is { } started && started <= DateTime.UtcNow ? started : null;
            _banked = TimeSpan.FromTicks(Math.Max(0, saved.BankedTicks));
            _laps.AddRange(saved.LapTicks.Select(TimeSpan.FromTicks));
        }

        var glyph = HudParts.Cell(HudParts.Glyph("", "WarnBrush", 16), 26);
        _time = HudParts.Label("00:00", 14, "TextBrush", FontWeights.SemiBold);
        _time.SetResourceReference(TextBlock.FontFamilyProperty, "RoundedFont");
        Typography.SetNumeralAlignment(_time, FontNumeralAlignment.Tabular);
        _time.TextTrimming = TextTrimming.None;
        _content = CompactContent.Of(glyph, HudParts.Cell(_time, 58));

        _tick = new DispatcherTimer(DispatcherPriority.Normal);
        _tick.Tick += (_, _) => OnTick();
        OnTick();
    }

    public override string Id => "stopwatch";
    public override int Priority => 190; // below a running timer
    public override bool IsActive => IsRunning;

    public string TabId => "timer";

    public bool IsRunning => _startedUtc != null;

    public TimeSpan Elapsed => _banked + (_startedUtc is { } started ? Max0(DateTime.UtcNow - started) : TimeSpan.Zero);

    /// <summary>Finished laps, oldest first.</summary>
    public IReadOnlyList<TimeSpan> Laps => _laps;

    /// <summary>The lap that is running now.</summary>
    public TimeSpan CurrentLap => Max0(Elapsed - _laps.Aggregate(TimeSpan.Zero, (a, b) => a + b));

    /// <summary>Start, stop, lap or reset happened.</summary>
    public event Action? Changed;

    /// <summary>The island opened from this activity: the Clock tab should show the stopwatch.</summary>
    public event Action? RevealRequested;

    public void Reveal() => RevealRequested?.Invoke();

    public void StartOrStop()
    {
        if (_startedUtc is { } started)
        {
            _banked += Max0(DateTime.UtcNow - started);
            _startedUtc = null;
        }
        else
        {
            _startedUtc = DateTime.UtcNow;
        }

        OnChanged();
    }

    /// <summary>Running: records a lap. Stopped: back to zero.</summary>
    public void LapOrReset()
    {
        if (IsRunning)
        {
            _laps.Add(CurrentLap);
        }
        else
        {
            _banked = TimeSpan.Zero;
            _laps.Clear();
        }

        OnChanged();
    }

    private void OnChanged()
    {
        JsonFile.Save(AppPaths.StopwatchFile, new StopwatchSnapshot
        {
            StartedUtc = _startedUtc,
            BankedTicks = _banked.Ticks,
            LapTicks = _laps.Select(l => l.Ticks).ToList(),
        });
        OnTick();
        RaiseStateChanged();
        Changed?.Invoke();
    }

    /// <summary>Updates the pill once per second, on the second.</summary>
    private void OnTick()
    {
        _tick.Stop();
        TimeSpan elapsed = Elapsed;
        _time.Text = Format(elapsed, hundredths: false);
        if (IsRunning)
        {
            _tick.Interval = TimeSpan.FromMilliseconds(Math.Max(30, 1000 - elapsed.TotalMilliseconds % 1000 + 10));
            _tick.Start();
        }
    }

    /// <summary>"01:02.34", "1:02:03.45"; without hundredths "01:02" / "1:02:03".</summary>
    public static string Format(TimeSpan time, bool hundredths = true)
    {
        time = Max0(time);
        string main = time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes:00}:{time.Seconds:00}";
        return hundredths ? $"{main}.{time.Milliseconds / 10:00}" : main;
    }

    private static TimeSpan Max0(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    public override CompactContent? CreateCompact() => _content;

    public void Dispose() => _tick.Stop();
}
