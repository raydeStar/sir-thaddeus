using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SirThaddeus.WebSearch.Providers;

public sealed class OsmPlacesDiscoveryProvider : IPlacesDiscoveryProvider, IDisposable
{
    private const string DefaultUserAgent = "SirThaddeusCopilot/1.0 (contact: local-runtime@localhost)";

    private static readonly string[] DefaultOverpassEndpoints =
    [
        "https://overpass-api.de/api/interpreter",
        "https://overpass.kumi.systems/api/interpreter",
        "https://lz4.overpass-api.de/api/interpreter"
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Key, string Value)>> CategoryTagMap =
        new Dictionary<string, IReadOnlyList<(string Key, string Value)>>(StringComparer.OrdinalIgnoreCase)
        {
            ["bakery"] = [("shop", "bakery")],
            ["florist"] = [("shop", "florist")],
            ["restaurant"] = [("amenity", "restaurant")],
            ["coffee shop"] = [("amenity", "cafe"), ("shop", "coffee")],
            ["deli"] = [("shop", "deli")],
            ["grocery store"] = [("shop", "supermarket"), ("shop", "greengrocer"), ("shop", "convenience")],
            ["bank"] = [("amenity", "bank")],
            ["park"] = [("leisure", "park"), ("leisure", "nature_reserve"), ("leisure", "playground")],
            ["salon"] = [("shop", "hairdresser"), ("shop", "beauty")],
            ["pharmacy"] = [("amenity", "pharmacy"), ("shop", "chemist")],
            ["dentist"] = [("amenity", "dentist")]
        };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly WeatherService _weatherService;
    private readonly bool _ownsWeatherService;
    private readonly TimeProvider _timeProvider;
    private readonly string[] _overpassEndpoints;
    private readonly TimeSpan _cacheTtl;
    private readonly ConcurrentDictionary<string, CacheEntry<PlacesDiscoveryResult>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public OsmPlacesDiscoveryProvider(
        HttpClient? httpClient = null,
        WeatherService? weatherService = null,
        TimeProvider? timeProvider = null,
        IEnumerable<string>? overpassEndpoints = null,
        TimeSpan? cacheTtl = null)
    {
        _http = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _weatherService = weatherService ?? new WeatherService(
            new WeatherServiceOptions { UserAgent = DefaultUserAgent },
            httpClient: _http,
            timeProvider: timeProvider ?? TimeProvider.System);
        _ownsWeatherService = weatherService is null;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _overpassEndpoints = (overpassEndpoints ?? DefaultOverpassEndpoints)
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
            .Select(endpoint => endpoint.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(10);

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
    }

    public string Name => "osm_overpass";

    public async Task<PlacesDiscoveryResult> DiscoverAsync(
        string query,
        string? userLocationHint,
        PlaceDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PlaceDiscoveryOptions();
        var normalizedQuery = (query ?? string.Empty).Trim();
        var resolvedLocationHint = ResolveLocationHint(normalizedQuery, userLocationHint);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return BuildErrorResult(normalizedQuery, resolvedLocationHint, options, "A place discovery query is required.");
        }

        if (string.IsNullOrWhiteSpace(resolvedLocationHint))
        {
            return BuildErrorResult(normalizedQuery, resolvedLocationHint, options, "A city or location hint is required for nearby place discovery.");
        }

        var category = ResolveCategory(normalizedQuery);
        if (string.IsNullOrWhiteSpace(category) || !CategoryTagMap.TryGetValue(category, out var tagFilters))
        {
            return BuildErrorResult(normalizedQuery, resolvedLocationHint, options, "This nearby place category is not supported by the open places provider yet.");
        }

        var cacheKey = BuildCacheKey(normalizedQuery, resolvedLocationHint, category, options);
        var now = _timeProvider.GetUtcNow();
        if (TryGetFresh(cacheKey, now, out var cached, out var ageSeconds))
        {
            return cached with
            {
                Cache = new PlacesCacheMetadata { Hit = true, AgeSeconds = ageSeconds }
            };
        }

        var geocode = await _weatherService.GeocodeAsync(resolvedLocationHint, maxResults: 1, cancellationToken);
        var center = geocode.Results.FirstOrDefault();
        if (center is null)
        {
            var noLocation = BuildErrorResult(normalizedQuery, resolvedLocationHint, options, "The open places provider could not resolve that location.");
            _cache[cacheKey] = new CacheEntry<PlacesDiscoveryResult>(noLocation, now);
            return noLocation;
        }

        var errors = new List<string>();
        foreach (var endpoint in _overpassEndpoints)
        {
            try
            {
                var result = await RetryHelper.ExecuteAsync(
                    () => QueryEndpointAsync(endpoint, normalizedQuery, resolvedLocationHint, category, center, tagFilters, options, cancellationToken),
                    cancellationToken,
                    maxRetries: 1,
                    backoffMs: 350);

                var normalized = result with
                {
                    Cache = new PlacesCacheMetadata { Hit = false, AgeSeconds = 0 },
                    Errors = errors
                };

                _cache[cacheKey] = new CacheEntry<PlacesDiscoveryResult>(normalized, now);
                return normalized;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{endpoint}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var failed = new PlacesDiscoveryResult
        {
            Provider = Name,
            Query = normalizedQuery,
            UserLocationHint = resolvedLocationHint,
            ResolvedLocation = center.Name,
            Center = new PlacesDiscoveryCenter
            {
                Label = center.Name,
                Latitude = center.Latitude,
                Longitude = center.Longitude
            },
            Options = options,
            Results = [],
            Errors = errors,
            Cache = new PlacesCacheMetadata { Hit = false, AgeSeconds = 0 }
        };

        _cache[cacheKey] = new CacheEntry<PlacesDiscoveryResult>(failed, now);
        return failed;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        foreach (var endpoint in _overpassEndpoints)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Replace("/interpreter", "/status", StringComparison.OrdinalIgnoreCase));
                using var response = await _http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Try the next endpoint.
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_ownsWeatherService)
            _weatherService.Dispose();
        if (_ownsHttpClient)
            _http.Dispose();
    }

