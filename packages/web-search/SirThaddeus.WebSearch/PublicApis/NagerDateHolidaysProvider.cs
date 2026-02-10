using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SirThaddeus.WebSearch;

// ─────────────────────────────────────────────────────────────────────────
// Nager.Date Holidays Provider
//
// Keyless endpoints:
//   - /PublicHolidays/{year}/{country}
//   - /IsTodayPublicHoliday/{country}
//   - /NextPublicHolidays/{country}
//
// Behavior:
//   - Region filtering (county/state code) done client-side using "counties".
//   - Bounded caching + coalesced in-flight requests.
//   - Polite throttling and deterministic output shape.
// ─────────────────────────────────────────────────────────────────────────

public sealed class NagerDateHolidaysProvider : IHolidaysProvider, IDisposable
{
    private const string NagerBaseUrl = "https://date.nager.at/api/v3";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly PublicApiServiceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly ProviderThrottle _throttle;

    private readonly ConcurrentDictionary<string, CacheEntry<HolidaySetResult>> _listCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CacheEntry<HolidayTodayResult>> _todayCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CacheEntry<HolidayNextResult>> _nextCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task<HolidaySetResult>>> _listInflight =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<HolidayTodayResult>>> _todayInflight =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<HolidayNextResult>>> _nextInflight =
        new(StringComparer.OrdinalIgnoreCase);

