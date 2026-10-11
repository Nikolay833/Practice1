using System;

namespace Dropshelf.Activities.Timer;

internal enum TimerMode
{
    /// <summary>A plain countdown (5 min, 10 min, a custom length).</summary>
    Countdown,

    /// <summary>Work / break cycles: work, short break, ... and a long break after the last round.</summary>
    Pomodoro,
}

internal enum TimerPhase
{
    Idle,
    Countdown,
    Work,
    Break,
    LongBreak,
}

/// <summary>Pomodoro lengths (from the settings).</summary>
internal readonly record struct PomodoroPlan(TimeSpan Work, TimeSpan Break, TimeSpan LongBreak, int Rounds);

/// <summary>
/// What <see cref="TimerEngine.Poll"/> reports when a phase ended. <paramref name="WhileAsleep"/>: the computer slept through so much
/// that the next phase had already ended too, so the timer stopped (paused, ready to start) instead of starting a stale phase.
/// </summary>
internal readonly record struct TimerFinish(TimerPhase Finished, TimerPhase Next, bool WhileAsleep = false);

/// <summary>Everything the timer needs to come back after a restart.</summary>
internal sealed class TimerSnapshot
{
    public TimerMode Mode { get; set; }
    public TimerPhase Phase { get; set; }
    public int Round { get; set; } = 1;
    public double TotalSeconds { get; set; }
    public double RemainingSeconds { get; set; }
    public bool Running { get; set; }
    public DateTime? EndUtc { get; set; }
    public double LastCountdownSeconds { get; set; } = 25 * 60;
}

/// <summary>
/// The timer's state machine, with no UI and no timers of its own. Time is kept as an end point on a monotonic clock while
/// running (<c>end = start + length</c>) and the remaining time is always <c>end - now</c>; ticks are never counted, and a manual
/// or NTP change of the wall clock does not move the countdown. The monotonic clock may stand still while the computer sleeps,
/// so <see cref="SystemSuspended"/> / <see cref="SystemResumed"/> add the wall-clock time that passed asleep. A computer that
/// sleeps through the end finds <c>now &gt;= end</c> when it wakes. Call <see cref="Poll"/> now and then (the activity does so
/// once per second while running and after resume). The wall clock is only used for the snapshot that survives a restart.
/// </summary>
internal sealed class TimerEngine
{
    private readonly Func<DateTime> _now;           // wall clock: persistence and the time spent asleep only
    private readonly Func<long> _ticks;             // monotonic milliseconds
    private readonly object _sleepGate = new();
    private long _endTick;
    private TimeSpan _remaining;     // while paused / idle
    private DateTime? _suspendedWall;
    private long _suspendedTick;

    public TimerEngine(Func<DateTime>? utcNow = null, Func<long>? monotonicMilliseconds = null)
    {
        _now = utcNow ?? (() => DateTime.UtcNow);
        _ticks = monotonicMilliseconds ?? (() => Environment.TickCount64);
    }

