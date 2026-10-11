using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Dropshelf.Services;

namespace Dropshelf.Activities.Calendar;

/// <summary>One agenda entry (from the Windows calendars or an ICS feed), with absolute times.</summary>
internal sealed record CalendarEvent(string Title, string Location, DateTimeOffset Start, DateTimeOffset End, bool AllDay, int Source);

/// <summary>A DTSTART / DTEND / EXDATE value: a wall-clock time plus how to read it (UTC, a time zone, floating local) or a plain date.</summary>
internal readonly record struct IcsTime(DateTime Wall, TimeZoneInfo? Zone, bool IsUtc, bool IsDate)
{
    /// <summary>The instant this value stands for. Dates are local midnight; floating times are local.</summary>
    public DateTimeOffset ToOffset()
    {
        if (IsUtc)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(Wall, DateTimeKind.Unspecified), TimeSpan.Zero);
        }

        TimeZoneInfo zone = IsDate || Zone == null ? TimeZoneInfo.Local : Zone;
        return IcsParser.InZone(Wall, zone);
    }
}

/// <summary>The supported subset of RRULE.</summary>
internal sealed class IcsRule
{
    public string Freq { get; init; } = string.Empty;
    public int Interval { get; init; } = 1;
    public int? Count { get; init; }
    public IcsTime? Until { get; init; }

    /// <summary>BYDAY entries: the weekday and the ordinal (0 = every, 1 = first, -1 = last...).</summary>
    public List<(DayOfWeek Day, int Ordinal)> ByDay { get; init; } = new();

    public List<int> ByMonthDay { get; init; } = new();
}

/// <summary>A VEVENT as written in the feed (before recurrences are expanded).</summary>
internal sealed class IcsEvent
{
    public string Uid { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public IcsTime Start { get; set; }
    public IcsTime? End { get; set; }
    public TimeSpan? Duration { get; set; }
    public IcsRule? Rule { get; set; }
    public List<IcsTime> ExDates { get; } = new();
    public List<IcsTime> RDates { get; } = new();
    public IcsTime? RecurrenceId { get; set; }
    public bool Cancelled { get; set; }
    public bool AllDay => Start.IsDate;
}

/// <summary>
/// A small iCalendar reader for the agenda: line unfolding, DTSTART / DTEND / DURATION with TZID, UTC ("Z") or DATE values,
/// SUMMARY, LOCATION, EXDATE, RDATE, RECURRENCE-ID overrides and RRULE (FREQ=DAILY / WEEKLY / MONTHLY / YEARLY with INTERVAL,
/// COUNT, UNTIL, BYDAY, and BYMONTHDAY for monthly). Anything else in the rule (BYSETPOS, hourly rules...) is ignored, so such
/// an event may show up on the wrong days, but never crashes the parser. Recurrences are expanded in the event's own time zone,
/// so a 09:00 meeting stays at 09:00 across daylight saving changes.
/// </summary>
internal static class IcsParser
{
    private const int MaxOccurrences = 20_000;

    // Hard limits per event: every loop step of the expansion counts, yielded or not, so a rule that never matches cannot spin.
    private const int MaxSteps = 500_000;
    private const long MaxEventMillis = 1_500;
    private const long MaxDays = 3_652_059; // 0001-01-01 .. 9999-12-31

    private static readonly Dictionary<string, TimeZoneInfo?> Zones = new(StringComparer.OrdinalIgnoreCase);

    // ---- Public entry points -----------------------------------------------------------------------

    /// <summary>All events of the feed that overlap [<paramref name="from"/>, <paramref name="to"/>), recurrences expanded, sorted.</summary>
    public static List<CalendarEvent> GetEvents(string text, DateTimeOffset from, DateTimeOffset to, int source)
    {
        var result = new List<CalendarEvent>();
        foreach (IcsEvent e in Parse(text))
        {
            foreach ((DateTimeOffset start, DateTimeOffset end) in SafeOccurrences(e, from, to))
            {
                result.Add(new CalendarEvent(e.Summary, e.Location, start, end, e.AllDay, source));
            }
        }

        result.Sort((a, b) =>
        {
            int c = a.Start.CompareTo(b.Start);
            return c != 0 ? c : string.CompareOrdinal(a.Title, b.Title);
        });
        return result;
    }

