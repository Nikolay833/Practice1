using System;
using System.Windows;
using System.Windows.Controls;
using Dropshelf.Activities.Hud;
using Dropshelf.Services;

namespace Dropshelf.Activities.Weather;

/// <summary>
/// Optional (Settings, off by default): the current temperature with its icon on the right side of the collapsed pill while
/// nothing else wants it. The lowest priority of all pill activities. Because the pill is "in use" then, the item count badge
/// of the idle pill is not shown while this is on.
/// </summary>
internal sealed class WeatherPillActivity : PillActivityBase, IDisposable
{
    private readonly SettingsService _settings;
    private readonly WeatherService _weather;
    private readonly WeatherIcon _icon = new(18);
    private readonly TextBlock _temp;
    private readonly CompactContent _content;
    private bool _active;

    public WeatherPillActivity(SettingsService settings, WeatherService weather)
    {
        _settings = settings;
        _weather = weather;

        _temp = HudParts.Label("--°", 13, "TextBrush", FontWeights.SemiBold);
        _temp.Margin = new Thickness(6, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 24 };
        _icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_icon);
        row.Children.Add(_temp);
        row.Width = 62;
        _content = CompactContent.Of(null, row);

        _weather.Changed += Refresh;
        Refresh();
    }

    public override string Id => "weather";
    public override int Priority => 10;
    public override bool IsActive => _active;

    public override CompactContent? CreateCompact() => _content;

    public void ApplySettings() => Refresh();

    private void Refresh()
    {
        WeatherData? data = _weather.Data;
        bool active = _settings.Current.ShowWeatherOnPill && _weather.Enabled && data != null;
        if (active && data != null)
        {
            _icon.Kind = WeatherCodes.IconFor(data.Code, data.IsDay);
            _temp.Text = _weather.FormatTemp(data.TempC);
        }

        if (active != _active)
        {
            _active = active;
            RaiseStateChanged();
        }
    }

    public void Dispose() => _weather.Changed -= Refresh;
}
