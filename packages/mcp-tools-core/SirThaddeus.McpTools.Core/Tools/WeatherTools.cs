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

/// <summary>
/// MCP tools for geocoding and weather forecasts using a coordinate-first
/// NWS-primary, Open-Meteo-fallback weather stack.
/// </summary>
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
        "US flag, confidence, and cache metadata. " +
        "Pass the location string as either `location` or `place`.")]
    public static async Task<string> WeatherGeocode(
        // Two aliases for the same input — small models routinely guess
        // `location` from the parameter description ("Human location
        // string"), while older callers already pass `place`. Accepting
        // both removes an entire class of silent argument-binding
        // failures. Whichever is non-empty wins; `location` is preferred
        // when both are supplied.
        [Description("Human location string, e.g. 'Portland, OR'. Pass EITHER this OR `place` — they are equivalent.")]
        string? location = null,
        [Description("Alias for `location`. Accepted for backward compat.")]
        string? place = null,
        [Description("Max candidates to return (1-5, default 3)")] int maxResults = 3,
        CancellationToken cancellationToken = default)
    {
        var input = !string.IsNullOrWhiteSpace(location) ? location : place;
        if (string.IsNullOrWhiteSpace(input))
            return Json(new { error = "A location is required. Pass it as `location` or `place`.", query = input ?? string.Empty, results = Array.Empty<object>() });

        maxResults = Math.Clamp(maxResults, 1, 5);

        try
        {
            var args = new { place = input, maxResults };
            var cached = await ToolResultCache.GetAsync<string>("weather_geocode", args);
            if (!string.IsNullOrWhiteSpace(cached))
                return cached;

            var lookup = await Service.Value.GeocodeAsync(input, maxResults, cancellationToken);

            var response = Json(new
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

            await ToolResultCache.SetAsync("weather_geocode", args, response, ToolResultCache.ResolveWeatherTtl());
            return response;
        }
        catch (OperationCanceledException)
        {
            return Json(new { error = "Geocoding cancelled.", query = input, results = Array.Empty<object>() });
        }
        catch (Exception ex)
        {
            return Json(new
            {
                error = $"Geocoding failed: {ex.Message}",
                query = input,
                results = Array.Empty<object>()
            });
        }
    }

    [McpServerTool, Description(
        "Gets weather forecast for coordinates. Uses NWS for US and " +
        "Open-Meteo fallback otherwise. Returns normalized current + daily data, " +
        "provider details, alerts, and cache metadata. " +
        "Pass coordinates as either top-level `latitude`+`longitude` OR " +
        "inside a `location` object; if only a place name is available, " +
        "call `weather_geocode` first to resolve coordinates.")]
    public static async Task<string> WeatherForecast(
        [Description("Latitude in decimal degrees (or omit and pass via `location`)")]
        double? latitude = null,
        [Description("Longitude in decimal degrees (or omit and pass via `location`)")]
        double? longitude = null,
        [Description("Alternate location container. Accepts a place-name string OR a nested object with `latitude`/`longitude`. Some callers prefer to wrap coordinates rather than flatten them; both forms are honored.")]
        System.Text.Json.JsonElement? location = null,
        [Description("Optional place label for response context")] string? placeHint = null,
        [Description("Optional country code from geocoder, e.g. 'US'")] string? countryCode = null,
        [Description("Days of daily forecast (1-7, default 7)")] int days = 7,
        CancellationToken cancellationToken = default)
    {
        // Pull lat/lon out of either the flat params or the nested
        // `location` object. Small models wrap coordinates in a `location`
        // container roughly as often as they flatten them — honoring both
        // shapes removes an entire class of "Weather forecast: 46 chars"
        // argument-binding failures downstream.
        var (resolvedLat, resolvedLon, resolvedPlaceHint) = ResolveForecastCoordinates(
            latitude, longitude, location, placeHint);

        if (resolvedLat is null || resolvedLon is null)
        {
            return Json(new
            {
                error = "Coordinates required. Pass `latitude` + `longitude` (flat) or inside a `location` object; if you only have a place name, call `weather_geocode` first.",
            });
        }

        var effectiveLatitude = resolvedLat.Value;
        var effectiveLongitude = resolvedLon.Value;

        if (effectiveLatitude is < -90 or > 90 || effectiveLongitude is < -180 or > 180)
        {
            return Json(new
            {
                error = "Coordinates out of range.",
                latitude = effectiveLatitude,
                longitude = effectiveLongitude
            });
        }

        placeHint ??= resolvedPlaceHint;

        days = Math.Clamp(days, 1, 7);

        try
        {
            var args = new
            {
                latitude = effectiveLatitude,
                longitude = effectiveLongitude,
                placeHint = placeHint ?? string.Empty,
                countryCode = countryCode ?? string.Empty,
                days
            };
            var cached = await ToolResultCache.GetAsync<string>("weather_forecast", args);
            if (!string.IsNullOrWhiteSpace(cached))
                return cached;

            var forecast = await Service.Value.ForecastAsync(
                effectiveLatitude, effectiveLongitude, placeHint, countryCode, days, cancellationToken);

            var response = Json(new
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

            await ToolResultCache.SetAsync("weather_forecast", args, response, ToolResultCache.ResolveWeatherTtl());
            return response;
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

    /// <summary>
    /// Pulls latitude/longitude from the flexible parameter shapes
    /// <c>weather_forecast</c> accepts. Priority order:
    ///   1. Flat <paramref name="latitude"/>/<paramref name="longitude"/>
    ///      when both present.
    ///   2. Nested object in <paramref name="locationArg"/> with
    ///      <c>latitude</c> / <c>longitude</c> properties (or aliases
    ///      <c>lat</c> / <c>lon</c> / <c>lng</c>).
    ///   3. Returns (null, null, hint) when coordinates cannot be parsed
    ///      — caller surfaces a structured error telling the LLM to
    ///      geocode first.
    /// </summary>
    private static (double? Lat, double? Lon, string? PlaceHint) ResolveForecastCoordinates(
        double? latitude,
        double? longitude,
        System.Text.Json.JsonElement? locationArg,
        string? placeHint)
    {
        if (latitude is not null && longitude is not null)
            return (latitude, longitude, placeHint);

        if (locationArg is null)
            return (latitude, longitude, placeHint);

        var loc = locationArg.Value;
        if (loc.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            double? lat = TryReadNumber(loc, "latitude") ?? TryReadNumber(loc, "lat");
            double? lon = TryReadNumber(loc, "longitude") ?? TryReadNumber(loc, "lon") ?? TryReadNumber(loc, "lng");
            string? hint = placeHint;
            if (hint is null && loc.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == System.Text.Json.JsonValueKind.String)
                hint = nameEl.GetString();
            if (lat is not null && lon is not null)
                return (lat, lon, hint);
        }
        else if (loc.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            // "47.6, -122.3" — parse as lat,lon pair.
            var raw = loc.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parts = raw!.Split(',');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pLat) &&
                    double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pLon))
                {
                    return (pLat, pLon, placeHint);
                }
                // Non-numeric string — pass through as a place hint so
                // the error response tells the LLM to geocode first.
                return (latitude, longitude, placeHint ?? raw);
            }
        }

        return (latitude, longitude, placeHint);
    }

    private static double? TryReadNumber(System.Text.Json.JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == System.Text.Json.JsonValueKind.Number && el.TryGetDouble(out var d))
            return d;
        if (el.ValueKind == System.Text.Json.JsonValueKind.String &&
            double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }
}