    public TimerMode Mode { get; private set; } = TimerMode.Countdown;
    public TimerPhase Phase { get; private set; } = TimerPhase.Idle;
    public bool Running { get; private set; }
    public TimeSpan Total { get; private set; } = TimeSpan.FromMinutes(25);
    public int Round { get; private set; } = 1;
    public PomodoroPlan Plan { get; set; } = new(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), 4);

    /// <summary>The length the plain countdown was last started with (the Reset button goes back to it).</summary>
    public TimeSpan LastCountdown { get; private set; } = TimeSpan.FromMinutes(25);

    public bool IsPaused => Phase != TimerPhase.Idle && !Running;

    public TimeSpan Remaining
    {
        get
        {
            if (!Running)
            {
                return _remaining;
            }

            long left = _endTick - _ticks();
            return left <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(left);
        }
    }

    /// <summary>0 at the start of the phase, 1 when it ends.</summary>
    public double Elapsed => Total <= TimeSpan.Zero ? 0 : Math.Clamp(1 - Remaining.TotalSeconds / Total.TotalSeconds, 0, 1);

    public event Action? Changed;

    public void StartCountdown(TimeSpan length)
    {
        if (length < TimeSpan.FromSeconds(1))
        {
            return;
        }

        Mode = TimerMode.Countdown;
        LastCountdown = length;
        Begin(TimerPhase.Countdown, length);
    }

    public void StartPomodoro()
    {
        Mode = TimerMode.Pomodoro;
        Round = 1;
        Begin(TimerPhase.Work, Plan.Work);
    }

    public void Pause()
    {
        if (!Running)
        {
            return;
        }

        _remaining = Remaining;
        Running = false;
        Changed?.Invoke();
    }

    public void Resume()
    {
        if (Running || Phase == TimerPhase.Idle || _remaining <= TimeSpan.Zero)
        {
            return;
        }

        _endTick = EndAfter(_remaining);
        Running = true;
        Changed?.Invoke();
    }

    public void Reset()
    {
        Running = false;
        Phase = TimerPhase.Idle;
        Round = 1;
        Total = Mode == TimerMode.Pomodoro ? Plan.Work : LastCountdown;
        _remaining = Total;
        Changed?.Invoke();
    }

    /// <summary>Selects the mode while idle so the big clock shows the matching length.</summary>
    public void Prepare(TimerMode mode, TimeSpan? countdown = null)
    {
        if (Phase != TimerPhase.Idle)
        {
            return;
        }

        Mode = mode;
        if (countdown is { } length && length > TimeSpan.Zero)
        {
            LastCountdown = length;
        }

        Total = mode == TimerMode.Pomodoro ? Plan.Work : LastCountdown;
        _remaining = Total;
        Changed?.Invoke();
    }

    /// <summary>The computer is going to sleep (any thread). Remembers both clocks to tell how long it slept.</summary>
    public void SystemSuspended()
    {
        lock (_sleepGate)
        {
            _suspendedWall = _now();
            _suspendedTick = _ticks();
        }
    }

    /// <summary>
    /// The computer woke up. The wall-clock time that passed while asleep, minus what the monotonic clock counted anyway, is taken
    /// off the running phase (zero if the monotonic clock already includes sleep, or if the wall clock was set back).
    /// </summary>
    public void SystemResumed()
    {
        TimeSpan asleep;
        lock (_sleepGate)
        {
            if (_suspendedWall is not { } wall)
            {
                return;
            }

            asleep = _now() - wall - TimeSpan.FromMilliseconds(_ticks() - _suspendedTick);
            _suspendedWall = null;
        }

        if (Running && asleep > TimeSpan.Zero)
        {
            _endTick -= (long)asleep.TotalMilliseconds;
        }
    }

    /// <summary>
    /// Checks whether the running phase has ended. A finished countdown goes back to idle; a finished Pomodoro phase starts the
    /// next one (counting from now). If the computer slept so long that the next phase would have ended too, nothing is started:
    /// the next phase is ready and paused and the result has <see cref="TimerFinish.WhileAsleep"/> set. Returns null when nothing ended.
    /// </summary>
    public TimerFinish? Poll()
    {
        if (!Running || _ticks() < _endTick)
        {
            return null;
        }

        TimeSpan overdue = TimeSpan.FromMilliseconds(_ticks() - _endTick);
        TimerPhase finished = Phase;
        if (Mode == TimerMode.Countdown)
        {
            Running = false;
            Phase = TimerPhase.Idle;
            Total = LastCountdown;
            _remaining = Total;
            Changed?.Invoke();
            return new TimerFinish(finished, TimerPhase.Idle);
        }

        TimerPhase next;
        switch (finished)
        {
            case TimerPhase.Work:
                next = Round >= Plan.Rounds ? TimerPhase.LongBreak : TimerPhase.Break;
                break;
            case TimerPhase.Break:
                Round++;
                next = TimerPhase.Work;
                break;
            default: // the long break ends the cycle
                Running = false;
                Phase = TimerPhase.Idle;
                Round = 1;
                Total = Plan.Work;
                _remaining = Total;
                Changed?.Invoke();
                return new TimerFinish(finished, TimerPhase.Idle);
        }

        TimeSpan length = next switch { TimerPhase.Work => Plan.Work, TimerPhase.Break => Plan.Break, _ => Plan.LongBreak };
        if (overdue >= length)
        {
            // Stale: the next phase is already over. Stop at the end of the phase that ended.
            Phase = next;
            Total = length;
            _remaining = length;
            Running = false;
            Changed?.Invoke();
            return new TimerFinish(finished, next, WhileAsleep: true);
        }

        Begin(next, length);
        return new TimerFinish(finished, next);
    }

    // ---- Persistence -------------------------------------------------------------------------------

    public TimerSnapshot Snapshot() => new()
    {
        Mode = Mode,
        Phase = Phase,
        Round = Round,
        TotalSeconds = Total.TotalSeconds,
        RemainingSeconds = Remaining.TotalSeconds,
        Running = Running,
        EndUtc = Running ? _now() + Remaining : null,
        LastCountdownSeconds = LastCountdown.TotalSeconds,
    };

    /// <summary>
    /// Restores a snapshot. A phase that ended while the app was closed is dropped (no late ding): the timer comes back idle.
    /// </summary>
    public void Restore(TimerSnapshot s)
    {
        LastCountdown = TimeSpan.FromSeconds(Math.Clamp(s.LastCountdownSeconds, 1, 24 * 3600));
        Mode = s.Mode;
        Round = Math.Max(1, s.Round);
        Total = TimeSpan.FromSeconds(Math.Clamp(s.TotalSeconds, 1, 24 * 3600));

        bool alive = s.Phase != TimerPhase.Idle;
        if (alive && s.Running && s.EndUtc is { } end)
        {
            if (end > _now())
            {
                Phase = s.Phase;
                _endTick = EndAfter(end - _now());
                Running = true;
                return;
            }

            alive = false;
        }

        if (alive && !s.Running && s.RemainingSeconds > 0)
        {
            Phase = s.Phase;
            _remaining = TimeSpan.FromSeconds(Math.Min(s.RemainingSeconds, Total.TotalSeconds));
            Running = false;
            return;
        }

        Phase = TimerPhase.Idle;
        Running = false;
        Round = 1;
        Total = Mode == TimerMode.Pomodoro ? Plan.Work : LastCountdown;
        _remaining = Total;
    }

    private void Begin(TimerPhase phase, TimeSpan length)
    {
        Phase = phase;
        Total = length;
        _remaining = length;
        _endTick = EndAfter(length);
        Running = true;
        Changed?.Invoke();
    }

    /// <summary>The monotonic reading at which a phase of this length, started now, ends.</summary>
    private long EndAfter(TimeSpan length) => _ticks() + (long)length.TotalMilliseconds;
}
