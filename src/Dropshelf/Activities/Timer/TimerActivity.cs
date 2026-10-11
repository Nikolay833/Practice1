using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Dropshelf.Activities.Hud;
using Dropshelf.Services;
using Dropshelf.Themes;
using Microsoft.Win32;

namespace Dropshelf.Activities.Timer;

/// <summary>
/// Timer and Pomodoro. Collapsed pill while one is running or paused: a progress ring around a small stopwatch on the left,
/// the remaining time (tabular digits) on the right; the ring turns from the accent (or green on a break) to orange during the
/// last minute. The "Timer" tab has presets, custom mm:ss, start / pause / reset and the Pomodoro cycle. When a phase ends the
/// island grows with a spring "ding" HUD and a soft chime plays (unless muted in Settings).
/// The only timer in here ticks once per second while a countdown runs, and is off otherwise.
/// </summary>
internal sealed class TimerActivity : PillActivityBase, IDisposable
{
    // Apple's timer colours: systemOrange while counting down (as in the Clock app and its Live Activity), systemGreen on breaks.
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);

    private readonly SettingsService _settings;
    private readonly ToastActivity _toast;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _tick;
    private readonly ProgressRing _ring;
    private readonly TextBlock _time;
    private readonly Grid _left;
    private readonly Grid _right;
    private CompactContent _content;
    private bool _hours;
    private bool _followAccent = true;
    private bool _disposed;

    public TimerActivity(SettingsService settings, ToastActivity toast)
    {
        _settings = settings;
        _toast = toast;
        Engine = new TimerEngine();
        ApplyPlan();

        TimerSnapshot? saved = JsonFile.Load<TimerSnapshot>(AppPaths.TimerFile);
        if (saved != null)
        {
            Engine.Restore(saved);
        }
        else
        {
            Engine.Reset();
        }

        _ring = new ProgressRing(24, 2.5, "", 10);
        _left = HudParts.Cell(_ring, 26);
        _time = HudParts.Label("00:00", 14, "WarnBrush", FontWeights.SemiBold);
        _time.SetResourceReference(TextBlock.FontFamilyProperty, "RoundedFont");
        Typography.SetNumeralAlignment(_time, FontNumeralAlignment.Tabular);
        _time.TextTrimming = TextTrimming.None;
        _right = HudParts.Cell(_time, 50);
        _content = CompactContent.Of(_left, _right);

        _tick = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
        _tick.Tick += (_, _) => OnTick();

        Engine.Changed += OnEngineChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        UpdateVisuals();
        ScheduleTick();
    }

    public TimerEngine Engine { get; }

    public override string Id => "timer";
    public override int Priority => 200;
    public override string? TabTitle => "Timer";
    public override int TabOrder => -80;
    public override bool HasExpanded => true;
    public override bool IsActive => Engine.Phase != TimerPhase.Idle;

    /// <summary>Raised every second while running, and on every state change: the Timer tab refreshes itself.</summary>
    public event Action? Updated;

    public override CompactContent? CreateCompact() => _content;

    public override FrameworkElement? CreateExpanded() => new TimerView(this);

    // ---- Commands (used by the tab) ---------------------------------------------------------------

    public void StartCountdown(TimeSpan length) => Engine.StartCountdown(length);

    public void StartPomodoro()
    {
        ApplyPlan();
        Engine.StartPomodoro();
    }

    public void PauseOrResume()
    {
        if (Engine.Running)
        {
            Engine.Pause();
        }
        else
        {
            Engine.Resume();
        }
    }

    public void Reset() => Engine.Reset();

    public void Prepare(TimerMode mode, TimeSpan? countdown = null)
    {
        ApplyPlan();
        Engine.Prepare(mode, countdown);
    }

    /// <summary>The Pomodoro lengths changed in Settings.</summary>
    public void ApplySettings()
    {
        ApplyPlan();
        if (Engine.Phase == TimerPhase.Idle)
        {
            Engine.Prepare(Engine.Mode);
        }

        UpdateVisuals();
        Updated?.Invoke();
    }

    /// <summary>The ring and countdown colour: orange, green on breaks.</summary>
    public Color? RingTint => Engine.Phase is TimerPhase.Break or TimerPhase.LongBreak ? Green : Orange;

    // ---- Ticking ----------------------------------------------------------------------------------

    private void ApplyPlan()
    {
        AppSettings s = _settings.Current;
        Engine.Plan = new PomodoroPlan(
            TimeSpan.FromMinutes(s.PomodoroWorkMinutes), TimeSpan.FromMinutes(s.PomodoroBreakMinutes),
            TimeSpan.FromMinutes(s.PomodoroLongBreakMinutes), s.PomodoroRounds);
    }

    private void OnEngineChanged()
    {
        JsonFile.Save(AppPaths.TimerFile, Engine.Snapshot());
        UpdateVisuals();
        ScheduleTick();
        RaiseStateChanged();
        Updated?.Invoke();
    }

    private void OnTick()
    {
        _tick.Stop();
        TimerFinish? finish = Engine.Poll(); // a finished phase raises Engine.Changed, which updates and reschedules
        if (finish is { } f)
        {
            OnFinished(f);
            return;
        }

        UpdateVisuals();
        Updated?.Invoke();
        ScheduleTick();
    }

    /// <summary>Wake at the next whole-second boundary of the remaining time (the clock is the end timestamp, not tick counting).</summary>
    private void ScheduleTick()
    {
        _tick.Stop();
        if (!Engine.Running || _disposed)
        {
            return;
        }

        double fraction = Engine.Remaining.TotalMilliseconds % 1000;
        _tick.Interval = TimeSpan.FromMilliseconds(Math.Max(30, fraction + 12));
        _tick.Start();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.Mode == PowerModes.Suspend)
        {
            Engine.SystemSuspended(); // raised on the system events thread; only notes the clocks
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                Engine.SystemResumed(); // the end may have passed while the computer slept
                OnTick();
            }));
        }
    }

    // ---- Pill content -----------------------------------------------------------------------------

    private void UpdateVisuals()
    {
        TimeSpan remaining = Engine.Remaining;
        int seconds = (int)Math.Ceiling(remaining.TotalSeconds - 0.001);
        bool hours = Engine.Phase != TimerPhase.Idle ? Engine.Total.TotalSeconds >= 3600 : false;
        _time.Text = Format(seconds, hours);
        _ring.SetProgress(Engine.Elapsed);
        _ring.Glyph = Engine.IsPaused ? "" : "";
        _time.SetResourceReference(TextBlock.ForegroundProperty,
            Engine.IsPaused ? "TextSecondaryBrush" : Engine.Phase is TimerPhase.Break or TimerPhase.LongBreak ? "SuccessBrush" : "WarnBrush");

        Color? tint = RingTint;
        if (tint is { } color)
        {
            _ring.SetColor(color);
            _followAccent = false;
        }
        else if (!_followAccent)
        {
            _ring.FollowAccent();
            _followAccent = true;
        }

        if (hours != _hours)
        {
            _hours = hours;
            _right.Width = hours ? 68 : 50;
            _content = CompactContent.Of(_left, _right);
            RaiseStateChanged();
        }
    }

    public static string Format(int totalSeconds, bool forceHours = false)
    {
        totalSeconds = Math.Max(0, totalSeconds);
        int h = totalSeconds / 3600, m = totalSeconds % 3600 / 60, s = totalSeconds % 60;
        return h > 0 || forceHours ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }

    // ---- Finish ------------------------------------------------------------------------------------

    private void OnFinished(TimerFinish finish)
    {
        if (finish.WhileAsleep)
        {
            // Several phases passed while the computer slept: say so, and leave the next phase paused instead of ringing for a stale break.
            _toast.Show(new ToastSpec
            {
                Glyph = "",
                Tone = "WarnBrush",
                Text = "Timer finished while asleep",
                Detail = "press start for the next phase",
                Duration = TimeSpan.FromMilliseconds(3400),
            });
            return;
        }

        string text, detail;
        switch (finish.Finished)
        {
            case TimerPhase.Work:
                text = "Work done";
                detail = finish.Next == TimerPhase.LongBreak
                    ? $"long break, {_settings.Current.PomodoroLongBreakMinutes} min"
                    : $"break, {_settings.Current.PomodoroBreakMinutes} min";
                break;
            case TimerPhase.Break:
                text = "Break over";
                detail = "back to work";
                break;
            case TimerPhase.LongBreak:
                text = "Pomodoro complete";
                detail = "nice work";
                break;
            default:
                text = "Time's up";
                detail = string.Empty;
                break;
        }

        _toast.Show(new ToastSpec
        {
            Glyph = "",
            Tone = finish.Finished == TimerPhase.Work || finish.Finished == TimerPhase.Countdown ? "WarnBrush" : "SuccessBrush",
            Text = text,
            Detail = detail.Length == 0 ? null : detail,
            Bounce = true,
            ExtraHeight = 8,
            Duration = TimeSpan.FromMilliseconds(3400),
        });
        if (_settings.Current.TimerSound)
        {
            Chime.Play();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _tick.Stop();
        Engine.Changed -= OnEngineChanged;
    }
}
