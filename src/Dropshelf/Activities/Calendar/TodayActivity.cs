using System.Windows;
using Dropshelf.Activities.Weather;
using Dropshelf.Services;

namespace Dropshelf.Activities.Calendar;

/// <summary>
/// The "Today" tab: the weather card on the left, today's and tomorrow's agenda on the right. Available while the calendar or the
/// weather is switched on. (The pill parts are <see cref="MeetingActivity"/> and <see cref="WeatherPillActivity"/>.)
/// </summary>
internal sealed class TodayActivity : PillActivityBase
{
    private readonly SettingsService _settings;

    public TodayActivity(SettingsService settings)
    {
        _settings = settings;
    }

    public override string Id => "today";
    public override int Priority => 90;
    public override string? TabTitle => "Today";
    public override int TabOrder => -90;
    public override bool HasExpanded => _settings.Current.CalendarEnabled || _settings.Current.WeatherEnabled;

    public override FrameworkElement? CreateExpanded() => new TodayView(_settings);

    /// <summary>A calendar / weather switch changed: the tab appears or goes away.</summary>
    public void ApplySettings() => RaiseStateChanged();
}
