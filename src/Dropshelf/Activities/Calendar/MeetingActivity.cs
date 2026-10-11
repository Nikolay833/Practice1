using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Dropshelf.Activities.Hud;
using Dropshelf.Services;

namespace Dropshelf.Activities.Calendar;

/// <summary>
/// "Meeting in 5 min · Title" on the pill during the ten minutes before an event starts (a calendar glyph on the left, the
/// countdown and title on the right; it counts down in minutes, then in m:ss for the last 100 seconds), and a short HUD when the
/// event starts. One timer, always set for the next moment something has to change; nothing runs when no event is near.
/// </summary>
internal sealed class MeetingActivity : PillActivityBase, IDisposable
{
    private static readonly TimeSpan Lead = TimeSpan.FromMinutes(10);

    private readonly SettingsService _settings;
    private readonly CalendarService _calendar;
    private readonly ToastActivity _toast;
    private readonly DispatcherTimer _timer;
    private readonly System.Windows.Controls.TextBlock _text;
    private readonly CompactContent _content;
    private CalendarEvent? _next;
    private bool _active;
    private string _announced = string.Empty;

    public MeetingActivity(SettingsService settings, CalendarService calendar, ToastActivity toast)
    {
        _settings = settings;
        _calendar = calendar;
        _toast = toast;

        var glyph = HudParts.Glyph("", "AccentBrush", 15);
        _text = HudParts.Label(string.Empty, 13, "TextBrush", FontWeights.SemiBold);
        _text.Width = 220;
        _content = CompactContent.Of(HudParts.Cell(glyph, 24), HudParts.Cell(_text, 220), extraHeight: 2);

        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) => Evaluate();
        _calendar.Changed += Evaluate;
        Evaluate();
    }

    public override string Id => "meeting";
    public override int Priority => 350;
    public override bool IsActive => _active;

    public override CompactContent? CreateCompact() => _content;

    /// <summary>The "Meeting alerts" setting changed.</summary>
    public void ApplySettings() => Evaluate();

    private void Evaluate()
    {
        _timer.Stop();
        bool enabled = _settings.Current.MeetingAlerts && _calendar.Enabled;
        DateTimeOffset now = DateTimeOffset.Now;

        CalendarEvent? next = enabled
            ? _calendar.Events.Where(e => !e.AllDay && e.Start >= now.AddSeconds(-30)).OrderBy(e => e.Start).FirstOrDefault()
            : null;

        // The moment an event starts: a HUD (once, and only if we are actually there when it happens, not after a long sleep).
        if (next != null && next.Start <= now)
        {
            string key = next.Start.UtcTicks + "|" + next.Title;
            if (key != _announced)
            {
                _announced = key;
                _toast.Show(new ToastSpec
                {
                    Glyph = "",
                    Text = "Meeting now",
                    Detail = Shorten(next.Title),
                    Bounce = true,
                    ExtraHeight = 6,
                    Duration = TimeSpan.FromSeconds(4),
                });
            }

            next = _calendar.Events.Where(e => !e.AllDay && e.Start > now).OrderBy(e => e.Start).FirstOrDefault();
        }

        bool active = next != null && next.Start - now <= Lead;
        _next = next;
        if (active)
        {
            _text.Text = Describe(next!, now);
        }

        if (active != _active)
        {
            _active = active;
            RaiseStateChanged();
        }

        Schedule(now);
    }

    /// <summary>Wakes at the next moment the text or the state changes: the next minute / second tick, the start, or ten minutes before.</summary>
    private void Schedule(DateTimeOffset now)
    {
        if (_next == null)
        {
            return;
        }

        TimeSpan until = _next.Start - now;
        TimeSpan wait;
        if (until > Lead)
        {
            wait = until - Lead;
        }
        else if (until > TimeSpan.FromSeconds(100))
        {
            // "in N min" changes when the remaining time crosses a whole minute.
            double seconds = until.TotalSeconds;
            wait = TimeSpan.FromSeconds(seconds - (Math.Ceiling(seconds / 60) - 1) * 60 + 0.05);
            TimeSpan toSecondsMode = TimeSpan.FromSeconds(seconds - 100);
            if (toSecondsMode > TimeSpan.Zero && toSecondsMode < wait)
            {
                wait = toSecondsMode;
            }
        }
        else
        {
            wait = TimeSpan.FromMilliseconds(Math.Max(50, until.TotalMilliseconds % 1000 + 20));
            if (until < wait)
            {
                wait = until + TimeSpan.FromMilliseconds(20);
            }
        }

        _timer.Interval = wait < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : wait;
        _timer.Start();
    }

    private static string Describe(CalendarEvent e, DateTimeOffset now)
    {
        TimeSpan until = e.Start - now;
        string when = until > TimeSpan.FromSeconds(100)
            ? $"in {Math.Ceiling(until.TotalSeconds / 60):0} min"
            : $"in {(int)Math.Max(0, until.TotalSeconds) / 60}:{(int)Math.Max(0, Math.Ceiling(until.TotalSeconds)) % 60:00}";
        return $"Meeting {when} · {e.Title}";
    }

    private static string Shorten(string title) => title.Length <= 28 ? title : title[..27] + "…";

    public void Dispose()
    {
        _calendar.Changed -= Evaluate;
        _timer.Stop();
    }
}