    /// <summary>The VEVENTs of a feed. Overrides (RECURRENCE-ID) are folded into their series as exclusions and kept as events of their own.</summary>
    public static List<IcsEvent> Parse(string text)
    {
        var events = new List<IcsEvent>();
        IcsEvent? current = null;
        bool inAlarm = false;

        foreach (string line in Unfold(text))
        {
            if (!TryParseLine(line, out string name, out Dictionary<string, string> parameters, out string value))
            {
                continue;
            }

            switch (name)
            {
                case "BEGIN":
                    if (value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
                    {
                        current = new IcsEvent();
                    }
                    else if (value.Equals("VALARM", StringComparison.OrdinalIgnoreCase))
                    {
                        inAlarm = true;
                    }

                    continue;
                case "END":
                    if (value.Equals("VALARM", StringComparison.OrdinalIgnoreCase))
                    {
                        inAlarm = false;
                    }
                    else if (value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase) && current != null)
                    {
                        if (current.Start.Wall != default)
                        {
                            events.Add(current);
                        }

                        current = null;
                    }

                    continue;
            }

            if (current == null || inAlarm)
            {
                continue;
            }

            try
            {
                switch (name)
                {
                    case "UID": current.Uid = value; break;
                    case "SUMMARY": current.Summary = Unescape(value); break;
                    case "LOCATION": current.Location = Unescape(value); break;
                    case "STATUS": current.Cancelled = value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                    case "DTSTART":
                        if (TryParseTime(value, parameters, out IcsTime start))
                        {
                            current.Start = start;
                        }

                        break;
                    case "DTEND":
                        if (TryParseTime(value, parameters, out IcsTime end))
                        {
                            current.End = end;
                        }

                        break;
                    case "DURATION":
                        if (TryParseDuration(value, out TimeSpan duration))
                        {
                            current.Duration = duration;
                        }

                        break;
                    case "RRULE": current.Rule = ParseRule(value, parameters); break;
                    case "EXDATE":
                        foreach (string part in value.Split(','))
                        {
                            if (TryParseTime(part, parameters, out IcsTime ex))
                            {
                                current.ExDates.Add(ex);
                            }
                        }

                        break;
                    case "RDATE":
                        foreach (string part in value.Split(','))
                        {
                            // RDATE;VALUE=PERIOD is not supported; the start of a period is used.
                            string startPart = part.Split('/')[0];
                            if (TryParseTime(startPart, parameters, out IcsTime rd))
                            {
                                current.RDates.Add(rd);
                            }
                        }

                        break;
                    case "RECURRENCE-ID":
                        if (TryParseTime(value, parameters, out IcsTime rid))
                        {
                            current.RecurrenceId = rid;
                        }

                        break;
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            {
                // One malformed property must not lose the rest of the feed.
            }
        }

        // A changed instance of a series replaces the original instance.
        var byUid = new Dictionary<string, IcsEvent>();
        foreach (IcsEvent master in events.Where(e => e.Uid.Length > 0 && e.RecurrenceId == null && e.Rule != null))
        {
            byUid.TryAdd(master.Uid, master);
        }

        foreach (IcsEvent over in events.Where(e => e.RecurrenceId != null))
        {
            if (byUid.TryGetValue(over.Uid, out IcsEvent? series))
            {
                series.ExDates.Add(over.RecurrenceId!.Value);
            }
        }

        events.RemoveAll(e => e.Cancelled);
        return events;
    }

    // ---- Line level --------------------------------------------------------------------------------

    /// <summary>
    /// RFC 5545 unfolding: a line that starts with a space or tab continues the previous one (the break and that one character
    /// are removed). Handles CRLF, LF and CR line ends and a leading byte-order mark.
    /// </summary>
    public static IEnumerable<string> Unfold(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        var current = new StringBuilder();
        bool any = false;
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (any && line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                current.Append(line, 1, line.Length - 1);
                continue;
            }

            if (any && current.Length > 0)
            {
                yield return current.ToString();
            }

            current.Clear();
            current.Append(line);
            any = true;
        }

        if (any && current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    /// <summary>Splits <c>NAME;PARAM=a;PARAM2="b:c":value</c> (colons and semicolons inside quotes do not count).</summary>
    public static bool TryParseLine(string line, out string name, out Dictionary<string, string> parameters, out string value)
    {
        name = string.Empty;
        value = string.Empty;
        parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        int colon = -1;
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ':' && !quoted)
            {
                colon = i;
                break;
            }
        }

        if (colon <= 0)
        {
            return false;
        }

        value = line[(colon + 1)..];
        var parts = new List<string>();
        var sb = new StringBuilder();
        quoted = false;
        foreach (char c in line.AsSpan(0, colon))
        {
            if (c == '"')
            {
                quoted = !quoted;
                sb.Append(c);
            }
            else if (c == ';' && !quoted)
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        parts.Add(sb.ToString());
        name = parts[0].Trim().ToUpperInvariant();
        int dot = name.LastIndexOf('.'); // Apple's "item1.X-ABLABEL" group prefix
        if (dot >= 0)
        {
            name = name[(dot + 1)..];
        }

        for (int i = 1; i < parts.Count; i++)
        {
            int eq = parts[i].IndexOf('=');
            if (eq > 0)
            {
                parameters[parts[i][..eq].Trim()] = parts[i][(eq + 1)..].Trim().Trim('"');
            }
        }

        return name.Length > 0;
    }

    /// <summary>TEXT unescaping: \n \N newline, \, \; \\.</summary>
    public static string Unescape(string value)
    {
        if (!value.Contains('\\'))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                char next = value[++i];
                sb.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    // ---- Values --------------------------------------------------------------------------------------

    /// <summary>yyyyMMdd (a date), yyyyMMddTHHmmss (floating, or in the TZID zone) or yyyyMMddTHHmmssZ (UTC).</summary>
    public static bool TryParseTime(string value, Dictionary<string, string> parameters, out IcsTime time)
    {
        time = default;
        value = value.Trim();
        parameters.TryGetValue("VALUE", out string? kind);
        bool isDate = value.Length == 8 || string.Equals(kind, "DATE", StringComparison.OrdinalIgnoreCase);

        if (isDate)
        {
            if (value.Length >= 8 && DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
            {
                time = new IcsTime(date, null, false, true);
                return true;
            }

            return false;
        }

        bool utc = value.EndsWith('Z') || value.EndsWith('z');
        string body = utc ? value[..^1] : value;
        string[] formats = ["yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm"];
        if (!DateTime.TryParseExact(body, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime wall))
        {
            return false;
        }

        TimeZoneInfo? zone = null;
        if (!utc && parameters.TryGetValue("TZID", out string? tzid))
        {
            zone = FindZone(tzid);
        }

        time = new IcsTime(wall, zone, utc, false);
        return true;
    }

    /// <summary>ISO 8601 duration as used by DURATION: <c>[+-]P[nW][nD][T[nH][nM][nS]]</c>.</summary>
    public static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = default;
        value = value.Trim();
        int sign = 1;
        if (value.StartsWith('-'))
        {
            sign = -1;
            value = value[1..];
        }
        else if (value.StartsWith('+'))
        {
            value = value[1..];
        }

        if (!value.StartsWith('P') || value.Length < 2)
        {
            return false;
        }

        double days = 0, seconds = 0;
        bool inTime = false;
        var number = new StringBuilder();
        foreach (char c in value.AsSpan(1))
        {
            if (char.IsDigit(c))
            {
                number.Append(c);
                continue;
            }

            if (c == 'T')
            {
                inTime = true;
                continue;
            }

            if (number.Length == 0 || !double.TryParse(number.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out double n))
            {
                return false;
            }

            number.Clear();
            switch (c)
            {
                case 'W': days += 7 * n; break;
                case 'D': days += n; break;
                case 'H' when inTime: seconds += 3600 * n; break;
                case 'M' when inTime: seconds += 60 * n; break;
                case 'S' when inTime: seconds += n; break;
                default: return false;
            }
        }

        duration = sign * (TimeSpan.FromDays(days) + TimeSpan.FromSeconds(seconds));
        return true;
    }

    public static IcsRule? ParseRule(string value, Dictionary<string, string> _)
    {
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string piece in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = piece.IndexOf('=');
            if (eq > 0)
            {
                parts[piece[..eq].Trim()] = piece[(eq + 1)..].Trim();
            }
        }

        if (!parts.TryGetValue("FREQ", out string? freq))
        {
            return null;
        }

        int interval = parts.TryGetValue("INTERVAL", out string? iv) && int.TryParse(iv, out int i) && i > 0 ? Math.Min(i, 10_000) : 1;
        int? count = parts.TryGetValue("COUNT", out string? cv) && int.TryParse(cv, out int c) && c > 0 ? c : null;

        IcsTime? until = null;
        if (parts.TryGetValue("UNTIL", out string? uv) && TryParseTime(uv, new Dictionary<string, string>(), out IcsTime u))
        {
            until = u;
        }

        var rule = new IcsRule { Freq = freq.ToUpperInvariant(), Interval = interval, Count = count, Until = until };
        if (parts.TryGetValue("BYDAY", out string? byDay))
        {
            foreach (string token in byDay.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string t = token.Trim().ToUpperInvariant();
                if (t.Length < 2)
                {
                    continue;
                }

                string dayCode = t[^2..];
                string ordinalText = t[..^2];
                int ordinal = 0;
                if (ordinalText.Length > 0 && !int.TryParse(ordinalText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out ordinal))
                {
                    continue;
                }

                ordinal = Math.Clamp(ordinal, -53, 53);
                DayOfWeek? day = dayCode switch
                {
                    "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
                    "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null,
                };
                if (day != null)
                {
                    rule.ByDay.Add((day.Value, ordinal));
                }
            }
        }

        if (parts.TryGetValue("BYMONTHDAY", out string? byMonthDay))
        {
            foreach (string token in byMonthDay.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int d) && d != 0 && Math.Abs(d) <= 31)
                {
                    rule.ByMonthDay.Add(d);
                }
            }
        }

        return rule;
    }

