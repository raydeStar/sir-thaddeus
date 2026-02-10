using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SirThaddeus.WebSearch;

// ─────────────────────────────────────────────────────────────────────────
// Weather Service
//
// Coordinate-first weather stack:
//   - Geocoding: Photon (primary), Open-Meteo fallback
//   - Forecast: NWS for US, Open-Meteo fallback otherwise
//   - Caching: in-memory geocode + forecast cache
//   - Optional local place memory: exact place->coordinate persistence
// ─────────────────────────────────────────────────────────────────────────

public sealed class WeatherService : IDisposable
{
    private const string PhotonGeocodeEndpoint = "https://photon.komoot.io/api";
    private const string OpenMeteoGeocodeEndpoint = "https://geocoding-api.open-meteo.com/v1/search";
    private const string OpenMeteoEndpoint = "https://api.open-meteo.com/v1/forecast";
    private const string NwsBaseUrl = "https://api.weather.gov";

    private readonly HttpClient _http;
    private readonly TimeProvider _timeProvider;
    private readonly WeatherServiceOptions _options;
    private readonly bool _ownsHttpClient;

    private readonly TimeSpan _geocodeTtl;
    private readonly TimeSpan _forecastTtl;

    private readonly ConcurrentDictionary<string, CacheEntry<GeocodeLookup>> _geocodeCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CacheEntry<WeatherForecast>> _forecastCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _placeMemoryLock = new(1, 1);
    private Dictionary<string, GeocodeCandidate>? _placeMemory;

