using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SirThaddeus.WebSearch;

namespace SirThaddeus.McpServer.Tools;

// ─────────────────────────────────────────────────────────────────────────
// Weather Tools
//
// Coordinate-first weather stack for deterministic, short weather answers:
//   1) weather_geocode(place) -> place candidates with lat/lon
//   2) weather_forecast(lat, lon) -> normalized weather payload
//
// Provider strategy:
//   - Geocoding: Photon primary, Open-Meteo fallback
//   - US locations: NWS primary
//   - Non-US or NWS failure: Open-Meteo fallback
//
// Bounded + safe:
//   - geocode results capped at 5
//   - forecast days capped at 7
//   - cache TTL configurable (forecast 10..30 min, geocode default 24h)
// ─────────────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class WeatherTools
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    private static readonly Lazy<WeatherService> Service = new(CreateService);

    [McpServerTool, Description(
        "Geocodes a human location string to coordinates. " +
        "Returns normalized place candidates with lat/lon, country code, " +
        "US flag, confidence, and cache metadata.")]
    public static async Task<string> WeatherGeocode(
        [Description("Human location string, e.g. 'Rexburg, ID'")] string place,
        [Description("Max candidates to return (1-5, default 3)")] int maxResults = 3,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(place))
            return Json(new { error = "Place is required.", query = place, results = Array.Empty<object>() });

        maxResults = Math.Clamp(maxResults, 1, 5);

        try
        {
            var lookup = await Service.Value.GeocodeAsync(place, maxResults, cancellationToken);

            return Json(new
            {
                query = lookup.Query,
                source = lookup.Source,
                cache = new
                {
                    hit = lookup.Cache.Hit,
                    ageSeconds = lookup.Cache.AgeSeconds
                },
                results = lookup.Results.Select(r => new
                {
                    name = r.Name,
                    region = r.Region,
                    countryCode = r.CountryCode,
                    isUs = r.IsUs,
                    latitude = r.Latitude,
                    longitude = r.Longitude,
                    confidence = r.Confidence
                }).ToArray()
            });
        }
        catch (OperationCanceledException)
        {
            return Json(new { error = "Geocoding cancelled.", query = place, results = Array.Empty<object>() });
        }
        catch (Exception ex)
        {
            return Json(new
            {
                error = $"Geocoding failed: {ex.Message}",
                query = place,
                results = Array.Empty<object>()
            });
        }
    }

    [McpServerTool, Description(
        "Gets weather forecast for coordinates. Uses NWS for US and " +
        "Open-Meteo fallback otherwise. Returns normalized current + daily data, " +
        "provider details, alerts, and cache metadata.")]
    public static async Task<string> WeatherForecast(
        [Description("Latitude in decimal degrees")] double latitude,
        [Description("Longitude in decimal degrees")] double longitude,
        [Description("Optional place label for response context")] string? placeHint = null,
        [Description("Optional country code from geocoder, e.g. 'US'")] string? countryCode = null,
        [Description("Days of daily forecast (1-7, default 7)")] int days = 7,
        CancellationToken cancellationToken = default)
    {
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            return Json(new
            {
                error = "Coordinates out of range.",
                latitude,
                longitude
            });
        }

        days = Math.Clamp(days, 1, 7);

        try
        {
            var forecast = await Service.Value.ForecastAsync(
                latitude, longitude, placeHint, countryCode, days, cancellationToken);

            return Json(new
            {
                provider = forecast.Provider,
                providerReason = forecast.ProviderReason,
                cache = new
                {
                    hit = forecast.Cache.Hit,
                    ageSeconds = forecast.Cache.AgeSeconds
                },
                location = new
                {
                    name = forecast.Location.Name,
                    countryCode = forecast.Location.CountryCode,
                    isUs = forecast.Location.IsUs,
                    latitude = forecast.Location.Latitude,
                    longitude = forecast.Location.Longitude
                },
                current = forecast.Current is null
                    ? null
                    : new
                    {
                        temperature = forecast.Current.Temperature,
                        unit = forecast.Current.Unit,
                        condition = forecast.Current.Condition,
                        wind = forecast.Current.Wind,
                        humidityPercent = forecast.Current.HumidityPercent,
                        observedAt = forecast.Current.ObservedAt?.ToString("o")
                    },
                daily = forecast.Daily
                    .Take(days)
                    .Select(d => new
                    {
                        date = d.Date.ToString("yyyy-MM-dd"),
                        tempHigh = d.TempHigh,
                        tempLow = d.TempLow,
                        avgTemp = d.AvgTemp,
                        unit = d.Unit,
                        condition = d.Condition
                    })
                    .ToArray(),
                alerts = forecast.Alerts
                    .Take(5)
                    .Select(a => new
                    {
                        headline = a.Headline,
                        severity = a.Severity,
                        @event = a.Event
                    })
                    .ToArray()
            });
        }
        catch (OperationCanceledException)
        {
            return Json(new
            {
                error = "Weather forecast cancelled.",
                latitude,
                longitude
            });
        }
        catch (Exception ex)
        {
            return Json(new
            {
                error = $"Weather forecast failed: {ex.Message}",
                latitude,
                longitude
            });
        }
    }

    private static WeatherService CreateService()
    {
        var providerMode = Environment.GetEnvironmentVariable("ST_WEATHER_PROVIDER_MODE")
                           ?? "nws_us_openmeteo_fallback";

        var forecastCacheMinutes = ParseIntEnv(
            "ST_WEATHER_FORECAST_CACHE_MINUTES", fallback: 15, min: 10, max: 30);
        var geocodeCacheMinutes = ParseIntEnv(
            "ST_WEATHER_GEOCODE_CACHE_MINUTES", fallback: 1_440, min: 60, max: 10_080);

        var placeMemoryEnabled = ParseBoolEnv("ST_WEATHER_PLACE_MEMORY_ENABLED", fallback: false);
        var placeMemoryPath = ResolvePlaceMemoryPath(
            Environment.GetEnvironmentVariable("ST_WEATHER_PLACE_MEMORY_PATH"));
        var userAgent = Environment.GetEnvironmentVariable("ST_WEATHER_USER_AGENT");

        var options = new WeatherServiceOptions
        {
            ProviderMode = providerMode,
            ForecastCacheMinutes = forecastCacheMinutes,
            GeocodeCacheMinutes = geocodeCacheMinutes,
            PlaceMemoryEnabled = placeMemoryEnabled,
            PlaceMemoryPath = placeMemoryPath,
            UserAgent = string.IsNullOrWhiteSpace(userAgent)
                ? "SirThaddeusCopilot/1.0 (contact: local-runtime@localhost)"
                : userAgent.Trim()
        };

        return new WeatherService(options);
    }

    private static int ParseIntEnv(string key, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (!int.TryParse(raw, out var parsed))
            return fallback;
        return Math.Clamp(parsed, min, max);
    }

    private static bool ParseBoolEnv(string key, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return raw?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };
    }

    private static string ResolvePlaceMemoryPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "SirThaddeus", "weather-places.json");
        }

        return raw;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOpts);
}