    public NagerDateHolidaysProvider(
        PublicApiServiceOptions? options = null,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new PublicApiServiceOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheTtl = TimeSpan.FromMinutes(Math.Max(30, _options.HolidaysCacheMinutes));

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

    public async Task<HolidaySetResult> GetHolidaysAsync(
        string countryCode,
        int year,
        string? regionCode = null,
        int maxItems = 50,
        CancellationToken cancellationToken = default)
    {
        var cc = NormalizeCountryCode(countryCode);
        var yy = Math.Clamp(year, 1900, 2100);
        var region = NormalizeRegionCode(regionCode, cc);
        maxItems = Math.Clamp(maxItems, 1, 100);
        var key = $"{cc}|{yy}|{region ?? "-"}";
        var now = _timeProvider.GetUtcNow();

        if (PublicApiCacheHelper.TryGetFresh(_listCache, key, _cacheTtl, now, out var cached, out var age))
        {
            return TrimHolidaySet(cached with
            {
                Cache = new PublicApiCacheMetadata { Hit = true, AgeSeconds = age }
            }, maxItems);
        }

        var fetched = await RequestCoalescer.CoalesceAsync(
            _listInflight,
            key,
            async () =>
            {
                var insideNow = _timeProvider.GetUtcNow();
                if (PublicApiCacheHelper.TryGetFresh(_listCache, key, _cacheTtl, insideNow, out var secondCache, out var secondAge))
                {
                    return secondCache with
                    {
                        Cache = new PublicApiCacheMetadata { Hit = true, AgeSeconds = secondAge }
                    };
                }

                var all = await FetchPublicHolidaysAsync(cc, yy, region, cancellationToken).ConfigureAwait(false);
                var result = new HolidaySetResult
                {
                    CountryCode = cc,
                    RegionCode = region,
                    Year = yy,
                    Holidays = all,
                    Source = "nager-date",
                    Cache = new PublicApiCacheMetadata { Hit = false, AgeSeconds = 0 }
                };

                _listCache[key] = new CacheEntry<HolidaySetResult>(result, insideNow);
                return result;
            }).ConfigureAwait(false);

        return TrimHolidaySet(fetched, maxItems);
    }

    public async Task<HolidayTodayResult> IsTodayPublicHolidayAsync(
        string countryCode,
        string? regionCode = null,
        CancellationToken cancellationToken = default)
    {
        var cc = NormalizeCountryCode(countryCode);
        var region = NormalizeRegionCode(regionCode, cc);
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var key = $"{cc}|{region ?? "-"}|{today:yyyy-MM-dd}";
        var now = _timeProvider.GetUtcNow();

        if (PublicApiCacheHelper.TryGetFresh(_todayCache, key, _cacheTtl, now, out var cached, out var age))
        {
            return cached with
            {
                Cache = new PublicApiCacheMetadata { Hit = true, AgeSeconds = age }
            };
        }

        return await RequestCoalescer.CoalesceAsync(
            _todayInflight,
            key,
            async () =>
            {
                var insideNow = _timeProvider.GetUtcNow();
                if (PublicApiCacheHelper.TryGetFresh(_todayCache, key, _cacheTtl, insideNow, out var second, out var secondAge))
                {
                    return second with
                    {
                        Cache = new PublicApiCacheMetadata { Hit = true, AgeSeconds = secondAge }
                    };
                }

                bool isToday;
                List<HolidayEntry> todays;

                if (string.IsNullOrWhiteSpace(region))
                {
                    isToday = await FetchIsTodayFlagAsync(cc, cancellationToken).ConfigureAwait(false);
                    if (isToday)
                    {
                        var list = await GetHolidaysAsync(cc, today.Year, null, 200, cancellationToken).ConfigureAwait(false);
                        todays = list.Holidays.Where(h => h.Date == today).ToList();
                    }
                    else
                    {
                        todays = [];
                    }
                }
                else
                {
                    var list = await GetHolidaysAsync(cc, today.Year, region, 200, cancellationToken).ConfigureAwait(false);
                    todays = list.Holidays.Where(h => h.Date == today).ToList();
                    isToday = todays.Count > 0;
                }

                var next = await GetNextPublicHolidaysAsync(cc, region, 1, cancellationToken).ConfigureAwait(false);
                var result = new HolidayTodayResult
                {
                    CountryCode = cc,
                    RegionCode = region,
                    Date = today,
                    IsPublicHoliday = isToday,
                    HolidaysToday = todays,
                    NextHoliday = next.Holidays.FirstOrDefault(),
                    Source = "nager-date",
                    Cache = new PublicApiCacheMetadata { Hit = false, AgeSeconds = 0 }
                };

                _todayCache[key] = new CacheEntry<HolidayTodayResult>(result, insideNow);
                return result;
            }).ConfigureAwait(false);
    }

    public async Task<HolidayNextResult> GetNextPublicHolidaysAsync(
        string countryCode,
        string? regionCode = null,
        int maxItems = 8,
        CancellationToken cancellationToken = default)
    {
        var cc = NormalizeCountryCode(countryCode);
        var region = NormalizeRegionCode(regionCode, cc);
        maxItems = Math.Clamp(maxItems, 1, 25);
        var key = $"{cc}|{region ?? "-"}";
        var now = _timeProvider.GetUtcNow();

        if (PublicApiCacheHelper.TryGetFresh(_nextCache, key, _cacheTtl, now, out var cached, out var age))
        {
            return TrimHolidayNext(cached with
            {
                Cache = new PublicApiCacheMetadata { Hit = true, AgeSeconds = age }
            }, maxItems);
        }

        var fetched = await RequestCoalescer.CoalesceAsync(
            _nextInflight,
            key,
            async () =>
            {
                var insideNow = _timeProvider.GetUtcNow();
                if (PublicApiCacheHelper.TryGetFresh(_nextCache, key, _cacheTtl, insideNow, out var second, out var secondAge))
                {
                    return second with
                    {
                        Cache = new PublicApiCacheMetadata { Hit = true, AgeSeconds = secondAge }
                    };
                }

                var url = $"{NagerBaseUrl}/NextPublicHolidays/{cc}";
                using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
                var parsed = ParseHolidayArray(doc.RootElement)
                    .Where(h => MatchesRegion(h, region))
                    .OrderBy(h => h.Date)
                    .ToList();

                var result = new HolidayNextResult
                {
                    CountryCode = cc,
                    RegionCode = region,
                    Holidays = parsed,
                    Source = "nager-date",
                    Cache = new PublicApiCacheMetadata { Hit = false, AgeSeconds = 0 }
                };

                _nextCache[key] = new CacheEntry<HolidayNextResult>(result, insideNow);
                return result;
            }).ConfigureAwait(false);

        return TrimHolidayNext(fetched, maxItems);
    }

    private async Task<List<HolidayEntry>> FetchPublicHolidaysAsync(
        string countryCode,
        int year,
        string? regionCode,
        CancellationToken cancellationToken)
    {
        var url = $"{NagerBaseUrl}/PublicHolidays/{year}/{countryCode}";
        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseHolidayArray(doc.RootElement)
            .Where(h => MatchesRegion(h, regionCode))
            .OrderBy(h => h.Date)
            .ToList();
    }

    private async Task<bool> FetchIsTodayFlagAsync(
        string countryCode,
        CancellationToken cancellationToken)
    {
        var url = $"{NagerBaseUrl}/IsTodayPublicHoliday/{countryCode}";
        var body = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var text = (body ?? "").Trim();

        if (bool.TryParse(text, out var parsedBool))
            return parsedBool;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.True || root.ValueKind == JsonValueKind.False)
                return root.GetBoolean();
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("isPublicHoliday", out var p) &&
                (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False))
            {
                return p.GetBoolean();
            }
        }
        catch
        {
            // Fall through.
        }

        return false;
    }

    private async Task<JsonDocument> GetJsonAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var body = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    private async Task<string> GetStringAsync(
        string url,
        CancellationToken cancellationToken)
    {
        await _throttle.WaitTurnAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(Math.Clamp(_options.RequestTimeoutMs, 2_000, 20_000));

            var response = await RetryHelper.ExecuteAsync(async () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var res = await _http.SendAsync(
                    req,
                    HttpCompletionOption.ResponseHeadersRead,
                    linked.Token).ConfigureAwait(false);

                if (!res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                    throw new HttpRequestException(
                        $"Nager.Date returned {(int)res.StatusCode} ({res.StatusCode}). Body: {Truncate(body, 220)}",
                        null,
                        res.StatusCode);
                }

                return res;
            }, linked.Token).ConfigureAwait(false);

            using (response)
            {
                return await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static HolidaySetResult TrimHolidaySet(HolidaySetResult value, int maxItems) => value with
    {
        Holidays = value.Holidays.Take(maxItems).ToArray()
    };

    private static HolidayNextResult TrimHolidayNext(HolidayNextResult value, int maxItems) => value with
    {
        Holidays = value.Holidays.Take(maxItems).ToArray()
    };

    private static List<HolidayEntry> ParseHolidayArray(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<HolidayEntry>();
        foreach (var item in root.EnumerateArray())
        {
            var dateText = item.TryGetProperty("date", out var d) ? (d.GetString() ?? "") : "";
            if (!DateOnly.TryParse(dateText, out var date))
                continue;

            var localName = item.TryGetProperty("localName", out var ln) ? (ln.GetString() ?? "") : "";
            var name = item.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
            var countryCode = item.TryGetProperty("countryCode", out var cc) ? (cc.GetString() ?? "") : "";
            var isGlobal = item.TryGetProperty("global", out var g) &&
                           (g.ValueKind == JsonValueKind.True || g.ValueKind == JsonValueKind.False) &&
                           g.GetBoolean();

            int? launchYear = null;
            if (item.TryGetProperty("launchYear", out var ly) &&
                ly.ValueKind == JsonValueKind.Number &&
                ly.TryGetInt32(out var parsedLy))
            {
                launchYear = parsedLy;
            }

            var counties = item.TryGetProperty("counties", out var cts) && cts.ValueKind == JsonValueKind.Array
                ? cts.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => (x.GetString() ?? "").Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray()
                : [];

            var types = item.TryGetProperty("types", out var tps) && tps.ValueKind == JsonValueKind.Array
                ? tps.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => (x.GetString() ?? "").Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray()
                : [];

            items.Add(new HolidayEntry
            {
                Date = date,
                LocalName = localName,
                Name = name,
                CountryCode = countryCode,
                Global = isGlobal,
                LaunchYear = launchYear,
                Counties = counties,
                Types = types
            });
        }

        return items;
    }

    private static bool MatchesRegion(HolidayEntry holiday, string? regionCode)
    {
        if (string.IsNullOrWhiteSpace(regionCode))
            return true;

        if (holiday.Global || holiday.Counties.Count == 0)
            return true;

        return holiday.Counties.Any(c =>
            string.Equals(c, regionCode, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeCountryCode(string countryCode)
    {
        var raw = (countryCode ?? "").Trim().ToUpperInvariant();
        if (raw.Length == 5 && raw[2] == '-')
            raw = raw[..2];

        if (raw.Length == 2 && raw.All(char.IsLetter))
            return raw;

        throw new ArgumentException("countryCode must be a 2-letter ISO code.", nameof(countryCode));
    }

    private static string? NormalizeRegionCode(string? regionCode, string countryCode)
    {
        var raw = (regionCode ?? "").Trim().ToUpperInvariant().Replace('_', '-');
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (raw.Length == 2 && raw.All(char.IsLetter))
            return $"{countryCode}-{raw}";

        if (raw.Length >= 5 && raw.Contains('-', StringComparison.Ordinal))
            return raw;

        return null;
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