    public WeatherService(
        WeatherServiceOptions? options = null,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new WeatherServiceOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;

        _geocodeTtl = TimeSpan.FromMinutes(Math.Max(1, _options.GeocodeCacheMinutes));
        _forecastTtl = TimeSpan.FromMinutes(Math.Clamp(_options.ForecastCacheMinutes, 10, 30));

        _http = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;

        // NWS requires a valid User-Agent. Keep deterministic and non-empty.
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            var userAgent = string.IsNullOrWhiteSpace(_options.UserAgent)
                ? "SirThaddeusCopilot/1.0 (contact: local-runtime@localhost)"
                : _options.UserAgent.Trim();
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        }
    }

    public async Task<GeocodeLookup> GeocodeAsync(
        string place,
        int maxResults = 3,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(place))
        {
            return new GeocodeLookup
            {
                Query = place,
                Results = [],
                Cache = new WeatherCacheMetadata { Hit = false, AgeSeconds = 0 },
                Source = "invalid"
            };
        }

        maxResults = Math.Clamp(maxResults, 1, 5);
        var normalized = NormalizePlaceKey(place);
        var now = _timeProvider.GetUtcNow();

        // Optional user-approved local place memory.
        var memoryHit = await TryReadPlaceMemoryAsync(normalized, ct);
        if (memoryHit is not null)
        {
            return new GeocodeLookup
            {
                Query = place,
                Results = [memoryHit],
                Cache = new WeatherCacheMetadata { Hit = true, AgeSeconds = 0 },
                Source = "place-memory"
            };
        }

        if (TryGetFresh(_geocodeCache, normalized, _geocodeTtl, now, out var cachedGeocode, out var ageSeconds))
        {
            return cachedGeocode with
            {
                Cache = new WeatherCacheMetadata { Hit = true, AgeSeconds = ageSeconds }
            };
        }

        var fetched = await FetchGeocodeWithFallbackAsync(place, maxResults, ct);
        var withCache = fetched with
        {
            Cache = new WeatherCacheMetadata { Hit = false, AgeSeconds = 0 }
        };

        _geocodeCache[normalized] = new CacheEntry<GeocodeLookup>(withCache, now);

        // Persist top exact mapping only when local place memory is enabled.
        if (withCache.Results.Count > 0)
            await TryWritePlaceMemoryAsync(normalized, withCache.Results[0], ct);

        return withCache;
    }

    public async Task<WeatherForecast> ForecastAsync(
        double latitude,
        double longitude,
        string? placeHint = null,
        string? countryCode = null,
        int days = 7,
        CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 7);
        var isUs = string.Equals(countryCode, "US", StringComparison.OrdinalIgnoreCase)
                   || LooksUsPlaceHint(placeHint);

        var mode = NormalizeProviderMode(_options.ProviderMode);
        var key = BuildForecastCacheKey(latitude, longitude, isUs, days, mode);
        var now = _timeProvider.GetUtcNow();

        if (TryGetFresh(_forecastCache, key, _forecastTtl, now, out var cachedForecast, out var ageSeconds))
        {
            return cachedForecast with
            {
                Cache = new WeatherCacheMetadata { Hit = true, AgeSeconds = ageSeconds }
            };
        }

        WeatherForecast fetched;

        if (mode == "openmeteo_only")
        {
            fetched = await FetchOpenMeteoForecastAsync(
                latitude, longitude, placeHint, countryCode, days,
                providerReason: "mode_openmeteo_only", ct);
        }
        else if (mode == "nws_only_us")
        {
            if (!isUs)
                throw new InvalidOperationException("NWS mode is US-only; non-US location provided.");

            fetched = await FetchNwsForecastAsync(
                latitude, longitude, placeHint, days, providerReason: "mode_nws_only_us", ct);
        }
        else
        {
            // Default: NWS for US, Open-Meteo otherwise or on NWS failure.
            if (isUs)
            {
                try
                {
                    fetched = await FetchNwsForecastAsync(
                        latitude, longitude, placeHint, days, providerReason: "us_primary", ct);
                }
                catch
                {
                    fetched = await FetchOpenMeteoForecastAsync(
                        latitude, longitude, placeHint, countryCode, days,
                        providerReason: "fallback_nws_error", ct);
                }
            }
            else
            {
                fetched = await FetchOpenMeteoForecastAsync(
                    latitude, longitude, placeHint, countryCode, days,
                    providerReason: "non_us_openmeteo", ct);
            }
        }

        fetched = fetched with
        {
            Cache = new WeatherCacheMetadata { Hit = false, AgeSeconds = 0 }
        };

        _forecastCache[key] = new CacheEntry<WeatherForecast>(fetched, now);
        return fetched;
    }

    // ─────────────────────────────────────────────────────────────────
    // Provider: Geocoding (Photon primary, Open-Meteo fallback)
    // ─────────────────────────────────────────────────────────────────

    private async Task<GeocodeLookup> FetchGeocodeWithFallbackAsync(
        string place,
        int maxResults,
        CancellationToken ct)
    {
        try
        {
            return await FetchPhotonGeocodeAsync(place, maxResults, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Public Photon endpoint can throttle occasionally. Fall back to
            // Open-Meteo geocoding so the weather pipeline keeps working.
            return await FetchOpenMeteoGeocodeAsync(place, maxResults, ct, "open-meteo-fallback");
        }
    }

    private async Task<GeocodeLookup> FetchPhotonGeocodeAsync(
        string place,
        int maxResults,
        CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(place.Trim());
        var url = $"{PhotonGeocodeEndpoint}?q={encoded}&limit={maxResults}&lang=en";
        using var doc = await GetJsonAsync(url, timeoutMs: 7_000, ct);

        var results = new List<GeocodeCandidate>();
        if (doc.RootElement.TryGetProperty("features", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var feature in arr.EnumerateArray().Take(maxResults))
            {
                if (!feature.TryGetProperty("geometry", out var geometry) ||
                    !geometry.TryGetProperty("coordinates", out var coords) ||
                    coords.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                using var coordEnum = coords.EnumerateArray();
                if (!coordEnum.MoveNext() || !coordEnum.Current.TryGetDouble(out var lon) ||
                    !coordEnum.MoveNext() || !coordEnum.Current.TryGetDouble(out var lat))
                {
                    continue;
                }

                feature.TryGetProperty("properties", out var props);
                var name = TryGetFirstNonEmpty(props, "name", "city", "town", "village", "county");
                var region = TryGetFirstNonEmpty(props, "state", "county");
                var country = TryGetFirstNonEmpty(props, "country");
                var countryCode = TryGetFirstNonEmpty(props, "countrycode", "country_code");
                var display = BuildDisplayName(name, region, country, countryCode);
                if (string.IsNullOrWhiteSpace(display))
                    display = $"{lat.ToString("F4", CultureInfo.InvariantCulture)}, {lon.ToString("F4", CultureInfo.InvariantCulture)}";

                var cc = string.IsNullOrWhiteSpace(countryCode) ? "" : countryCode.ToUpperInvariant();
                var isUs = string.Equals(cc, "US", StringComparison.OrdinalIgnoreCase);

                results.Add(new GeocodeCandidate
                {
                    Name = display,
                    Region = region,
                    CountryCode = string.IsNullOrWhiteSpace(cc) ? country : cc,
                    IsUs = isUs,
                    Latitude = lat,
                    Longitude = lon,
                    Confidence = EstimateGeocodeConfidence(place, name, region)
                });
            }
        }

        return new GeocodeLookup
        {
            Query = place,
            Results = results,
            Cache = new WeatherCacheMetadata(),
            Source = "photon"
        };
    }

    private async Task<GeocodeLookup> FetchOpenMeteoGeocodeAsync(
        string place,
        int maxResults,
        CancellationToken ct,
        string source)
    {
        var encoded = Uri.EscapeDataString(place.Trim());
        var url = $"{OpenMeteoGeocodeEndpoint}?name={encoded}&count={maxResults}&language=en&format=json";
        using var doc = await GetJsonAsync(url, timeoutMs: 7_000, ct);

        var results = new List<GeocodeCandidate>();
        if (doc.RootElement.TryGetProperty("results", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray().Take(maxResults))
            {
                var name = item.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                var admin1 = item.TryGetProperty("admin1", out var a1) ? (a1.GetString() ?? "") : "";
                var country = item.TryGetProperty("country", out var ctry) ? (ctry.GetString() ?? "") : "";
                var countryCode = item.TryGetProperty("country_code", out var cc)
                    ? (cc.GetString() ?? "")
                    : "";

                if (!item.TryGetProperty("latitude", out var latEl) ||
                    !item.TryGetProperty("longitude", out var lonEl))
                {
                    continue;
                }

                if (!latEl.TryGetDouble(out var lat) || !lonEl.TryGetDouble(out var lon))
                    continue;

                var display = BuildDisplayName(name, admin1, country, countryCode);
                var isUs = string.Equals(countryCode, "US", StringComparison.OrdinalIgnoreCase);

                results.Add(new GeocodeCandidate
                {
                    Name = display,
                    Region = admin1,
                    CountryCode = string.IsNullOrWhiteSpace(countryCode) ? country : countryCode.ToUpperInvariant(),
                    IsUs = isUs,
                    Latitude = lat,
                    Longitude = lon,
                    Confidence = EstimateGeocodeConfidence(place, name, admin1)
                });
            }
        }

        return new GeocodeLookup
        {
            Query = place,
            Results = results,
            Cache = new WeatherCacheMetadata(),
            Source = source
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Provider: NWS (US primary)
    // ─────────────────────────────────────────────────────────────────

    private async Task<WeatherForecast> FetchNwsForecastAsync(
        double latitude,
        double longitude,
        string? placeHint,
        int days,
        string providerReason,
        CancellationToken ct)
    {
        var lat = latitude.ToString("F4", CultureInfo.InvariantCulture);
        var lon = longitude.ToString("F4", CultureInfo.InvariantCulture);

        using var pointsDoc = await GetJsonAsync($"{NwsBaseUrl}/points/{lat},{lon}", timeoutMs: 8_000, ct);

        var props = pointsDoc.RootElement.GetProperty("properties");
        var forecastUrl = props.GetProperty("forecast").GetString();
        var hourlyUrl = props.TryGetProperty("forecastHourly", out var hr)
            ? hr.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(forecastUrl))
            throw new InvalidOperationException("NWS points response missing forecast URL.");

        string locationName = placeHint ?? "";
        if (string.IsNullOrWhiteSpace(locationName) &&
            props.TryGetProperty("relativeLocation", out var rl) &&
            rl.TryGetProperty("properties", out var rlp))
        {
            var city = rlp.TryGetProperty("city", out var cityEl) ? cityEl.GetString() : null;
            var state = rlp.TryGetProperty("state", out var stateEl) ? stateEl.GetString() : null;
            locationName = string.Join(", ", new[] { city, state }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        if (string.IsNullOrWhiteSpace(locationName))
            locationName = $"{lat}, {lon}";

        using var forecastDoc = await GetJsonAsync(forecastUrl!, timeoutMs: 8_000, ct);

        JsonDocument? hourlyDoc = null;
        if (!string.IsNullOrWhiteSpace(hourlyUrl))
        {
            try
            {
                hourlyDoc = await GetJsonAsync(hourlyUrl!, timeoutMs: 8_000, ct);
            }
            catch
            {
                // Optional enrich path; continue with standard forecast.
            }
        }

        JsonDocument? alertsDoc = null;
        try
        {
            alertsDoc = await GetJsonAsync($"{NwsBaseUrl}/alerts/active?point={lat},{lon}", timeoutMs: 7_000, ct);
        }
        catch
        {
            // Alerts are optional.
        }

        var current = ExtractNwsCurrent(hourlyDoc, forecastDoc);
        var daily = ExtractNwsDaily(forecastDoc, days);
        var alerts = ExtractNwsAlerts(alertsDoc);

        hourlyDoc?.Dispose();
        alertsDoc?.Dispose();

        return new WeatherForecast
        {
            Provider = "nws",
            ProviderReason = providerReason,
            Location = new WeatherLocation
            {
                Name = locationName,
                CountryCode = "US",
                IsUs = true,
                Latitude = latitude,
                Longitude = longitude
            },
            Current = current,
            Daily = daily,
            Alerts = alerts,
            Cache = new WeatherCacheMetadata()
        };
    }

    private static WeatherCurrent? ExtractNwsCurrent(JsonDocument? hourlyDoc, JsonDocument forecastDoc)
    {
        // Prefer hourly for "right now".
        if (hourlyDoc is not null &&
            TryGetFirstNwsPeriod(hourlyDoc.RootElement, out var p))
        {
            return BuildNwsCurrentFromPeriod(p);
        }

        // Fallback to first forecast period.
        if (TryGetFirstNwsPeriod(forecastDoc.RootElement, out var f))
            return BuildNwsCurrentFromPeriod(f);

        return null;
    }

    private static WeatherCurrent BuildNwsCurrentFromPeriod(JsonElement period)
    {
        var temp = period.TryGetProperty("temperature", out var t) && t.TryGetInt32(out var tv)
            ? tv
            : (int?)null;
        var unit = period.TryGetProperty("temperatureUnit", out var u) ? (u.GetString() ?? "") : "";
        var condition = period.TryGetProperty("shortForecast", out var s) ? (s.GetString() ?? "") : "";
        var wind = period.TryGetProperty("windSpeed", out var w) ? (w.GetString() ?? "") : "";

        int? humidity = null;
        if (period.TryGetProperty("relativeHumidity", out var rh) &&
            rh.TryGetProperty("value", out var rhv) &&
            rhv.ValueKind == JsonValueKind.Number &&
            rhv.TryGetDouble(out var rhd))
        {
            humidity = (int)Math.Round(rhd);
        }

        DateTimeOffset? observedAt = null;
        if (period.TryGetProperty("startTime", out var st) &&
            DateTimeOffset.TryParse(st.GetString(), out var parsed))
        {
            observedAt = parsed;
        }

        return new WeatherCurrent
        {
            Temperature = temp,
            Unit = NormalizeTemperatureUnit(unit),
            Condition = condition,
            Wind = wind,
            HumidityPercent = humidity,
            ObservedAt = observedAt
        };
    }

    private static IReadOnlyList<WeatherDaily> ExtractNwsDaily(JsonDocument forecastDoc, int maxDays)
    {
        var list = new List<WeatherDaily>();

        if (!forecastDoc.RootElement.TryGetProperty("properties", out var props) ||
            !props.TryGetProperty("periods", out var periods) ||
            periods.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        var byDate = new Dictionary<DateOnly, List<(int temp, string unit, string condition, bool isDay)>>();

        foreach (var p in periods.EnumerateArray())
        {
            if (!p.TryGetProperty("startTime", out var st) ||
                !DateTimeOffset.TryParse(st.GetString(), out var dto))
                continue;

            var date = DateOnly.FromDateTime(dto.Date);
            var temp = p.TryGetProperty("temperature", out var t) && t.TryGetInt32(out var tv)
                ? tv
                : int.MinValue;
            if (temp == int.MinValue)
                continue;

            var unit = p.TryGetProperty("temperatureUnit", out var u)
                ? NormalizeTemperatureUnit(u.GetString() ?? "")
                : "";
            var cond = p.TryGetProperty("shortForecast", out var c) ? (c.GetString() ?? "") : "";
            var isDay = p.TryGetProperty("isDaytime", out var d) && d.ValueKind == JsonValueKind.True;

            if (!byDate.TryGetValue(date, out var bucket))
            {
                bucket = [];
                byDate[date] = bucket;
            }

            bucket.Add((temp, unit, cond, isDay));
        }

        foreach (var kvp in byDate.OrderBy(k => k.Key).Take(maxDays))
        {
            var entries = kvp.Value;
            var high = entries.Max(e => e.temp);
            var low = entries.Min(e => e.temp);
            var unit = entries.FirstOrDefault().unit;

            // Prefer daytime label when available.
            var condition = entries.FirstOrDefault(e => e.isDay).condition;
            if (string.IsNullOrWhiteSpace(condition))
                condition = entries.FirstOrDefault().condition;

            int? avg = null;
            if (high != int.MinValue && low != int.MaxValue)
                avg = (int)Math.Round((high + low) / 2.0);

            list.Add(new WeatherDaily
            {
                Date = kvp.Key,
                TempHigh = high,
                TempLow = low,
                Unit = unit,
                Condition = condition ?? "",
                AvgTemp = avg
            });
        }

        return list;
    }

    private static IReadOnlyList<WeatherAlert> ExtractNwsAlerts(JsonDocument? alertsDoc)
    {
        var alerts = new List<WeatherAlert>();
        if (alertsDoc is null)
            return alerts;

        if (!alertsDoc.RootElement.TryGetProperty("features", out var features) ||
            features.ValueKind != JsonValueKind.Array)
            return alerts;

        foreach (var feature in features.EnumerateArray().Take(5))
        {
            if (!feature.TryGetProperty("properties", out var p))
                continue;

            var headline = p.TryGetProperty("headline", out var h) ? (h.GetString() ?? "") : "";
            var severity = p.TryGetProperty("severity", out var s) ? (s.GetString() ?? "") : "";
            var @event = p.TryGetProperty("event", out var e) ? (e.GetString() ?? "") : "";

            if (string.IsNullOrWhiteSpace(headline))
                continue;

            alerts.Add(new WeatherAlert
            {
                Headline = headline,
                Severity = severity,
                Event = @event
            });
        }

        return alerts;
    }

    private static bool TryGetFirstNwsPeriod(JsonElement root, out JsonElement period)
    {
        period = default;
        if (!root.TryGetProperty("properties", out var props) ||
            !props.TryGetProperty("periods", out var periods) ||
            periods.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var p in periods.EnumerateArray())
        {
            period = p;
            return true;
        }

        return false;
    }

    // ─────────────────────────────────────────────────────────────────
    // Provider: Open-Meteo (fallback / non-US)
    // ─────────────────────────────────────────────────────────────────

    private async Task<WeatherForecast> FetchOpenMeteoForecastAsync(
        double latitude,
        double longitude,
        string? placeHint,
        string? countryCode,
        int days,
        string providerReason,
        CancellationToken ct)
    {
        var isUs = string.Equals(countryCode, "US", StringComparison.OrdinalIgnoreCase)
                   || LooksUsPlaceHint(placeHint);
        var tempUnit = isUs ? "fahrenheit" : "celsius";
        var windUnit = isUs ? "mph" : "kmh";

        var lat = latitude.ToString("F4", CultureInfo.InvariantCulture);
        var lon = longitude.ToString("F4", CultureInfo.InvariantCulture);
        var url =
            $"{OpenMeteoEndpoint}?latitude={lat}&longitude={lon}" +
            $"&current=temperature_2m,weather_code,wind_speed_10m,relative_humidity_2m" +
            $"&daily=weather_code,temperature_2m_max,temperature_2m_min" +
            $"&timezone=auto&forecast_days={days}" +
            $"&temperature_unit={tempUnit}&wind_speed_unit={windUnit}";

        using var doc = await GetJsonAsync(url, timeoutMs: 8_000, ct);

        var current = ExtractOpenMeteoCurrent(doc.RootElement, isUs);
        var daily = ExtractOpenMeteoDaily(doc.RootElement, days, isUs);

        return new WeatherForecast
        {
            Provider = "open-meteo",
            ProviderReason = providerReason,
            Location = new WeatherLocation
            {
                Name = string.IsNullOrWhiteSpace(placeHint) ? $"{lat}, {lon}" : placeHint!,
                CountryCode = string.IsNullOrWhiteSpace(countryCode) ? "" : countryCode.ToUpperInvariant(),
                IsUs = isUs,
                Latitude = latitude,
                Longitude = longitude
            },
            Current = current,
            Daily = daily,
            Alerts = [],
            Cache = new WeatherCacheMetadata()
        };
    }

    private static WeatherCurrent? ExtractOpenMeteoCurrent(JsonElement root, bool isUs)
    {
        if (!root.TryGetProperty("current", out var current) ||
            current.ValueKind != JsonValueKind.Object)
            return null;

        int? temp = null;
        if (current.TryGetProperty("temperature_2m", out var t) && t.TryGetDouble(out var td))
            temp = (int)Math.Round(td);

        var code = current.TryGetProperty("weather_code", out var wc) && wc.TryGetInt32(out var cv)
            ? cv
            : -1;

        int? humidity = null;
        if (current.TryGetProperty("relative_humidity_2m", out var rh) && rh.TryGetDouble(out var rhd))
            humidity = (int)Math.Round(rhd);

        var windValue = current.TryGetProperty("wind_speed_10m", out var ws) && ws.TryGetDouble(out var wsd)
            ? Math.Round(wsd).ToString(CultureInfo.InvariantCulture)
            : "";
        var windUnit = isUs ? "mph" : "km/h";
        var wind = string.IsNullOrWhiteSpace(windValue) ? "" : $"{windValue} {windUnit}";

        DateTimeOffset? observedAt = null;
        if (current.TryGetProperty("time", out var timeEl) &&
            DateTimeOffset.TryParse(timeEl.GetString(), out var parsed))
        {
            observedAt = parsed;
        }

        return new WeatherCurrent
        {
            Temperature = temp,
            Unit = isUs ? "F" : "C",
            Condition = MapOpenMeteoCodeToCondition(code),
            Wind = wind,
            HumidityPercent = humidity,
            ObservedAt = observedAt
        };
    }

    private static IReadOnlyList<WeatherDaily> ExtractOpenMeteoDaily(JsonElement root, int days, bool isUs)
    {
        var list = new List<WeatherDaily>();
        if (!root.TryGetProperty("daily", out var daily) ||
            daily.ValueKind != JsonValueKind.Object)
            return list;

        var dates = daily.TryGetProperty("time", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : [];
        var maxes = daily.TryGetProperty("temperature_2m_max", out var max) && max.ValueKind == JsonValueKind.Array
            ? max.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var v) ? (double?)v : null).ToList()
            : [];
        var mins = daily.TryGetProperty("temperature_2m_min", out var min) && min.ValueKind == JsonValueKind.Array
            ? min.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var v) ? (double?)v : null).ToList()
            : [];
        var codes = daily.TryGetProperty("weather_code", out var code) && code.ValueKind == JsonValueKind.Array
            ? code.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var v) ? (int?)v : null).ToList()
            : [];

        var count = new[] { dates.Count, maxes.Count, mins.Count, codes.Count }.Min();
        for (var i = 0; i < count && i < days; i++)
        {
            if (!DateOnly.TryParse(dates[i], out var date))
                continue;

            var hi = maxes[i] is null ? (int?)null : (int)Math.Round(maxes[i]!.Value);
            var lo = mins[i] is null ? (int?)null : (int)Math.Round(mins[i]!.Value);
            int? avg = null;
            if (hi.HasValue && lo.HasValue)
                avg = (int)Math.Round((hi.Value + lo.Value) / 2.0);

            list.Add(new WeatherDaily
            {
                Date = date,
                TempHigh = hi,
                TempLow = lo,
                Unit = isUs ? "F" : "C",
                Condition = MapOpenMeteoCodeToCondition(codes[i] ?? -1),
                AvgTemp = avg
            });
        }

        return list;
    }

    private static string MapOpenMeteoCodeToCondition(int code) => code switch
    {
        0 => "clear",
        1 => "mostly clear",
        2 => "partly cloudy",
        3 => "overcast",
        45 or 48 => "foggy",
        51 or 53 or 55 => "drizzle",
        56 or 57 => "freezing drizzle",
        61 or 63 or 65 => "rainy",
        66 or 67 => "freezing rain",
        71 or 73 or 75 => "snowing",
        77 => "snow grains",
        80 or 81 or 82 => "rain showers",
        85 or 86 => "snow showers",
        95 => "thunderstorms",
        96 or 99 => "thunderstorms with hail",
        _ => "unknown"
    };

    // ─────────────────────────────────────────────────────────────────
    // Optional local place memory
    // ─────────────────────────────────────────────────────────────────

    private async Task<GeocodeCandidate?> TryReadPlaceMemoryAsync(string normalizedPlace, CancellationToken ct)
    {
        if (!_options.PlaceMemoryEnabled || string.IsNullOrWhiteSpace(_options.PlaceMemoryPath))
            return null;

        await _placeMemoryLock.WaitAsync(ct);
        try
        {
            await EnsurePlaceMemoryLoadedAsync(ct);
            if (_placeMemory is not null &&
                _placeMemory.TryGetValue(normalizedPlace, out var cached))
            {
                return cached;
            }
            return null;
        }
        finally
        {
            _placeMemoryLock.Release();
        }
    }

    private async Task TryWritePlaceMemoryAsync(
        string normalizedPlace,
        GeocodeCandidate candidate,
        CancellationToken ct)
    {
        if (!_options.PlaceMemoryEnabled || string.IsNullOrWhiteSpace(_options.PlaceMemoryPath))
            return;

        await _placeMemoryLock.WaitAsync(ct);
        try
        {
            await EnsurePlaceMemoryLoadedAsync(ct);
            _placeMemory ??= new Dictionary<string, GeocodeCandidate>(StringComparer.OrdinalIgnoreCase);
            _placeMemory[normalizedPlace] = candidate;

            var path = _options.PlaceMemoryPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_placeMemory, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(path, json, ct);
        }
        catch
        {
            // Local place memory is best effort by design.
        }
        finally
        {
            _placeMemoryLock.Release();
        }
    }

    private async Task EnsurePlaceMemoryLoadedAsync(CancellationToken ct)
    {
        if (_placeMemory is not null)
            return;

        _placeMemory = new Dictionary<string, GeocodeCandidate>(StringComparer.OrdinalIgnoreCase);
        var path = _options.PlaceMemoryPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, GeocodeCandidate>>(json);
            if (loaded is not null)
            {
                _placeMemory = new Dictionary<string, GeocodeCandidate>(
                    loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Ignore malformed place memory file.
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Shared helpers
    // ─────────────────────────────────────────────────────────────────

    private async Task<JsonDocument> GetJsonAsync(string url, int timeoutMs, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeoutMs);

        var response = await RetryHelper.ExecuteAsync(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (url.Contains("api.weather.gov", StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/geo+json"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }
            else
            {
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }

            var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(linked.Token);
                throw new HttpRequestException(
                    $"Weather API returned {(int)res.StatusCode} ({res.StatusCode}). Body: {Truncate(body, 240)}",
                    null,
                    res.StatusCode);
            }

            return res;
        }, linked.Token);

        using (response)
        await using (var stream = await response.Content.ReadAsStreamAsync(linked.Token))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: linked.Token);
        }
    }

    private static bool TryGetFresh<T>(
        ConcurrentDictionary<string, CacheEntry<T>> cache,
        string key,
        TimeSpan ttl,
        DateTimeOffset now,
        out T value,
        out int ageSeconds)
    {
        if (cache.TryGetValue(key, out var entry))
        {
            var age = now - entry.StoredAt;
            if (age <= ttl)
            {
                value = entry.Value;
                ageSeconds = (int)Math.Max(0, age.TotalSeconds);
                return true;
            }
        }

        value = default!;
        ageSeconds = 0;
        return false;
    }

    private static string NormalizePlaceKey(string place)
    {
        var parts = (place ?? "")
            .Trim()
            .ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    private static string BuildDisplayName(string name, string admin1, string country, string countryCode)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(name))
            parts.Add(name.Trim());
        if (!string.IsNullOrWhiteSpace(admin1))
            parts.Add(admin1.Trim());
        if (!string.IsNullOrWhiteSpace(countryCode))
            parts.Add(countryCode.Trim().ToUpperInvariant());
        else if (!string.IsNullOrWhiteSpace(country))
            parts.Add(country.Trim());
        return string.Join(", ", parts);
    }

    private static string TryGetFirstNonEmpty(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object || keys.Length == 0)
            return "";

        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value))
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
        }

        return "";
    }

    private static string NormalizeProviderMode(string mode) => mode.Trim().ToLowerInvariant() switch
    {
        "openmeteo_only" => "openmeteo_only",
        "nws_only_us" => "nws_only_us",
        _ => "nws_us_openmeteo_fallback"
    };

    private static string BuildForecastCacheKey(
        double latitude,
        double longitude,
        bool isUs,
        int days,
        string mode)
    {
        var lat = Math.Round(latitude, 3).ToString("F3", CultureInfo.InvariantCulture);
        var lon = Math.Round(longitude, 3).ToString("F3", CultureInfo.InvariantCulture);
        return $"{mode}|{(isUs ? "us" : "nonus")}|{lat}|{lon}|d{days}";
    }

    private static bool LooksUsPlaceHint(string? placeHint)
    {
        if (string.IsNullOrWhiteSpace(placeHint))
            return false;

        var lower = placeHint.ToLowerInvariant();
        return lower.Contains(", us", StringComparison.Ordinal) ||
               lower.Contains(" united states", StringComparison.Ordinal) ||
               lower.EndsWith(", id", StringComparison.Ordinal) ||
               lower.EndsWith(", ca", StringComparison.Ordinal) ||
               lower.EndsWith(", tx", StringComparison.Ordinal) ||
               lower.EndsWith(", ny", StringComparison.Ordinal) ||
               lower.EndsWith(", fl", StringComparison.Ordinal) ||
               lower.EndsWith(", wa", StringComparison.Ordinal);
    }

    private static double EstimateGeocodeConfidence(string query, string name, string admin1)
    {
        var q = NormalizePlaceKey(query);
        var label = NormalizePlaceKey($"{name} {admin1}");
        if (string.IsNullOrWhiteSpace(q) || string.IsNullOrWhiteSpace(label))
            return 0.6;

        if (label.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            return 0.95;
        if (label.Contains(q, StringComparison.OrdinalIgnoreCase))
            return 0.85;
        return 0.70;
    }

    private static string NormalizeTemperatureUnit(string unit)
    {
        var u = (unit ?? "").Trim();
        if (u.Equals("F", StringComparison.OrdinalIgnoreCase) ||
            u.Contains('F') || u.Contains('f'))
            return "F";
        if (u.Equals("C", StringComparison.OrdinalIgnoreCase) ||
            u.Contains('C') || u.Contains('c'))
            return "C";
        return u;
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return s.Length <= max ? s : s[..max] + "\u2026";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
        _placeMemoryLock.Dispose();
    }

    private sealed record CacheEntry<T>(T Value, DateTimeOffset StoredAt);
}
