using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using Dropshelf.Services;
using Microsoft.Win32;
using Windows.Devices.Geolocation;

namespace Dropshelf.Activities.Weather;

/// <summary>
/// Weather from Open-Meteo (no key). The location comes from the Windows location service when allowed, otherwise from the
/// "City" setting (geocoded with Open-Meteo too). Refreshed every 30 minutes and after resume, cached on disk (encrypted: it
/// holds a location). Off by default; while it is off nothing is requested and no timer runs. Network failures only flip
/// <see cref="Offline"/>: the last forecast stays on screen.
/// </summary>
internal sealed class WeatherService : IDisposable
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(30);

    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _timer;
    private bool _refreshing;
    private bool _disposed;

    public WeatherService(SettingsService settings)
    {
        _settings = settings;
        Current = this;
        Data = JsonFile.LoadProtected<WeatherData>(AppPaths.WeatherFile, out _);
        if (Data is { Days.Count: 0 })
        {
            Data = null;
        }

        _timer = new DispatcherTimer { Interval = RefreshEvery };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        ApplySettings();
    }

    public static WeatherService? Current { get; private set; }

    public bool Enabled => _settings.Current.WeatherEnabled;
    public WeatherData? Data { get; private set; }

    /// <summary>The last attempt to reach the network failed (the data shown may be old).</summary>
    public bool Offline { get; private set; }

    public bool Loading { get; private set; }

    /// <summary>Why there is no forecast ("no location"...), or null.</summary>
    public string? Problem { get; private set; }

    public event Action? Changed;

    public bool Fahrenheit => _settings.Current.WeatherFahrenheit;

    public string UnitSymbol => Fahrenheit ? "°F" : "°C";

    /// <summary>"21°" in the chosen unit.</summary>
    public string FormatTemp(double celsius)
    {
        double value = Math.Round(Fahrenheit ? celsius * 9 / 5 + 32 : celsius);
        if (value == 0)
        {
            value = 0; // -0.4 rounds to -0, which would print as "-0"
        }

        return $"{value:0}\u00B0";
    }

    /// <summary>Starts or stops the refresh timer to match the settings (call when a weather setting changes).</summary>
    public void ApplySettings()
    {
        if (Enabled)
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }

            if (Data == null || DateTime.UtcNow - Data.FetchedUtc > RefreshEvery)
            {
                _ = RefreshAsync();
            }
        }
        else
        {
            _timer.Stop();
        }

        Changed?.Invoke();
    }

    /// <summary>A location setting changed: forget what we know about the place and ask again.</summary>
    public void LocationSettingsChanged()
    {
        Problem = null;
        if (Enabled)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>Refreshes when the data is older than the refresh interval (the Today tab calls this when it opens).</summary>
    public void RefreshIfStale()
    {
        if (Enabled && !_refreshing && (Data == null || DateTime.UtcNow - Data.FetchedUtc > RefreshEvery))
        {
            _ = RefreshAsync();
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
            Loading = Data == null;
            Changed?.Invoke();
            (double lat, double lon, string name, string cityKey)? place = await ResolveLocationAsync();
            if (place == null)
            {
                Problem = _settings.Current.WeatherCity.Trim().Length == 0
                    ? "Allow location access in Windows, or set a city in Settings"
                    : "City not found";
                return;
            }

            string json = await Net.Client.GetStringAsync(ForecastUrl(place.Value.lat, place.Value.lon));
            WeatherData? data = WeatherData.Parse(json, place.Value.name, place.Value.cityKey, place.Value.lat, place.Value.lon);
            if (data == null)
            {
                Offline = true;
                Log.Error("Weather: unexpected forecast response");
                return;
            }

            Data = data;
            Offline = false;
            Problem = null;
            JsonFile.SaveProtected(AppPaths.WeatherFile, data);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Offline = true;
            Log.Info($"Weather: could not refresh ({ex.GetType().Name}: {ex.Message})"); // usually just offline: one line, no stack
        }
        catch (Exception ex)
        {
            Offline = true;
            Log.Error("Weather: refresh failed", ex);
        }
        finally
        {
            _refreshing = false;
            Loading = false;
            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("Weather: change handler failed", ex);
            }
        }
    }

    // ---- Location ----------------------------------------------------------------------------------

    private async Task<(double lat, double lon, string name, string cityKey)?> ResolveLocationAsync()
    {
        if (_settings.Current.WeatherUseLocation)
        {
            (double lat, double lon)? device = await TryDeviceLocationAsync();
            if (device != null)
            {
                return (device.Value.lat, device.Value.lon, "Current location", string.Empty);
            }
        }

        string city = _settings.Current.WeatherCity.Trim();
        if (city.Length == 0)
        {
            return null;
        }

        if (Data != null && Data.CityKey.Equals(city, StringComparison.OrdinalIgnoreCase))
        {
            return (Data.Latitude, Data.Longitude, Data.Place, Data.CityKey); // geocoded before
        }

        string url = "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(city) + "&count=1";
        string json = await Net.Client.GetStringAsync(url);
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out JsonElement results) || results.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement hit = results[0];
        string name = hit.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? city : city;
        if (hit.TryGetProperty("country", out JsonElement country) && country.GetString() is { Length: > 0 } c)
        {
            name += ", " + c;
        }

        return (hit.GetProperty("latitude").GetDouble(), hit.GetProperty("longitude").GetDouble(), name, city);
    }

    /// <summary>The Windows location service. Null when access is denied, location is off or it takes too long.</summary>
    private static async Task<(double lat, double lon)?> TryDeviceLocationAsync()
    {
        try
        {
            GeolocationAccessStatus access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                return null;
            }

            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            Task<Geoposition> request = locator.GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(10)).AsTask();
            if (await Task.WhenAny(request, Task.Delay(Net.Timeout + TimeSpan.FromSeconds(3))) != request)
            {
                return null;
            }

            BasicGeoposition p = (await request).Coordinate.Point.Position;
            return (p.Latitude, p.Longitude);
        }
        catch (Exception ex)
        {
            Log.Error("Weather: device location unavailable", ex);
            return null;
        }
    }

    // ---- Forecast ----------------------------------------------------------------------------------

    public static string ForecastUrl(double lat, double lon) =>
        "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString("0.####", CultureInfo.InvariantCulture)
        + "&longitude=" + lon.ToString("0.####", CultureInfo.InvariantCulture)
        + "&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min,weather_code"
        + "&hourly=temperature_2m,weather_code,precipitation_probability,is_day&forecast_hours=30"
        + "&timezone=auto&forecast_days=6";

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && !_disposed)
        {
            // The network takes a few seconds to come back after sleep.
            _dispatcher.BeginInvoke(new Action(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                if (Enabled && !_disposed)
                {
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
    }
}
