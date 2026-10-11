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

/// <summary>One hour of the forecast, in the location's local time (Celsius).</summary>
internal sealed class WeatherHour
{
    public DateTime Time { get; set; }
    public double TempC { get; set; }
    public int Code { get; set; }
    public int PrecipitationChance { get; set; }
    public bool IsDay { get; set; } = true;
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
    public List<WeatherHour> Hours { get; set; } = new();

    /// <summary>The location's local time when the forecast was fetched (Open-Meteo's <c>current.time</c>).</summary>
    public DateTime CurrentLocal { get; set; }

    /// <summary>The next <paramref name="count"/> hours from now (the current hour first), in the location's local time.</summary>
    public IEnumerable<WeatherHour> UpcomingHours(int count)
    {
        DateTime now = CurrentLocal == default ? DateTime.Now : CurrentLocal + (DateTime.UtcNow - FetchedUtc);
        DateTime hour = new(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        int taken = 0;
        foreach (WeatherHour h in Hours)
        {
            if (h.Time >= hour && taken++ < count)
            {
                yield return h;
            }
        }
    }

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

        if (current.TryGetProperty("time", out JsonElement currentTime)
            && DateTime.TryParseExact(currentTime.GetString(), "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime localNow))
        {
            data.CurrentLocal = localNow;
        }

        if (root.TryGetProperty("hourly", out JsonElement hourly) && hourly.TryGetProperty("time", out JsonElement hourTimes))
        {
            hourly.TryGetProperty("temperature_2m", out JsonElement hourTemps);
            hourly.TryGetProperty("weather_code", out JsonElement hourCodes);
            hourly.TryGetProperty("precipitation_probability", out JsonElement hourRain);
            hourly.TryGetProperty("is_day", out JsonElement hourDay);
            for (int i = 0; i < hourTimes.GetArrayLength(); i++)
            {
                if (!DateTime.TryParseExact(hourTimes[i].GetString(), "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time)
                    || Number(hourTemps, i) is not { } temp)
                {
                    continue;
                }

                data.Hours.Add(new WeatherHour
                {
                    Time = time,
                    TempC = temp,
                    Code = (int)(Number(hourCodes, i) ?? 3),
                    PrecipitationChance = (int)(Number(hourRain, i) ?? 0),
                    IsDay = (Number(hourDay, i) ?? 1) != 0,
                });
            }
        }

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

    /// <summary>The number at <paramref name="index"/> of a JSON array, or null when missing or not a number.</summary>
    private static double? Number(JsonElement array, int index) =>
        array.ValueKind == JsonValueKind.Array && index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.Number
            ? array[index].GetDouble()
            : null;
}
