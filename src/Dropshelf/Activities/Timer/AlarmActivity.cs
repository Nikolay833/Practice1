using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Dropshelf.Activities.Hud;
using Dropshelf.Services;
using Microsoft.Win32;

namespace Dropshelf.Activities.Timer;

/// <summary>One alarm: a time of day, on or off, once or every day, with an optional label.</summary>
public sealed class Alarm : NotifyBase
{
    private bool _enabled = true;
    private bool _daily;

    public Guid Id { get; set; } = Guid.NewGuid();
    public int Hour { get; set; }
    public int Minute { get; set; }
    public string Label { get; set; } = string.Empty;

    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }

    public bool Daily
    {
        get => _daily;
        set
        {
            if (SetField(ref _daily, value))
            {
                OnPropertyChanged(nameof(Detail));
            }
        }
    }

    /// <summary>When it rings next (local time); not saved, worked out again at start-up.</summary>
    [JsonIgnore] public DateTime Next { get; set; }

    /// <summary>"7:30" (12-hour clocks show the AM / PM separately).</summary>
    [JsonIgnore] public string TimeText => AlarmActivity.FormatTime(Hour, Minute, out _);

    [JsonIgnore]
    public string AmPm
    {
        get
        {
            AlarmActivity.FormatTime(Hour, Minute, out string suffix);
            return suffix;
        }
    }

    /// <summary>"Alarm", "Wake up, every day".</summary>
    [JsonIgnore] public string Detail => (Label.Length > 0 ? Label : "Alarm") + (Daily ? ", every day" : string.Empty);
}

/// <summary>
/// Alarms. A one-shot timer wakes at the next alarm (checking at least every 30 s, so clock changes and sleep are noticed).
/// When one goes off the island shows it with Snooze and Stop and a chime repeats for up to a minute; a one-time alarm then
/// switches itself off, like on the iPhone. An alarm whose time passed while the PC slept rings on wake only if it is less
/// than five minutes late.
/// </summary>
internal sealed class AlarmActivity : PillActivityBase, IOpensTab, IDisposable
{
    private static readonly TimeSpan RingFor = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SnoozeFor = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan LateLimit = TimeSpan.FromMinutes(5);

    private readonly SettingsService _settings;
    private readonly ActivityHost _host;
    private readonly DispatcherTimer _check;
    private readonly DispatcherTimer _chime;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private DateTime _ringStarted;
    private DateTime? _snoozedUntil;
    private Alarm? _snoozed;

    public AlarmActivity(SettingsService settings, ActivityHost host)
    {
        _settings = settings;
        _host = host;
        foreach (Alarm alarm in JsonFile.Load<List<Alarm>>(AppPaths.AlarmsFile) ?? new List<Alarm>())
        {
            alarm.Hour = Math.Clamp(alarm.Hour, 0, 23);
            alarm.Minute = Math.Clamp(alarm.Minute, 0, 59);
            Watch(alarm);
            Alarms.Add(alarm);
        }

        Sort();
        _check = new DispatcherTimer(DispatcherPriority.Normal);
        _check.Tick += (_, _) => Check();
        _chime = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _chime.Tick += (_, _) => OnChimeTick();
        SystemEvents.TimeChanged += OnClockChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Schedule();
    }

    /// <summary>All alarms, earliest time of day first.</summary>
    public ObservableCollection<Alarm> Alarms { get; } = new();

    /// <summary>The alarm that is going off, or null.</summary>
    public Alarm? Ringing { get; private set; }

    public override string Id => "alarm";
    public override int Priority => 300;
    public override TimeSpan HudDuration => RingFor;
    public override bool QueueHudWhileExpanded => true;
    public override bool ShowInFullscreen => true;

    public string TabId => "timer";

    /// <summary>The list or the ringing state changed.</summary>
    public event Action? Changed;

    public event Action? RevealRequested;

    public void Reveal() => RevealRequested?.Invoke();

    // ---- Editing ------------------------------------------------------------------------------------

    public Alarm Add(int hour, int minute)
    {
        var alarm = new Alarm { Hour = hour, Minute = minute };
        Watch(alarm);
        Alarms.Add(alarm);
        Sort();
        Save();
        Schedule();
        return alarm;
    }

    public void Remove(Alarm alarm)
    {
        if (ReferenceEquals(Ringing, alarm))
        {
            Stop();
        }

        Alarms.Remove(alarm);
        Save();
        Schedule();
    }