    // ---- Time zones ----------------------------------------------------------------------------------

    /// <summary>Finds a zone by IANA name ("Europe/Berlin") or Windows name ("W. Europe Standard Time"); null when unknown.</summary>
    public static TimeZoneInfo? FindZone(string id)
    {
        id = id.Trim().Trim('"');
        lock (Zones)
        {
            if (Zones.TryGetValue(id, out TimeZoneInfo? cached))
            {
                return cached;
            }

            TimeZoneInfo? zone = TryFind(id);
            if (zone == null && id.StartsWith('/'))
            {
                // Old "/mozilla.org/20050126_1/Europe/Berlin" style ids: keep the last two segments.
                string[] segments = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2)
                {
                    zone = TryFind(segments[^2] + "/" + segments[^1]) ?? TryFind(segments[^1]);
                }
            }

            Zones[id] = zone;
            return zone;
        }
    }

    private static TimeZoneInfo? TryFind(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            // Maybe an IANA id on a system that only knows Windows ids.
        }

        try
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out string? windows) && windows != null)
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windows);
            }
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException or PlatformNotSupportedException)
        {
            // Unknown: the caller treats the time as local.
        }

        return null;
    }

    /// <summary>Wall-clock time in a zone to an instant. A time that does not exist (spring-forward gap) moves an hour on.</summary>
    public static DateTimeOffset InZone(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall))
        {
            wall = wall.AddHours(1);
        }

        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    // ---- Recurrence ----------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="Occurrences"/> as a list that never throws: a malformed event (dates out of range...) is skipped and logged,
    /// so one bad event cannot take the rest of the feed down.
    /// </summary>
    public static List<(DateTimeOffset Start, DateTimeOffset End)> SafeOccurrences(IcsEvent e, DateTimeOffset from, DateTimeOffset to)
    {
        var list = new List<(DateTimeOffset, DateTimeOffset)>();
        try
        {
            foreach (var occurrence in Occurrences(e, from, to))
            {
                list.Add(occurrence);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidOperationException or FormatException)
        {
            Log.Error($"Calendar event skipped (cannot expand \"{e.Summary}\")", ex);
            list.Clear();
        }

        return list;
    }

    /// <summary>The instances of <paramref name="e"/> that overlap [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Occurrences(IcsEvent e, DateTimeOffset from, DateTimeOffset to)
    {
        DateTimeOffset firstStart = e.Start.ToOffset();
        TimeSpan length = LengthOf(e, firstStart);
        int allDayDays = e.AllDay ? AllDayLength(e) : 0;
        DateTimeOffset horizon = to > DateTimeOffset.MaxValue.AddDays(-1) ? DateTimeOffset.MaxValue : to.AddDays(1);

        // Start and end of the instance whose wall-clock start is `wall` (all-day events end at a local midnight).
        // False when that moment is outside 0001..9999: such an occurrence is skipped.
        bool TryBuild(DateTime wall, out DateTimeOffset s, out DateTimeOffset en)
        {
            s = en = default;
            try
            {
                var at = new IcsTime(wall, e.Start.Zone, e.Start.IsUtc, e.Start.IsDate);
                s = at.ToOffset();
                en = e.AllDay
                    ? new IcsTime(wall.Date.AddDays(allDayDays), null, false, true).ToOffset()
                    : s + length;
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        bool Overlaps(DateTimeOffset s, DateTimeOffset en) =>
            en > s ? s < to && en > from : s >= from && s < to;

        if (e.Rule == null || e.Rule.Freq is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY"))
        {
            if (TryBuild(e.Start.Wall, out var s0, out var e0) && Overlaps(s0, e0) && !IsExcluded(e, s0))
            {
                yield return (s0, e0);
            }

            foreach (IcsTime rdate in e.RDates)
            {
                if (TryBuild(rdate.Wall, out var s1, out var e1) && Overlaps(s1, e1) && !IsExcluded(e, s1))
                {
                    yield return (s1, e1);
                }
            }

            yield break;
        }

        IcsRule rule = e.Rule;
        DateTimeOffset? until = UntilOf(rule);

        var budget = new StepBudget();
        int produced = 0;
        int iterations = 0;
        foreach (DateTime wall in Walls(e, rule, from, budget))
        {
            if (++iterations > MaxOccurrences)
            {
                yield break;
            }

            if (!TryBuild(wall, out var start, out var end))
            {
                continue;
            }

            if (until != null && start > until)
            {
                yield break;
            }

            produced++;
            if (rule.Count != null && produced > rule.Count)
            {
                yield break;
            }

            if (start >= horizon)
            {
                yield break;
            }

            if (Overlaps(start, end) && !IsExcluded(e, start))
            {
                yield return (start, end);
            }
        }

        if (budget.Exhausted)
        {
            Log.Error($"Calendar event \"{e.Summary}\": recurrence expansion stopped (too many steps or too slow)");
        }
    }

    /// <summary>The last instant a rule may produce. UNTIL as a date includes that whole day (no +1 day at the end of the calendar).</summary>
    private static DateTimeOffset? UntilOf(IcsRule rule)
    {
        if (rule.Until is not { } u)
        {
            return null;
        }

        try
        {
            DateTimeOffset until = u.ToOffset();
            return u.IsDate && u.Wall.Year < 9999 ? until.AddDays(1).AddTicks(-1) : until;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MaxValue; // beyond what can be represented: no limit
        }
    }

    /// <summary>Counts every step of a recurrence expansion (not only the yielded ones) and the time spent, so a rule can never loop forever.</summary>
    private sealed class StepBudget
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _steps;

        public bool Exhausted { get; private set; }

        public bool Step()
        {
            if (Exhausted)
            {
                return false;
            }

            if (++_steps > MaxSteps || ((_steps & 0xFF) == 0 && _clock.ElapsedMilliseconds > MaxEventMillis))
            {
                Exhausted = true;
                return false;
            }

            return true;
        }
    }

    private static bool TryAddDays(DateTime date, long days, out DateTime result)
    {
        result = default;
        if (days < -MaxDays || days > MaxDays)
        {
            return false;
        }

        long ticks = date.Ticks + days * TimeSpan.TicksPerDay;
        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        result = new DateTime(ticks, date.Kind);
        return true;
    }

    private static bool TryAt(DateTime day, TimeSpan timeOfDay, out DateTime result)
    {
        result = default;
        if (day.Ticks > DateTime.MaxValue.Ticks - timeOfDay.Ticks)
        {
            return false;
        }

        result = day + timeOfDay;
        return true;
    }

    /// <summary>Days an all-day event spans (DTEND is exclusive; default one day).</summary>
    private static int AllDayLength(IcsEvent e)
    {
        if (e.End is { } end)
        {
            return Math.Max(1, (int)Math.Round((end.Wall.Date - e.Start.Wall.Date).TotalDays));
        }

        return e.Duration is { } d ? Math.Max(1, (int)Math.Round(d.TotalDays)) : 1;
    }

    private static TimeSpan LengthOf(IcsEvent e, DateTimeOffset firstStart)
    {
        if (e.End is { } end)
        {
            TimeSpan span = end.ToOffset() - firstStart;
            if (span >= TimeSpan.Zero)
            {
                return span;
            }
        }

        if (e.Duration is { } d && d >= TimeSpan.Zero)
        {
            return d;
        }

        return e.AllDay ? TimeSpan.FromDays(1) : TimeSpan.Zero;
    }

    private static bool IsExcluded(IcsEvent e, DateTimeOffset start)
    {
        foreach (IcsTime ex in e.ExDates)
        {
            try
            {
                if (ex.IsDate)
                {
                    if (ex.Wall.Date == (e.AllDay ? start.Date : start.ToLocalTime().Date))
                    {
                        return true;
                    }
                }
                else if (ex.ToOffset() == start)
                {
                    return true;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // An exclusion outside 0001..9999 matches nothing.
            }
        }

        return false;
    }

    /// <summary>Wall-clock start times of a series in order, beginning at DTSTART (fast-forwarded for daily / weekly rules without COUNT).</summary>
    private static IEnumerable<DateTime> Walls(IcsEvent e, IcsRule rule, DateTimeOffset from, StepBudget budget)
    {
        DateTime start = e.Start.Wall;
        TimeSpan timeOfDay = e.Start.IsDate ? TimeSpan.Zero : start.TimeOfDay;
        int interval = Math.Clamp(rule.Interval, 1, 10_000);

        switch (rule.Freq)
        {
            case "DAILY":
            {
                long k = 0;
                if (rule.Count == null)
                {
                    double days = (from.UtcDateTime.Date - start.Date).TotalDays - 2;
                    k = Math.Max(0, (long)Math.Floor(days / interval));
                }

                // Every pass counts against the budget: a BYDAY that never matches (INTERVAL=7;BYDAY=MO from a Tuesday) yields nothing.
                for (; budget.Step(); k++)
                {
                    if (!TryAddDays(start.Date, k * interval, out DateTime day))
                    {
                        yield break;
                    }

                    if (rule.ByDay.Count > 0 && !rule.ByDay.Any(b => b.Day == day.DayOfWeek))
                    {
                        continue;
                    }

                    if (!TryAt(day, timeOfDay, out DateTime wall))
                    {
                        yield break;
                    }

                    yield return wall;
                }

                yield break;
            }

            case "WEEKLY":
            {
                // Weeks start on Monday (WKST is not read).
                DateTime weekStart = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                var days = rule.ByDay.Count > 0
                    ? rule.ByDay.Select(b => b.Day).Distinct().OrderBy(d => ((int)d + 6) % 7).ToList()
                    : new List<DayOfWeek> { start.DayOfWeek };
                long k = 0;
                if (rule.Count == null)
                {
                    double weeks = (from.UtcDateTime.Date - weekStart).TotalDays / 7 - 2;
                    k = Math.Max(0, (long)Math.Floor(weeks / interval));
                }

                for (; budget.Step(); k++)
                {
                    if (!TryAddDays(weekStart, 7L * interval * k, out DateTime ws))
                    {
                        yield break;
                    }

                    foreach (DayOfWeek d in days)
                    {
                        if (!budget.Step())
                        {
                            yield break;
                        }

                        if (!TryAddDays(ws, ((int)d + 6) % 7, out DateTime day) || !TryAt(day, timeOfDay, out DateTime wall))
                        {
                            yield break;
                        }

                        if (wall < start)
                        {
                            continue;
                        }

                        yield return wall;
                    }
                }

                yield break;
            }

            case "MONTHLY":
            {
                for (long k = 0; k < 50_000 && budget.Step(); k++)
                {
                    long monthIndex = start.Year * 12L + (start.Month - 1) + k * interval;
                    long year = monthIndex / 12;
                    int month = (int)(monthIndex % 12) + 1;
                    if (year > 9998)
                    {
                        yield break;
                    }

                    foreach (DateTime day in MonthlyDays(rule, start, (int)year, month))
                    {
                        if (day + timeOfDay < start)
                        {
                            continue;
                        }

                        yield return day + timeOfDay;
                    }
                }

                yield break;
            }

            default: // YEARLY
            {
                for (long k = 0; k < 50_000 && budget.Step(); k++)
                {
                    long year = start.Year + k * interval;
                    if (year > 9998)
                    {
                        yield break;
                    }

                    if (start.Day <= DateTime.DaysInMonth((int)year, start.Month))
                    {
                        yield return new DateTime((int)year, start.Month, start.Day) + timeOfDay;
                    }
                }

                yield break;
            }
        }
    }

    /// <summary>The days of one month a monthly rule selects: BYDAY with ordinals (first Monday, last Friday), BYMONTHDAY, or the start's day.</summary>
    private static List<DateTime> MonthlyDays(IcsRule rule, DateTime start, int year, int month)
    {
        int daysInMonth = DateTime.DaysInMonth(year, month);
        var days = new SortedSet<int>();

        if (rule.ByDay.Count > 0)
        {
            foreach ((DayOfWeek day, int ordinal) in rule.ByDay)
            {
                var matches = Enumerable.Range(1, daysInMonth).Where(d => new DateTime(year, month, d).DayOfWeek == day).ToList();
                if (ordinal == 0)
                {
                    foreach (int d in matches)
                    {
                        days.Add(d);
                    }
                }
                else if (ordinal > 0 && ordinal <= matches.Count)
                {
                    days.Add(matches[ordinal - 1]);
                }
                else if (ordinal < 0 && -ordinal <= matches.Count)
                {
                    days.Add(matches[matches.Count + ordinal]);
                }
            }
        }
        else if (rule.ByMonthDay.Count > 0)
        {
            foreach (int d in rule.ByMonthDay)
            {
                int day = d > 0 ? d : daysInMonth + d + 1;
                if (day >= 1 && day <= daysInMonth)
                {
                    days.Add(day);
                }
            }
        }
        else if (start.Day <= daysInMonth)
        {
            days.Add(start.Day);
        }

        return days.Select(d => new DateTime(year, month, d)).ToList();
    }
}
