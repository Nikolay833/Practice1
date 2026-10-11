using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Threading;
using Dropshelf.Services;
using Microsoft.Win32;
using Windows.ApplicationModel.Appointments;

namespace Dropshelf.Activities.Calendar;

internal enum WindowsCalendarState
{
    /// <summary>Turned off in Settings.</summary>
    Off,

    /// <summary>The appointment store needs package identity (the installed MSIX build); a plain exe cannot read it.</summary>
    Unavailable,

    Available,
}

/// <summary>The ICS feed URLs as they are stored (encrypted, they usually contain a secret token).</summary>
internal sealed class CalendarSecrets
{
    public List<string> IcsUrls { get; set; } = new();
}

/// <summary>
/// Today's and tomorrow's events from two sources: the Windows appointment store (packaged builds only) and any number of ICS
/// feeds. Feeds are fetched every 15 minutes and after resume, parsed off the UI thread and kept in memory; the merged list
/// is rebuilt after each fetch and at midnight. The feed URLs live in <c>calendar.json</c>, encrypted with DPAPI, and are
/// never written to the log. Nothing runs while the calendar is switched off.
/// </summary>
internal sealed class CalendarService : IDisposable
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);

    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _midnight;
    private readonly Dictionary<string, List<IcsEvent>> _feeds = new();
    private List<CalendarEvent> _windowsEvents = new();
    private DateTime _builtFor = DateTime.MinValue;
    private bool _refreshing;
    private bool _loggedWindowsFailure;
    private bool _disposed;

    public CalendarService(SettingsService settings)
    {
        _settings = settings;
        Current = this;
        IcsUrls = JsonFile.LoadProtected<CalendarSecrets>(AppPaths.CalendarFile, out _)?.IcsUrls ?? new List<string>();

        _timer = new DispatcherTimer { Interval = RefreshEvery };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _midnight = new DispatcherTimer();
        _midnight.Tick += (_, _) =>
        {
            _ = RebuildAsync();
            ScheduleMidnight();
        };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        ApplySettings();
    }

    public static CalendarService? Current { get; private set; }

    public bool Enabled => _settings.Current.CalendarEnabled;

    /// <summary>Events overlapping today or tomorrow, earliest first.</summary>
    public IReadOnlyList<CalendarEvent> Events { get; private set; } = Array.Empty<CalendarEvent>();

    public IReadOnlyList<string> IcsUrls { get; private set; }

    public WindowsCalendarState Windows { get; private set; } = WindowsCalendarState.Off;

    /// <summary>At least one feed could not be fetched on the last attempt (the last good copy is still used).</summary>
    public bool Offline { get; private set; }

    public event Action? Changed;

    /// <summary>Starts or stops the timers to match the settings.</summary>
    public void ApplySettings()
    {
        if (Enabled)
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }

            ScheduleMidnight();
            _ = RefreshAsync();
        }
        else
        {
            _timer.Stop();
            _midnight.Stop();
            Events = Array.Empty<CalendarEvent>();
            Windows = WindowsCalendarState.Off;
            Offline = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Saves a new list of feed URLs (encrypted) and refreshes.</summary>
    public void SetIcsUrls(IEnumerable<string> urls)
    {
        var cleaned = urls.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (cleaned.SequenceEqual(IcsUrls))
        {
            return;
        }

        IcsUrls = cleaned;
        JsonFile.SaveProtected(AppPaths.CalendarFile, new CalendarSecrets { IcsUrls = cleaned });
        _feeds.Clear();
        if (Enabled)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>Rebuilds the list if the day changed since it was built (the Today tab calls this when it opens).</summary>
    public void EnsureCurrentDay()
    {
        if (Enabled && _builtFor != DateTime.Today)
        {
            _ = RebuildAsync();
        }
    }

    public async Task RefreshAsync()
    {
        if (!Enabled || _refreshing || _disposed)
        {
            return;
        }

        _refreshing = true;
        try
        {
            bool anyFailed = false;
            foreach (string url in IcsUrls.ToList())
            {
                (List<IcsEvent>? events, string? error) = await FetchFeedAsync(url);
                if (events != null)
                {
                    _feeds[url] = events;
                }
                else
                {
                    anyFailed = true;
                    Log.Error($"Calendar feed {HostOf(url)} could not be loaded: {error}");
                }
            }

            foreach (string gone in _feeds.Keys.Where(k => !IcsUrls.Contains(k)).ToList())
            {
                _feeds.Remove(gone);
            }

            Offline = anyFailed;
            await RebuildAsync(refreshWindows: true);
        }
        catch (Exception ex)
        {
            Log.Error("Calendar refresh failed", ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Fetches one feed and returns how many events it holds and how many fall in the next seven days (the Settings test button).</summary>
    public static async Task<(int Total, int NextWeek, string? Error)> TestAsync(string url)
    {
        (List<IcsEvent>? events, string? error) = await FetchFeedAsync(url);
        if (events == null)
        {
            return (0, 0, error);
        }

        DateTimeOffset from = DateTimeOffset.Now, to = from.AddDays(7);
        int next = await Task.Run(() => events.Sum(e => IcsParser.Occurrences(e, from, to).Count()));
        return (events.Count, next, null);
    }

    // ---- Building the list --------------------------------------------------------------------------

    private async Task RebuildAsync(bool refreshWindows = false)
    {
        DateTime today = DateTime.Today;
        DateTimeOffset from = new(today, TimeZoneInfo.Local.GetUtcOffset(today));
        DateTime end = today.AddDays(2);
        DateTimeOffset to = new(end, TimeZoneInfo.Local.GetUtcOffset(end));

        if (refreshWindows || Windows == WindowsCalendarState.Off)
        {
            await ReadWindowsAsync(from, to);
        }

        var feeds = _feeds.Values.ToList();
        var urls = _feeds.Keys.ToList();
        List<CalendarEvent> merged = await Task.Run(() =>
        {
            var all = new List<CalendarEvent>(_windowsEvents.Where(e => e.Start < to && e.End > from));
            for (int i = 0; i < feeds.Count; i++)
            {
                int source = 1 + IcsUrls.ToList().IndexOf(urls[i]);
                foreach (IcsEvent e in feeds[i])
                {
                    foreach ((DateTimeOffset s, DateTimeOffset en) in IcsParser.Occurrences(e, from, to))
                    {
                        all.Add(new CalendarEvent(e.Summary, e.Location, s, en, e.AllDay, Math.Max(1, source)));
                    }
                }
            }

            all.Sort((a, b) =>
            {
                int c = a.AllDay == b.AllDay ? a.Start.CompareTo(b.Start) : a.AllDay ? -1 : 1;
                return c != 0 ? c : string.CompareOrdinal(a.Title, b.Title);
            });
            return all;
        });

        if (_disposed || !Enabled)
        {
            return;
        }

        Events = merged;
        _builtFor = today;
        Changed?.Invoke();
    }

    private void ScheduleMidnight()
    {
        _midnight.Stop();
        TimeSpan untilMidnight = DateTime.Today.AddDays(1) - DateTime.Now + TimeSpan.FromSeconds(3);
        _midnight.Interval = untilMidnight < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : untilMidnight;
        _midnight.Start();
    }

    // ---- Sources ------------------------------------------------------------------------------------

    /// <summary>Windows calendars through the appointment store. Needs package identity, so a plain exe lands in the catch.</summary>
    private async Task ReadWindowsAsync(DateTimeOffset from, DateTimeOffset to)
    {
        if (!_settings.Current.CalendarUseWindows)
        {
            Windows = WindowsCalendarState.Off;
            _windowsEvents = new List<CalendarEvent>();
            return;
        }

        try
        {
            AppointmentStore? store = await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
            if (store == null)
            {
                Windows = WindowsCalendarState.Unavailable;
                _windowsEvents = new List<CalendarEvent>();
                return;
            }

            var options = new FindAppointmentsOptions { MaxCount = 200 };
            foreach (string property in AppointmentProperties.DefaultProperties)
            {
                options.FetchProperties.Add(property);
            }

            IReadOnlyList<Appointment> found = await store.FindAppointmentsAsync(from, to - from, options);
            _windowsEvents = found.Select(a => new CalendarEvent(
                string.IsNullOrWhiteSpace(a.Subject) ? "(no title)" : a.Subject,
                a.Location ?? string.Empty,
                a.StartTime,
                a.StartTime + a.Duration,
                a.AllDay,
                0)).ToList();
            Windows = WindowsCalendarState.Available;
        }
        catch (Exception ex)
        {
            // No package identity (a loose exe), access denied by the user, or no calendar app: the source is simply unavailable.
            Windows = WindowsCalendarState.Unavailable;
            _windowsEvents = new List<CalendarEvent>();
            if (!_loggedWindowsFailure)
            {
                _loggedWindowsFailure = true;
                Log.Info($"Windows calendars unavailable ({ex.GetType().Name}); they need the installed (MSIX) version");
            }
        }
    }

    /// <summary>Downloads and parses one ICS feed (https only; webcal:// is read as https://).</summary>
    private static async Task<(List<IcsEvent>? Events, string? Error)> FetchFeedAsync(string url)
    {
        if (!TryNormalize(url, out Uri? uri))
        {
            return (null, "not an http(s) or webcal URL");
        }

        try
        {
            string text = await Net.Client.GetStringAsync(uri);
            List<IcsEvent> events = await Task.Run(() => IcsParser.Parse(text));
            if (events.Count == 0 && !text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            {
                return (null, "not an iCalendar feed");
            }

            return (events, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, ex is TaskCanceledException ? "timed out" : ex.Message);
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name);
        }
    }

    public static bool TryNormalize(string url, out Uri? uri)
    {
        url = url.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url["webcal://".Length..];
        }
        else if (url.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url["webcals://".Length..];
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            return true;
        }

        uri = null;
        return false;
    }

    /// <summary>The host of a URL for log lines (the rest of it may contain a secret token).</summary>
    private static string HostOf(string url) => TryNormalize(url, out Uri? uri) ? uri!.Host : "(invalid URL)";

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && !_disposed)
        {
            _dispatcher.BeginInvoke(new Action(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(8)); // wait for the network
                if (Enabled && !_disposed)
                {
                    ScheduleMidnight();
                    await RefreshAsync();
                }
            }));
        }
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _timer.Stop();
        _midnight.Stop();
    }
}
