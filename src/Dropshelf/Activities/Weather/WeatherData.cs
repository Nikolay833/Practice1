using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Dropshelf.Activities.Weather;

/// <summary>One day of the forecast (temperatures in Celsius, as Open-Meteo reports them).</summary>
internal sealed class WeatherDay
{
    public DateTime Date { get; set; }
    public double MaxC { get; set; }
    public double MinC { get; set; }
    public int Code { get; set; }
}

/// <summary>The last forecast (also the on-disk cache).</summary>
internal sealed class WeatherData
{
    public DateTime FetchedUtc { get; set; }
    public string Place { get; set; } = string.Empty;
    public string CityKey { get; set; } = string.Empty;   // the City setting this location was geocoded from ("" = device location)
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double TempC { get; set; }
    public int Code { get; set; }
    public bool IsDay { get; set; } = true;
    public List<WeatherDay> Days { get; set; } = new();

    /// <summary>Reads Open-Meteo's forecast JSON. Null when the shape is not what we expect.</summary>
    public static WeatherData? Parse(string json, string place, string cityKey, double lat, double lon)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("current", out JsonElement current) || !root.TryGetProperty("daily", out JsonElement daily))
        {
            return null;
        }

        var data = new WeatherData
        {
            FetchedUtc = DateTime.UtcNow,
            Place = place,
            CityKey = cityKey,
            Latitude = lat,
            Longitude = lon,
            TempC = current.GetProperty("temperature_2m").GetDouble(),
            Code = current.GetProperty("weather_code").GetInt32(),
            IsDay = !current.TryGetProperty("is_day", out JsonElement isDay) || isDay.GetInt32() != 0,
        };

        JsonElement times = daily.GetProperty("time");
        JsonElement max = daily.GetProperty("temperature_2m_max");
        JsonElement min = daily.GetProperty("temperature_2m_min");
        JsonElement codes = daily.GetProperty("weather_code");
        for (int i = 0; i < times.GetArrayLength(); i++)
        {
            if (!DateTime.TryParseExact(times[i].GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
                || max[i].ValueKind != JsonValueKind.Number || min[i].ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            data.Days.Add(new WeatherDay
            {
                Date = date,
                MaxC = max[i].GetDouble(),
                MinC = min[i].GetDouble(),
                Code = codes[i].ValueKind == JsonValueKind.Number ? codes[i].GetInt32() : 3,
            });
        }

        return data.Days.Count == 0 ? null : data;
    }
}
