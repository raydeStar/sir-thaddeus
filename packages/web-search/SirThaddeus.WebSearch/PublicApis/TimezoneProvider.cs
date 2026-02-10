using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SirThaddeus.WebSearch;

// ─────────────────────────────────────────────────────────────────────────
// Timezone Provider
//
// Coordinate-first timezone resolution:
//   - US: NWS points endpoint (timeZone)
//   - Fallback/global: Open-Meteo (timezone=auto)
//
// Safety and behavior:
//   - Bounded timeout + retry
//   - In-memory TTL cache
//   - Request coalescing (same key, same in-flight task)
//   - Polite per-provider throttling
// ─────────────────────────────────────────────────────────────────────────

public sealed class TimezoneProvider : ITimezoneProvider, IDisposable
{
    private const string NwsPointsEndpoint = "https://api.weather.gov/points";
    private const string OpenMeteoEndpoint = "https://api.open-meteo.com/v1/forecast";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly TimeProvider _timeProvider;
    private readonly PublicApiServiceOptions _options;
    private readonly TimeSpan _cacheTtl;
    private readonly ProviderThrottle _throttle;

    private readonly ConcurrentDictionary<string, CacheEntry<TimezoneResolution>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task<TimezoneResolution>>> _inflight =
        new(StringComparer.OrdinalIgnoreCase);

    public TimezoneProvider(
        PublicApiServiceOptions? options = null,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new PublicApiServiceOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheTtl = TimeSpan.FromMinutes(Math.Max(60, _options.TimezoneCacheMinutes));

        _http = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _throttle = new ProviderThrottle(
            _options.MaxConcurrentRequestsPerProvider,
            TimeSpan.FromMilliseconds(Math.Max(0, _options.MinRequestSpacingMs)),
            _timeProvider);

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            var userAgent = string.IsNullOrWhiteSpace(_options.UserAgent)
                ? "SirThaddeusCopilot/1.0 (contact: local-runtime@localhost)"
                : _options.UserAgent.Trim();
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        }
    }

    public async Task<TimezoneResolution> ResolveAsync(
        double latitude,
        double longitude,
        string? countryCode = null,
        CancellationToken cancellationToken = default)
    {
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(latitude), "Coordinates out of range.");

        var cc = (countryCode ?? "").Trim().ToUpperInvariant();
        var key = BuildCacheKey(latitude, longitude, cc);
        var now = _timeProvider.GetUtcNow();

        if (PublicApiCacheHelper.TryGetFresh(_cache, key, _cacheTtl, now, out var cached, out var ageSeconds))
        {
            return cached with
            {
                Cache = new PublicApiCacheMetadata { Hit = true, AgeSeconds = ageSeconds }
            };
        }

        return await RequestCoalescer.CoalesceAsync(
            _inflight,
            key,
            async () =>
            {
                var insideNow = _timeProvider.GetUtcNow();
                if (PublicApiCacheHelper.TryGetFresh(_cache, key, _cacheTtl, insideNow, out var secondCached, out var secondAge))
                {
                    return secondCached with
                    {
                        Cache = new PublicApiCacheMetadata { Hit = true, AgeSeconds = secondAge }
                    };
                }

                var fetched = await FetchAsync(latitude, longitude, cc, cancellationToken).ConfigureAwait(false);
                var normalized = fetched with
                {
                    Cache = new PublicApiCacheMetadata { Hit = false, AgeSeconds = 0 }
                };

                _cache[key] = new CacheEntry<TimezoneResolution>(normalized, insideNow);
                return normalized;
            }).ConfigureAwait(false);
    }

    private async Task<TimezoneResolution> FetchAsync(
        double latitude,
        double longitude,
        string countryCode,
        CancellationToken cancellationToken)
    {
        if (countryCode == "US")
        {
            try
            {
                return await FetchFromNwsAsync(latitude, longitude, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Fall through to Open-Meteo.
            }
        }

        return await FetchFromOpenMeteoAsync(latitude, longitude, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TimezoneResolution> FetchFromNwsAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        var lat = latitude.ToString("F4", CultureInfo.InvariantCulture);
        var lon = longitude.ToString("F4", CultureInfo.InvariantCulture);
        var url = $"{NwsPointsEndpoint}/{lat},{lon}";

        using var doc = await GetJsonAsync(
            url,
            timeoutMs: Math.Clamp(_options.RequestTimeoutMs, 2_000, 20_000),
            acceptGeoJson: true,
            cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("properties", out var props) ||
            !props.TryGetProperty("timeZone", out var tzEl))
        {
            throw new InvalidOperationException("NWS response missing timeZone.");
        }

        var timezone = tzEl.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(timezone))
            throw new InvalidOperationException("NWS returned an empty timeZone.");

        return new TimezoneResolution
        {
            Latitude = latitude,
            Longitude = longitude,
            Timezone = timezone.Trim(),
            Source = "nws",
            Cache = new PublicApiCacheMetadata()
        };
    }

    private async Task<TimezoneResolution> FetchFromOpenMeteoAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        var lat = latitude.ToString("F4", CultureInfo.InvariantCulture);
        var lon = longitude.ToString("F4", CultureInfo.InvariantCulture);
        var url =
            $"{OpenMeteoEndpoint}?latitude={lat}&longitude={lon}" +
            "&timezone=auto&current=temperature_2m&forecast_days=1";

        using var doc = await GetJsonAsync(
            url,
            timeoutMs: Math.Clamp(_options.RequestTimeoutMs, 2_000, 20_000),
            acceptGeoJson: false,
            cancellationToken).ConfigureAwait(false);

        var timezone = doc.RootElement.TryGetProperty("timezone", out var tzEl)
            ? (tzEl.GetString() ?? "")
            : "";

        if (string.IsNullOrWhiteSpace(timezone))
            throw new InvalidOperationException("Open-Meteo response missing timezone.");

        return new TimezoneResolution
        {
            Latitude = latitude,
            Longitude = longitude,
            Timezone = timezone.Trim(),
            Source = "open-meteo",
            Cache = new PublicApiCacheMetadata()
        };
    }

    private async Task<JsonDocument> GetJsonAsync(
        string url,
        int timeoutMs,
        bool acceptGeoJson,
        CancellationToken cancellationToken)
    {
        await _throttle.WaitTurnAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeoutMs);

            var response = await RetryHelper.ExecuteAsync(async () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (acceptGeoJson)
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/geo+json"));

                var res = await _http.SendAsync(
                    req,
                    HttpCompletionOption.ResponseHeadersRead,
                    linked.Token).ConfigureAwait(false);

                if (!res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                    throw new HttpRequestException(
                        $"Timezone API returned {(int)res.StatusCode} ({res.StatusCode}). Body: {Truncate(body, 220)}",
                        null,
                        res.StatusCode);
                }

                return res;
            }, linked.Token).ConfigureAwait(false);

            using (response)
            await using (var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false))
            {
                return await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: linked.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static string BuildCacheKey(double latitude, double longitude, string countryCode)
    {
        var lat = Math.Round(latitude, 3).ToString("F3", CultureInfo.InvariantCulture);
        var lon = Math.Round(longitude, 3).ToString("F3", CultureInfo.InvariantCulture);
        return $"{lat}|{lon}|{countryCode}";
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
        _throttle.Dispose();
    }
}
