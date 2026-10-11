namespace Dropshelf.Activities.Weather;

/// <summary>Which picture stands for a kind of weather.</summary>
internal enum WeatherIconKind
{
    Sun,
    Moon,
    PartlyDay,
    PartlyNight,
    Cloud,
    Fog,
    Drizzle,
    Rain,
    Snow,
    Thunder,
}

/// <summary>Open-Meteo reports WMO weather interpretation codes (WW); this maps them to an icon and a label.</summary>
internal static class WeatherCodes
{
    public static WeatherIconKind IconFor(int code, bool isDay) => code switch
    {
        0 => isDay ? WeatherIconKind.Sun : WeatherIconKind.Moon,
        1 or 2 => isDay ? WeatherIconKind.PartlyDay : WeatherIconKind.PartlyNight,
        3 => WeatherIconKind.Cloud,
        45 or 48 => WeatherIconKind.Fog,
        51 or 53 or 55 or 56 or 57 => WeatherIconKind.Drizzle,
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => WeatherIconKind.Rain,
        71 or 73 or 75 or 77 or 85 or 86 => WeatherIconKind.Snow,
        95 or 96 or 99 => WeatherIconKind.Thunder,
        _ => WeatherIconKind.Cloud,
    };

    public static string LabelFor(int code) => code switch
    {
        0 => "Clear sky",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 => "Fog",
        48 => "Freezing fog",
        51 => "Light drizzle",
        53 => "Drizzle",
        55 => "Heavy drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 => "Light showers",
        81 => "Showers",
        82 => "Heavy showers",
        85 => "Snow showers",
        86 => "Heavy snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm, hail",
        _ => "Unknown",
    };
}