    private void Watch(Alarm alarm) => alarm.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName is nameof(Alarm.Enabled) or nameof(Alarm.Daily))
        {
            Save();
            Schedule();
        }
    };

    private void Sort()
    {
        var sorted = Alarms.OrderBy(a => a.Hour * 60 + a.Minute).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int at = Alarms.IndexOf(sorted[i]);
            if (at != i)
            {
                Alarms.Move(at, i);
            }
        }
    }

    private void Save() => JsonFile.Save(AppPaths.AlarmsFile, Alarms.ToList());

    // ---- Scheduling ---------------------------------------------------------------------------------

    /// <summary>Works out every alarm's next time and sets the wake-up timer for the earliest.</summary>
    private void Schedule()
    {
        DateTime now = DateTime.Now;
        foreach (Alarm alarm in Alarms)
        {
            alarm.Next = NextOccurrence(alarm, now);
        }

        Arm(now);
        Changed?.Invoke();
    }

    private void Arm(DateTime now)
    {
        _check.Stop();
        DateTime? next = Alarms.Where(a => a.Enabled).Select(a => (DateTime?)a.Next).Min();
        if (_snoozedUntil is { } snooze && (next == null || snooze < next))
        {
            next = snooze;
        }

        if (next == null)
        {
            return;
        }

        TimeSpan wait = next.Value - now;
        _check.Interval = wait <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(50)
            : wait < TimeSpan.FromSeconds(30) ? wait + TimeSpan.FromMilliseconds(20) : TimeSpan.FromSeconds(30);
        _check.Start();
    }

    private static DateTime NextOccurrence(Alarm alarm, DateTime after)
    {
        DateTime today = after.Date.AddHours(alarm.Hour).AddMinutes(alarm.Minute);
        return today > after ? today : today.AddDays(1);
    }

    private void Check()
    {
        DateTime now = DateTime.Now;
        if (_snoozedUntil is { } snooze && now >= snooze)
        {
            Alarm? snoozed = _snoozed;
            _snoozedUntil = null;
            _snoozed = null;
            if (snoozed != null && now - snooze < LateLimit)
            {
                Ring(snoozed);
            }
        }

        foreach (Alarm alarm in Alarms.Where(a => a.Enabled).ToList())
        {
            if (now < alarm.Next)
            {
                continue;
            }

            bool late = now - alarm.Next >= LateLimit;
            if (!alarm.Daily)
            {
                alarm.Enabled = false; // a one-time alarm switches itself off (this saves and reschedules)
            }

            if (!late)
            {
                Ring(alarm);
            }
        }

        DateTime fresh = DateTime.Now;
        foreach (Alarm alarm in Alarms.Where(a => a.Enabled && a.Next <= fresh))
        {
            alarm.Next = NextOccurrence(alarm, fresh);
        }

        Arm(fresh);
    }

    private void OnClockChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(new Action(Schedule));

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _dispatcher.BeginInvoke(new Action(Check)); // an alarm may have passed while asleep
        }
    }

    // ---- Ringing ------------------------------------------------------------------------------------

    private void Ring(Alarm alarm)
    {
        Ringing = alarm;
        _ringStarted = DateTime.UtcNow;
        PlayChime();
        _chime.Start();
        RaiseStateChanged();
        RequestHud();
        Changed?.Invoke();
    }

    private void OnChimeTick()
    {
        if (Ringing == null || DateTime.UtcNow - _ringStarted >= RingFor)
        {
            Stop();
            return;
        }

        PlayChime();
        if (!_host.IsHudShowing || !ReferenceEquals(_host.Current, this))
        {
            RequestHud(); // something else took the island for a moment (volume): come back
        }
    }

    private void PlayChime()
    {
        if (_settings.Current.TimerSound)
        {
            Chime.Play();
        }
    }

    /// <summary>Silences the alarm.</summary>
    public void Stop()
    {
        _chime.Stop();
        if (Ringing == null)
        {
            return;
        }

        Ringing = null;
        _host.DismissHud(this);
        RaiseStateChanged();
        Changed?.Invoke();
    }

    /// <summary>Silences the alarm and rings it again in nine minutes.</summary>
    public void Snooze()
    {
        Alarm? alarm = Ringing;
        Stop();
        if (alarm != null)
        {
            _snoozed = alarm;
            _snoozedUntil = DateTime.Now + SnoozeFor;
            Arm(DateTime.Now);
            Changed?.Invoke();
        }
    }

    /// <summary>"Snoozed until 7:39", or null.</summary>
    public string? SnoozeText => _snoozedUntil is { } until ? "Snoozed until " + until.ToString("t", CultureInfo.CurrentCulture) : null;

    public override CompactContent? CreateCompact()
    {
        if (Ringing is not { } alarm)
        {
            return null;
        }

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(HudParts.Cell(HudParts.Glyph("", "WarnBrush", 16), 24));
        var label = HudParts.Label(alarm.Label.Length > 0 ? alarm.Label : DateTime.Today.AddHours(alarm.Hour).AddMinutes(alarm.Minute).ToString("t", CultureInfo.CurrentCulture),
            13, "TextBrush", FontWeights.SemiBold);
        label.Margin = new Thickness(6, 0, 0, 0);
        label.MaxWidth = 110;
        left.Children.Add(label);

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(PillButton("Snooze", "FillBrush", "TextBrush", Snooze));
        right.Children.Add(PillButton("Stop", "WarnBrush", "BlackBrush", Stop));
        ((FrameworkElement)right.Children[1]).Margin = new Thickness(6, 0, 0, 0);
        return CompactContent.Of(left, right, extraHeight: 12, interactive: true);
    }

    private static Button PillButton(string text, string background, string foreground, Action click)
    {
        var content = HudParts.Label(text, 12, foreground, FontWeights.SemiBold);
        var border = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 4, 12, 4), Child = content };
        border.SetResourceReference(Border.BackgroundProperty, background);
        var factory = new FrameworkElementFactory(typeof(ContentPresenter));
        var button = new Button
        {
            Content = border,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = factory },
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>The time of day the way this PC shows it: "7:30" + "AM", or "19:30" + "".</summary>
    public static string FormatTime(int hour, int minute, out string suffix)
    {
        DateTimeFormatInfo format = CultureInfo.CurrentCulture.DateTimeFormat;
        bool twelve = format.ShortTimePattern.Contains('h');
        if (!twelve)
        {
            suffix = string.Empty;
            return $"{hour:00}:{minute:00}";
        }

        suffix = hour < 12 ? format.AMDesignator : format.PMDesignator;
        int h = hour % 12 == 0 ? 12 : hour % 12;
        return $"{h}:{minute:00}";
    }

    public void Dispose()
    {
        _check.Stop();
        _chime.Stop();
        SystemEvents.TimeChanged -= OnClockChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
