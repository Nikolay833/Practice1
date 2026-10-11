using System;
using System.Collections.Generic;
using System.Globalization;
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

namespace Dropshelf.Activities.Calendar;

/// <summary>
/// The "Today" tab: two minimal widgets in the style of iOS StandBy. Left, the weather: "Now" with a big icon and the
/// temperature, then the place and an hourly forecast (time, icon, chance of rain, temperature). Right, the week: the month,
/// the weekdays, this week's dates with today in a white circle, and the next event underneath. Redrawn when the weather or
/// the calendar changes, and every 30 seconds while the tab is visible (that timer stops when it is hidden).
/// </summary>
public partial class TodayView : UserControl
{
    private static readonly Brush RainBrush = Frozen(Color.FromRgb(0x64, 0xD2, 0xFF)); // systemCyan

    private readonly SettingsService _settings;
    private readonly DispatcherTimer _minute;

    internal TodayView(SettingsService settings)
    {
        InitializeComponent();
        _settings = settings;

        _minute = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _minute.Tick += (_, _) => RenderCalendar();

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
            calendar.Changed += RenderCalendar;
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
            calendar.Changed -= RenderCalendar;
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
        RenderCalendar();
    }

    // ---- Weather -----------------------------------------------------------------------------------

    private void RenderWeather()
    {
        WeatherHost.Children.Clear();
        WeatherService? weather = WeatherService.Current;

        if (weather == null || !weather.Enabled)
        {
            var off = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            off.Children.Add(Text("Weather", 17, "TextBrush", FontWeights.SemiBold));
            off.Children.Add(Text("Sends your approximate location to open-meteo.com", 11, "TextSecondaryBrush", wrap: true, margin: new Thickness(0, 2, 0, 0)));
            var on = new Button { Content = "Turn On", Style = (Style)FindResource("TextButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 4, 0, 0) };
            on.Click += (_, _) => _settings.Current.WeatherEnabled = true;
            off.Children.Add(on);
            WeatherHost.Children.Add(off);
            return;
        }

        WeatherData? data = weather.Data;
        if (data == null)
        {
            string message = weather.Loading ? "Loading weather..." : weather.Problem ?? (weather.Offline ? "Weather is offline" : "No weather yet");
            WeatherHost.Children.Add(Text(message, 12, "TextSecondaryBrush", wrap: true));
            ((FrameworkElement)WeatherHost.Children[0]).VerticalAlignment = VerticalAlignment.Center;
            return;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Now: label, big icon, big temperature.
        var now = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 70 };
        now.Children.Add(Text("Now", 15, "TextBrush", FontWeights.SemiBold, centered: true));
        now.Children.Add(new WeatherIcon(40) { Kind = WeatherCodes.IconFor(data.Code, data.IsDay), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 6) });
        TextBlock temp = Text(weather.FormatTemp(data.TempC), 30, "TextBrush", FontWeights.Light, centered: true);
        temp.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        now.Children.Add(temp);
        grid.Children.Add(now);

        var divider = new Border { Width = 1, Margin = new Thickness(14, 8, 16, 8) };
        divider.SetResourceReference(Border.BackgroundProperty, "StrokeBrush");
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);

        // Place, "hourly forecast", then one column per hour.
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        TextBlock place = Text(string.IsNullOrEmpty(data.Place) ? "Weather" : data.Place, 20, "TextBrush", FontWeights.SemiBold, trim: true);
        place.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        right.Children.Add(place);

        var caption = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 8) };
        caption.Children.Add(HudParts.Glyph("", "TextSecondaryBrush", 10));
        caption.Children.Add(Text(weather.Offline ? "HOURLY FORECAST · OFFLINE" : "HOURLY FORECAST", 10, "TextSecondaryBrush", FontWeights.SemiBold, margin: new Thickness(5, 0, 0, 0)));
        right.Children.Add(caption);

        var hours = new UniformGrid { Rows = 1, Columns = 5 };
        List<WeatherHour> upcoming = data.UpcomingHours(5).ToList();
        if (upcoming.Count > 0)
        {
            foreach (WeatherHour h in upcoming)
            {
                hours.Children.Add(HourColumn(HourLabel(h.Time), WeatherCodes.IconFor(h.Code, h.IsDay), h.PrecipitationChance, weather.FormatTemp(h.TempC)));
            }
        }
        else
        {
            // An older cached forecast has no hours yet: show the next days until the next refresh.
            foreach (WeatherDay d in data.Days.Where(d => d.Date.Date >= DateTime.Today).Take(5))
            {
                hours.Children.Add(HourColumn(d.Date.ToString("ddd", CultureInfo.CurrentCulture).ToUpperInvariant(), WeatherCodes.IconFor(d.Code, true), 0, weather.FormatTemp(d.MaxC)));
            }
        }

        right.Children.Add(hours);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        WeatherHost.Children.Add(grid);
    }

    private FrameworkElement HourColumn(string label, WeatherIconKind icon, int rain, string temp)
    {
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        column.Children.Add(Text(label, 10, "TextSecondaryBrush", FontWeights.SemiBold, centered: true));
        column.Children.Add(new WeatherIcon(18) { Kind = icon, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
        TextBlock chance = Text(rain >= 20 ? $"{rain}%" : " ", 9, "TextSecondaryBrush", FontWeights.SemiBold, centered: true);
        chance.Foreground = RainBrush;
        column.Children.Add(chance);
        column.Children.Add(Text(temp, 14, "TextBrush", FontWeights.Medium, centered: true));
        return column;
    }

    /// <summary>"3PM" in 12-hour cultures, "15" in 24-hour ones.</summary>
    private static string HourLabel(DateTime time)
    {
        bool twelveHour = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('t');
        return twelveHour ? time.ToString("h tt", CultureInfo.CurrentCulture).Replace(" ", string.Empty).ToUpperInvariant() : time.ToString("HH", CultureInfo.CurrentCulture);
    }

    // ---- Calendar ----------------------------------------------------------------------------------

    private void RenderCalendar()
    {
        CalendarHost.Children.Clear();
        DateTime today = DateTime.Today;
        CultureInfo culture = CultureInfo.CurrentCulture;
        DayOfWeek first = culture.DateTimeFormat.FirstDayOfWeek;
        DateTime weekStart = today.AddDays(-(((int)today.DayOfWeek - (int)first + 7) % 7));

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Text(culture.DateTimeFormat.GetMonthName(today.Month), 13, "TextSecondaryBrush", FontWeights.SemiBold,
            margin: new Thickness(4, 0, 0, 8)));

        var names = new UniformGrid { Rows = 1, Columns = 7 };
        var dates = new UniformGrid { Rows = 1, Columns = 7, Margin = new Thickness(0, 6, 0, 0) };
        for (int i = 0; i < 7; i++)
        {
            DateTime day = weekStart.AddDays(i);
            string name = culture.DateTimeFormat.GetAbbreviatedDayName(day.DayOfWeek);
            names.Children.Add(Text(name.Length > 2 ? name[..2] : name, 11, "TextBrush", FontWeights.Bold, centered: true));

            bool isToday = day == today;
            bool otherMonth = day.Month != today.Month;
            TextBlock number = Text(day.Day.ToString(culture), 13, otherMonth ? "TextDisabledBrush" : "TextBrush", FontWeights.Bold, centered: true);
            number.VerticalAlignment = VerticalAlignment.Center;
            if (isToday)
            {
                number.Foreground = Brushes.Black;
                dates.Children.Add(new Grid
                {
                    Width = 28,
                    Height = 28,
                    Children = { new Ellipse { Fill = Brushes.White }, number },
                });
            }
            else
            {
                dates.Children.Add(new Grid { Height = 28, Children = { number } });
            }
        }

        stack.Children.Add(names);
        stack.Children.Add(dates);
        stack.Children.Add(NextEvent());
        CalendarHost.Children.Add(stack);
    }

    /// <summary>The event in progress or the next one today / tomorrow, as one line under the week; or a hint.</summary>
    private FrameworkElement NextEvent()
    {
        var margin = new Thickness(4, 12, 0, 0);
        CalendarService? calendar = CalendarService.Current;
        if (calendar == null || !calendar.Enabled)
        {
            return Text("Calendar is off", 11, "TextDisabledBrush", margin: margin);
        }

        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset until = new DateTimeOffset(DateTime.Today.AddDays(2));
        CalendarEvent? next = calendar.Events
            .Where(e => e.End > now && e.Start < until)
            .OrderBy(e => e.AllDay ? 1 : 0).ThenBy(e => e.Start)
            .FirstOrDefault();
        if (next == null)
        {
            string hint = calendar.IcsUrls.Count == 0 && calendar.Windows != WindowsCalendarState.Available
                ? "Add a calendar in Settings"
                : "No more events today";
            return Text(hint, 11, "TextDisabledBrush", margin: margin);
        }

        bool live = !next.AllDay && next.Start <= now;
        string when = next.AllDay ? "All day"
            : live ? "Now"
            : next.Start.LocalDateTime.Date == DateTime.Today ? next.Start.LocalDateTime.ToString("t", CultureInfo.CurrentCulture)
            : "Tomorrow " + next.Start.LocalDateTime.ToString("t", CultureInfo.CurrentCulture);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = margin };
        var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, live ? "DangerBrush" : "AccentBrush");
        row.Children.Add(dot);
        row.Children.Add(Text(when, 11, live ? "DangerBrush" : "TextSecondaryBrush", FontWeights.SemiBold, margin: new Thickness(0, 0, 6, 0)));
        TextBlock title = Text(next.Title, 11, "TextBrush", FontWeights.SemiBold, trim: true);
        title.MaxWidth = 150;
        row.Children.Add(title);
        row.ToolTip = next.Title + (string.IsNullOrWhiteSpace(next.Location) ? string.Empty : "\n" + next.Location);
        return row;
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static TextBlock Text(string text, double size, string brushKey, FontWeight? weight = null, bool centered = false, bool trim = false,
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