    private async Task<PlacesDiscoveryResult> QueryEndpointAsync(
        string endpoint,
        string query,
        string userLocationHint,
        string category,
        GeocodeCandidate center,
        IReadOnlyList<(string Key, string Value)> tagFilters,
        PlaceDiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                BuildOverpassQuery(center.Latitude, center.Longitude, options.RadiusMeters, tagFilters),
                Encoding.UTF8,
                "application/x-www-form-urlencoded")
        };

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(payload);
        var candidates = ParseCandidates(doc.RootElement, category, center, options.MaxResults);

        return new PlacesDiscoveryResult
        {
            Provider = Name,
            Query = query,
            UserLocationHint = userLocationHint,
            ResolvedLocation = center.Name,
            Center = new PlacesDiscoveryCenter
            {
                Label = center.Name,
                Latitude = center.Latitude,
                Longitude = center.Longitude
            },
            Options = options,
            Results = candidates,
            Errors = []
        };
    }

    private static string BuildOverpassQuery(
        double latitude,
        double longitude,
        int radiusMeters,
        IReadOnlyList<(string Key, string Value)> tagFilters)
    {
        var lat = latitude.ToString("F6", CultureInfo.InvariantCulture);
        var lon = longitude.ToString("F6", CultureInfo.InvariantCulture);
        var radius = Math.Clamp(radiusMeters, 500, 20_000);

        var sb = new StringBuilder();
        sb.Append("data=");
        sb.Append(Uri.EscapeDataString("[out:json][timeout:20];("));

        foreach (var filter in tagFilters)
        {
            var clause = $"node(around:{radius},{lat},{lon})[\"{filter.Key}\"=\"{filter.Value}\"];way(around:{radius},{lat},{lon})[\"{filter.Key}\"=\"{filter.Value}\"];relation(around:{radius},{lat},{lon})[\"{filter.Key}\"=\"{filter.Value}\"];";
            sb.Append(Uri.EscapeDataString(clause));
        }

        sb.Append(Uri.EscapeDataString(");out center tags;"));
        return sb.ToString();
    }

    private static IReadOnlyList<PlaceDiscoveryCandidate> ParseCandidates(
        JsonElement root,
        string category,
        GeocodeCandidate center,
        int maxResults)
    {
        if (!root.TryGetProperty("elements", out var elements) ||
            elements.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<PlaceDiscoveryCandidate>();

        foreach (var element in elements.EnumerateArray())
        {
            var tags = ReadTags(element);
            if (!tags.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
                continue;

            if (!TryGetCoordinates(element, out var latitude, out var longitude))
                continue;

            var dedupeKey = $"{name.Trim().ToLowerInvariant()}|{Math.Round(latitude, 4)}|{Math.Round(longitude, 4)}";
            if (!dedupe.Add(dedupeKey))
                continue;

            var distanceMeters = ComputeDistanceMeters(center.Latitude, center.Longitude, latitude, longitude);
            var osmUrl = BuildOsmUrl(element, latitude, longitude);
            var address = BuildAddress(tags);
            results.Add(new PlaceDiscoveryCandidate
            {
                Id = BuildId(element),
                Name = name.Trim(),
                Address = address,
                Category = category,
                Latitude = latitude,
                Longitude = longitude,
                DistanceMeters = distanceMeters,
                OsmUrl = osmUrl,
                Tags = tags
            });
        }

        return results
            .OrderBy(result => result.DistanceMeters ?? int.MaxValue)
            .ThenBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxResults, 1, 20))
            .ToList();
    }

    private static Dictionary<string, string> ReadTags(JsonElement element)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!element.TryGetProperty("tags", out var tagsElement) ||
            tagsElement.ValueKind != JsonValueKind.Object)
        {
            return tags;
        }

        foreach (var property in tagsElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                tags[property.Name] = property.Value.GetString()!;
            }
        }

        return tags;
    }

    private static bool TryGetCoordinates(JsonElement element, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;

        if (element.TryGetProperty("lat", out var latElement) &&
            element.TryGetProperty("lon", out var lonElement) &&
            latElement.TryGetDouble(out latitude) &&
            lonElement.TryGetDouble(out longitude))
        {
            return true;
        }

        if (element.TryGetProperty("center", out var centerElement) &&
            centerElement.ValueKind == JsonValueKind.Object &&
            centerElement.TryGetProperty("lat", out latElement) &&
            centerElement.TryGetProperty("lon", out lonElement) &&
            latElement.TryGetDouble(out latitude) &&
            lonElement.TryGetDouble(out longitude))
        {
            return true;
        }

        return false;
    }

    private static string BuildId(JsonElement element)
    {
        var type = element.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : "element";
        var id = element.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var numericId)
            ? numericId.ToString(CultureInfo.InvariantCulture)
            : Guid.NewGuid().ToString("N");
        return $"{type}:{id}";
    }

    private static string BuildOsmUrl(JsonElement element, double latitude, double longitude)
    {
        var type = element.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString() ?? "node"
            : "node";
        if (element.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var numericId))
            return $"https://www.openstreetmap.org/{type}/{numericId}";

        return $"https://www.openstreetmap.org/?mlat={latitude.ToString("F6", CultureInfo.InvariantCulture)}&mlon={longitude.ToString("F6", CultureInfo.InvariantCulture)}#map=18/{latitude.ToString("F6", CultureInfo.InvariantCulture)}/{longitude.ToString("F6", CultureInfo.InvariantCulture)}";
    }

    private static string BuildAddress(IReadOnlyDictionary<string, string> tags)
    {
        var streetParts = new List<string>();
        if (tags.TryGetValue("addr:housenumber", out var houseNumber) && !string.IsNullOrWhiteSpace(houseNumber))
            streetParts.Add(houseNumber.Trim());
        if (tags.TryGetValue("addr:street", out var street) && !string.IsNullOrWhiteSpace(street))
            streetParts.Add(street.Trim());

        var localityParts = new List<string>();
        if (tags.TryGetValue("addr:city", out var city) && !string.IsNullOrWhiteSpace(city))
            localityParts.Add(city.Trim());
        if (tags.TryGetValue("addr:state", out var state) && !string.IsNullOrWhiteSpace(state))
            localityParts.Add(state.Trim());
        if (tags.TryGetValue("addr:postcode", out var postcode) && !string.IsNullOrWhiteSpace(postcode))
            localityParts.Add(postcode.Trim());

        var parts = new List<string>();
        if (streetParts.Count > 0)
            parts.Add(string.Join(" ", streetParts));
        if (localityParts.Count > 0)
            parts.Add(string.Join(", ", localityParts));

        return string.Join(", ", parts);
    }

    private static int ComputeDistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusMeters = 6_371_000;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Pow(Math.Sin(dLat / 2), 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) * Math.Pow(Math.Sin(dLon / 2), 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return (int)Math.Round(earthRadiusMeters * c);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private static string? ResolveCategory(string query)
    {
        var lower = query.ToLowerInvariant();
        if (lower.Contains("bakery", StringComparison.Ordinal) || lower.Contains("bakeries", StringComparison.Ordinal))
            return "bakery";
        if (lower.Contains("florist", StringComparison.Ordinal) || lower.Contains("florists", StringComparison.Ordinal))
            return "florist";
        if (lower.Contains("restaurant", StringComparison.Ordinal) || lower.Contains("restaurants", StringComparison.Ordinal))
            return "restaurant";
        if (lower.Contains("coffee", StringComparison.Ordinal) || lower.Contains("cafe", StringComparison.Ordinal))
            return "coffee shop";
        if (lower.Contains("deli", StringComparison.Ordinal) || lower.Contains("delis", StringComparison.Ordinal))
            return "deli";
        if (lower.Contains("grocery", StringComparison.Ordinal) || lower.Contains("supermarket", StringComparison.Ordinal))
            return "grocery store";
        if (lower.Contains("bank", StringComparison.Ordinal) || lower.Contains("banks", StringComparison.Ordinal) || lower.Contains("credit union", StringComparison.Ordinal))
            return "bank";
        if (lower.Contains("park", StringComparison.Ordinal) || lower.Contains("parks", StringComparison.Ordinal) || lower.Contains("playground", StringComparison.Ordinal))
            return "park";
        if (lower.Contains("salon", StringComparison.Ordinal))
            return "salon";
        if (lower.Contains("pharmacy", StringComparison.Ordinal))
            return "pharmacy";
        if (lower.Contains("dentist", StringComparison.Ordinal))
            return "dentist";
        return null;
    }

    private static string? ResolveLocationHint(string query, string? userLocationHint)
    {
        if (!string.IsNullOrWhiteSpace(userLocationHint) && !IsGenericLocationHint(userLocationHint))
            return userLocationHint.Trim();

        var lower = query.ToLowerInvariant();
        var marker = lower.LastIndexOf(" in ", StringComparison.Ordinal);
        if (marker >= 0)
            return NormalizeLocationHintCandidate(query[(marker + 4)..]);

        marker = lower.LastIndexOf(" near ", StringComparison.Ordinal);
        if (marker >= 0)
            return NormalizeLocationHintCandidate(query[(marker + 6)..]);

        return null;
    }

    private static string? NormalizeLocationHintCandidate(string value)
    {
        var candidate = value.Trim().TrimEnd('?', '.', '!');
        return IsGenericLocationHint(candidate) ? null : candidate;
    }

    private static bool IsGenericLocationHint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "me" or "near me" or "nearby" or "here" or "around here" or "my area" or "current location" or "this area" or "local";
    }

    private PlacesDiscoveryResult BuildErrorResult(
        string query,
        string? userLocationHint,
        PlaceDiscoveryOptions options,
        string error)
    {
        return new PlacesDiscoveryResult
        {
            Provider = Name,
            Query = query,
            UserLocationHint = userLocationHint ?? string.Empty,
            ResolvedLocation = userLocationHint ?? string.Empty,
            Options = options,
            Results = [],
            Errors = [error],
            Cache = new PlacesCacheMetadata { Hit = false, AgeSeconds = 0 }
        };
    }

    private string BuildCacheKey(
        string query,
        string userLocationHint,
        string category,
        PlaceDiscoveryOptions options)
    {
        return string.Join("|",
            query.Trim().ToLowerInvariant(),
            userLocationHint.Trim().ToLowerInvariant(),
            category.Trim().ToLowerInvariant(),
            Math.Clamp(options.MaxResults, 1, 20),
            Math.Clamp(options.RadiusMeters, 500, 20_000));
    }

    private bool TryGetFresh(
        string cacheKey,
        DateTimeOffset now,
        out PlacesDiscoveryResult result,
        out int ageSeconds)
    {
        result = new PlacesDiscoveryResult();
        ageSeconds = 0;

        if (!_cache.TryGetValue(cacheKey, out var entry))
            return false;

        var age = now - entry.CreatedAt;
        if (age > _cacheTtl)
            return false;

        result = entry.Value;
        ageSeconds = (int)Math.Max(0, age.TotalSeconds);
        return true;
    }

    private sealed record CacheEntry<T>(T Value, DateTimeOffset CreatedAt);
}