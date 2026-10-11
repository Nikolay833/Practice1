using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Dropshelf.Activities.Hud;
using Dropshelf.Activities.Weather;
using Dropshelf.Services;
using Dropshelf.Themes;

namespace Dropshelf.Activities.Calendar;

/// <summary>
/// The "Today" tab. Left: a weather card (big temperature, condition, high / low and a five-day strip). Right: today's and
/// tomorrow's events with a coloured bar per source, past events dimmed and a "now" marker. Redrawn when the weather or the
/// calendar changes, and once a minute while the tab is visible (that timer stops when it is hidden).
/// </summary>
public partial class TodayView : UserControl
{
    private static readonly Color[] SourceColors =
    [
        Color.FromRgb(0x3B, 0x82, 0xF6), // Windows calendars use the accent instead (see ColorOf)
        Color.FromRgb(0x14, 0xB8, 0xA6),
        Color.FromRgb(0xA8, 0x55, 0xF7),
        Color.FromRgb(0xF9, 0x73, 0x16),
        Color.FromRgb(0xEC, 0x48, 0x99),
        Color.FromRgb(0x22, 0xC5, 0x5E),
    ];

    private readonly SettingsService _settings;
    private readonly DispatcherTimer _minute;

    internal TodayView(SettingsService settings)
    {
        InitializeComponent();
        _settings = settings;

        _minute = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _minute.Tick += (_, _) => RenderAgenda(keepScroll: true);

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                OnShown();
            }
            else
            {
                _minute.Stop();
            }
        };
        RenderAll();
    }

    private void Attach()
    {
        Detach();
        if (WeatherService.Current is { } weather)
        {
            weather.Changed += RenderWeather;
        }

        if (CalendarService.Current is { } calendar)
        {
            calendar.Changed += OnCalendarChanged;
        }

        _settings.Changed += OnSettingChanged;
        if (IsVisible)
        {
            OnShown();
        }
    }

    private void Detach()
    {
        if (WeatherService.Current is { } weather)
        {
            weather.Changed -= RenderWeather;
        }

        if (CalendarService.Current is { } calendar)
        {
            calendar.Changed -= OnCalendarChanged;
        }

        _settings.Changed -= OnSettingChanged;
        _minute.Stop();
    }

    private void OnShown()
    {
        WeatherService.Current?.RefreshIfStale();
        CalendarService.Current?.EnsureCurrentDay();
        RenderAll();
        _minute.Start();
    }

    private void OnCalendarChanged() => RenderAgenda();

    private void OnSettingChanged(string name)
    {
        if (name.StartsWith("Weather", StringComparison.Ordinal) || name.StartsWith("Calendar", StringComparison.Ordinal))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RenderAll));
        }
    }

    private void RenderAll()
    {
        RenderWeather();
        RenderAgenda();
    }

    // ---- Weather -----------------------------------------------------------------------------------

    private void RenderWeather()
    {
        WeatherHost.Children.Clear();
        WeatherService? weather = WeatherService.Current;

        if (weather == null || !weather.Enabled)
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(Text("Weather is off", 13, "TextBrush", FontWeights.SemiBold, centered: true));
            stack.Children.Add(Text("Sends your approximate location to open-meteo.com", 11, "TextSecondaryBrush", centered: true, wrap: true));
            var on = new Button { Content = "Turn on", Style = (Style)FindResource("AccentButton"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            on.Click += (_, _) => _settings.Current.WeatherEnabled = true;
            stack.Children.Add(on);
            WeatherHost.Children.Add(stack);
            return;
        }

        WeatherData? data = weather.Data;
        if (data == null)
        {
            string message = weather.Loading ? "Loading weather..." : weather.Problem ?? (weather.Offline ? "Weather is offline" : "No weather yet");
            WeatherHost.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = (Style)FindResource("TextSecondary"),
            });
            return;
        }

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Now: icon, big temperature, condition / high-low / place.
        var now = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        now.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        now.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        now.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new WeatherIcon(36) { Kind = WeatherCodes.IconFor(data.Code, data.IsDay), VerticalAlignment = VerticalAlignment.Center };
        now.Children.Add(icon);
        TextBlock temp = Text(weather.FormatTemp(data.TempC), 30, "TextBrush", FontWeights.SemiBold);
        temp.Margin = new Thickness(8, 0, 10, 0);
        temp.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(temp, 1);
        now.Children.Add(temp);

        WeatherDay? today = data.Days.FirstOrDefault(d => d.Date.Date == DateTime.Today) ?? data.Days.FirstOrDefault();
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Text(WeatherCodes.LabelFor(data.Code), 12, "TextBrush", FontWeights.SemiBold, trim: true));
        if (today != null)
        {
            info.Children.Add(Text($"H {weather.FormatTemp(today.MaxC)}  L {weather.FormatTemp(today.MinC)}", 11, "TextSecondaryBrush"));
        }

        string place = weather.Offline ? $"{data.Place} - offline" : data.Place;
        info.Children.Add(Text(place, 10, "TextDisabledBrush", trim: true));
        Grid.SetColumn(info, 2);
        now.Children.Add(info);
        grid.Children.Add(now);

        // Next five days.
        var strip = new UniformGrid { Rows = 1, Columns = 5, VerticalAlignment = VerticalAlignment.Bottom };
        foreach (WeatherDay day in data.Days.Where(d => d.Date.Date > DateTime.Today).Take(5))
        {
            var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            column.Children.Add(Text(day.Date.ToString("ddd"), 10, "TextSecondaryBrush", centered: true));
            var small = new WeatherIcon(18) { Kind = WeatherCodes.IconFor(day.Code, true), Margin = new Thickness(0, 1, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            column.Children.Add(small);
            column.Children.Add(Text(weather.FormatTemp(day.MaxC), 11, "TextBrush", centered: true));
            column.Children.Add(Text(weather.FormatTemp(day.MinC), 10, "TextDisabledBrush", centered: true));
            strip.Children.Add(column);
        }

        Grid.SetRow(strip, 1);
        grid.Children.Add(strip);
        WeatherHost.Children.Add(grid);
    }

    // ---- Agenda ------------------------------------------------------------------------------------

    private void RenderAgenda(bool keepScroll = false)
    {
        double savedOffset = AgendaScroll.VerticalOffset;
        Agenda.Children.Clear();
        CalendarService? calendar = CalendarService.Current;
        if (calendar == null || !calendar.Enabled)
        {
            Agenda.Children.Add(Empty("Calendar is off", "Turn it on in Settings > Calendar & Weather"));
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        DateTime today = DateTime.Today;
        FrameworkElement? nowRow = null;
        IReadOnlyList<CalendarEvent> events = calendar.Events;
        var todays = events.Where(e => OnDay(e, today)).ToList();
        var tomorrows = events.Where(e => OnDay(e, today.AddDays(1))).ToList();

        if (todays.Count == 0 && tomorrows.Count == 0)
        {
            Agenda.Children.Add(EmptyAgenda(calendar));
            return;
        }

        Agenda.Children.Add(Header("Today", today));
        if (todays.Count == 0)
        {
            Agenda.Children.Add(Text("Nothing planned", 12, "TextDisabledBrush", margin: new Thickness(10, 2, 0, 6)));
        }

        bool nowPlaced = false;
        foreach (CalendarEvent e in todays)
        {
            if (!nowPlaced && !e.AllDay && e.End > now)
            {
                nowRow = NowMarker(now);
                Agenda.Children.Add(nowRow);
                nowPlaced = true;
            }

            Agenda.Children.Add(Row(e, now));
        }

        if (!nowPlaced && todays.Count > 0 && todays.Any(e => !e.AllDay))
        {
            nowRow = NowMarker(now);
            Agenda.Children.Add(nowRow);
        }

        Agenda.Children.Add(Header("Tomorrow", today.AddDays(1)));
        if (tomorrows.Count == 0)
        {
            Agenda.Children.Add(Text("Nothing planned", 12, "TextDisabledBrush", margin: new Thickness(10, 2, 0, 6)));
        }

        foreach (CalendarEvent e in tomorrows)
        {
            Agenda.Children.Add(Row(e, now));
        }

        string? footer = calendar.Windows == WindowsCalendarState.Unavailable
            ? "Windows calendars: available in the installed (MSIX) version"
            : null;
        if (calendar.Offline)
        {
            footer = (footer == null ? string.Empty : footer + ". ") + "A calendar feed is offline";
        }

        if (footer != null)
        {
            Agenda.Children.Add(Text(footer, 10, "TextDisabledBrush", margin: new Thickness(10, 6, 0, 2), wrap: true));
        }

        if (keepScroll)
        {
            // The once-a-minute redraw must not yank the list back while the user scrolls.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => AgendaScroll.ScrollToVerticalOffset(savedOffset)));
        }
        else if (nowRow != null)
        {
            FrameworkElement target = nowRow;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                try
                {
                    // Keep the "now" marker near the top: scroll to it only when it is not in view.
                    GeneralTransform t = target.TransformToAncestor(Agenda);
                    AgendaScroll.ScrollToVerticalOffset(Math.Max(0, t.Transform(new Point(0, 0)).Y - 22));
                }
                catch (InvalidOperationException)
                {
                    // Not laid out yet.
                }
            }));
        }
    }

    private static bool OnDay(CalendarEvent e, DateTime day)
    {
        DateTimeOffset start = new(day, TimeZoneInfo.Local.GetUtcOffset(day));
        DateTime next = day.AddDays(1);
        DateTimeOffset end = new(next, TimeZoneInfo.Local.GetUtcOffset(next));
        return e.Start < end && (e.End > start || e.Start >= start);
    }

    private FrameworkElement Header(string label, DateTime day)
    {
        var header = Text($"{label}  {day:ddd d MMM}", 11, "TextSecondaryBrush", FontWeights.SemiBold, margin: new Thickness(2, 2, 0, 4));
        return header;
    }

    private FrameworkElement Row(CalendarEvent e, DateTimeOffset now)
    {
        bool past = e.End <= now && !e.AllDay;
        bool current = !e.AllDay && e.Start <= now && e.End > now;

        var row = new Grid { Margin = new Thickness(0, 0, 0, 5), Opacity = past ? 0.45 : 1 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bar = new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(ColorOf(e.Source)), Margin = new Thickness(0, 1, 0, 1) };
        row.Children.Add(bar);

        string time = e.AllDay ? "All day" : e.Start.ToLocalTime().ToString("t");
        TextBlock timeText = Text(time, 12, current ? "AccentBrush" : "TextSecondaryBrush", current ? FontWeights.SemiBold : FontWeights.Normal);
        timeText.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(timeText, 1);
        row.Children.Add(timeText);

        var details = new StackPanel();
        details.Children.Add(Text(e.Title, 13, "TextBrush", FontWeights.SemiBold, trim: true));
        if (!string.IsNullOrWhiteSpace(e.Location))
        {
            details.Children.Add(Text(e.Location.Replace('\n', ' '), 11, "TextSecondaryBrush", trim: true));
        }

        Grid.SetColumn(details, 2);
        row.Children.Add(details);
        row.ToolTip = $"{e.Title}\n{(e.AllDay ? "All day" : e.Start.ToLocalTime().ToString("t") + " - " + e.End.ToLocalTime().ToString("t"))}"
                      + (string.IsNullOrWhiteSpace(e.Location) ? string.Empty : "\n" + e.Location);
        return row;
    }

    private FrameworkElement NowMarker(DateTimeOffset now)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 6), Height = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0) };
        dot.SetResourceReference(Shape.FillProperty, "AccentBrush");
        grid.Children.Add(dot);

        var line = new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Center };
        line.SetResourceReference(Shape.FillProperty, "AccentBrush");
        var label = Text("Now " + now.ToLocalTime().ToString("t"), 10, "AccentBrush", FontWeights.SemiBold);
        label.Margin = new Thickness(0, 0, 8, 0);
        var lineHost = new DockPanel();
        DockPanel.SetDock(label, Dock.Left);
        lineHost.Children.Add(label);
        lineHost.Children.Add(line);
        Grid.SetColumn(lineHost, 1);
        grid.Children.Add(lineHost);
        return grid;
    }

    private FrameworkElement EmptyAgenda(CalendarService calendar)
    {
        string hint = calendar.IcsUrls.Count == 0
            ? "Add a calendar link (ICS) in Settings > Calendar & Weather"
            : calendar.Offline ? "The calendar feed could not be reached" : "Enjoy the quiet";
        if (calendar.Windows == WindowsCalendarState.Unavailable)
        {
            hint += "\nWindows calendars: available in the installed (MSIX) version";
        }

        return Empty("No events today or tomorrow", hint);
    }

    private FrameworkElement Empty(string title, string hint)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        stack.Children.Add(Text(title, 13, "TextBrush", FontWeights.SemiBold, centered: true));
        stack.Children.Add(Text(hint, 11, "TextSecondaryBrush", centered: true, wrap: true, margin: new Thickness(0, 3, 0, 0)));
        return stack;
    }

    private static Color ColorOf(int source) =>
        source <= 0 ? (ThemeService.Current?.Accent ?? ThemeService.DefaultAccent) : SourceColors[1 + (source - 1) % (SourceColors.Length - 1)];

    private TextBlock Text(string text, double size, string brushKey, FontWeight? weight = null, bool centered = false, bool trim = false,
        bool wrap = false, Thickness? margin = null)
    {
        TextBlock block = HudParts.Label(text, size, brushKey, weight);
        block.TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        if (wrap)
        {
            block.TextWrapping = TextWrapping.Wrap;
        }

        if (centered)
        {
            block.TextAlignment = TextAlignment.Center;
            block.HorizontalAlignment = HorizontalAlignment.Center;
        }

        if (margin is { } m)
        {
            block.Margin = m;
        }

        return block;
    }
}
