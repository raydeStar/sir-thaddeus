using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SirThaddeus.WebSearch;
using SirThaddeus.WebSearch.Providers;

namespace SirThaddeus.McpServer.Tools;

/// <summary>
/// Places lookup tools for deep-dive briefings.
/// All external HTTP stays in MCP to preserve the trust boundary.
/// </summary>
[McpServerToolType]
public static class PlacesTools
{
    private static readonly Lazy<HttpClient> SharedHttp = new(CreateHttpClient);
    private static readonly Lazy<IPlacesDiscoveryProvider> OpenDiscoveryProvider =
        new(() => new OsmPlacesDiscoveryProvider(httpClient: SharedHttp.Value));

    [McpServerTool, Description(
        "Discovers nearby businesses and places using open OSM geocoding + Overpass data. " +
        "Works without paid API keys and returns structured nearby place candidates.")]
    public static async Task<string> PlacesDiscover(
        [Description("Nearby place query, such as 'bakeries nearby' or 'parks in Olympia'.")] string query,
        [Description("Optional user location hint (city/region).")]
        string? userLocationHint = null,
        [Description("Maximum number of place candidates to return (1-20).")]
        int maxResults = 10,
        [Description("Search radius in meters around the resolved location (500-20000).")]
        int radiusMeters = 4_000,
        [Description("Locale hint for future formatting support.")]
        string locale = "en-US",
        CancellationToken cancellationToken = default)
    {
        var cacheArgs = new
        {
            query,
            userLocationHint = userLocationHint ?? string.Empty,
            maxResults,
            radiusMeters,
            locale = locale ?? "en-US"
        };
        var cached = await ToolResultCache.GetAsync<string>("places_discover", cacheArgs);
        if (!string.IsNullOrWhiteSpace(cached))
            return cached;

        var options = new PlaceDiscoveryOptions
        {
            MaxResults = Math.Clamp(maxResults, 1, 20),
            RadiusMeters = Math.Clamp(radiusMeters, 500, 20_000),
            Locale = string.IsNullOrWhiteSpace(locale) ? "en-US" : locale.Trim()
        };

        var result = await OpenDiscoveryProvider.Value.DiscoverAsync(
            query,
            userLocationHint,
            options,
            cancellationToken);

        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        var response = JsonSerializer.Serialize(result, serializerOptions);
        await ToolResultCache.SetAsync("places_discover", cacheArgs, response, ToolResultCache.ResolvePlacesAndHolidaysTtl());
        return response;
    }

    [McpServerTool, Description(
        "Looks up a place via Google Places API and returns structured details " +
        "(hours, reviews, address, links, and coordinates) for deep-dive briefings.")]
    public static async Task<string> PlacesLookup(
        [Description("Place query (business or venue name).")] string query,
        [Description("Timezone context (IANA or Windows ID).")] string timezone = "unknown",
        [Description("Locale hint (for example en-US).")] string locale = "en-US",
        [Description("Optional user location hint (city/region).")] string? userLocationHint = null,
        [Description("Maximum review snippets to include (1-5).")] int maxReviewSnippets = 3,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return SerializeError("Query is required.", query);

        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return SerializeError("Google Places API key is not configured.", query);

        var timeoutMs = ParseIntEnv("ST_DEEPDIVE_PLACES_TIMEOUT_MS", fallback: 8_000, min: 2_000, max: 20_000);
        var reviewLimit = Math.Clamp(maxReviewSnippets, 1, 5);
        var language = NormalizeLanguage(locale);
        var nowIso = DateTimeOffset.UtcNow.ToString("O");

        var cacheArgs = new
        {
            query,
            timezone = timezone ?? "unknown",
            locale = locale ?? "en-US",
            userLocationHint = userLocationHint ?? string.Empty,
            maxReviewSnippets = reviewLimit
        };
        var cached = await ToolResultCache.GetAsync<string>("places_lookup", cacheArgs);
        if (!string.IsNullOrWhiteSpace(cached))
            return cached;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            var placeId = await FindPlaceIdAsync(query, language, apiKey!, linkedCts.Token);
            if (string.IsNullOrWhiteSpace(placeId))
                return SerializeError("No matching place was found for that query.", query);

            var details = await GetPlaceDetailsAsync(placeId, language, apiKey!, linkedCts.Token);
            if (details.error is not null)
                return SerializeError(details.error, query);

            var payload = new
            {
                provider = "google_places",
                query,
                timezone,
                locale,
                userLocationHint,
                fetchedAt = nowIso,
                error = (string?)null,
                place = details.place,
                sources = new[]
                {
                    new
                    {
                        name = "Google Places",
                        url = details.place?.DirectionsUrl ?? $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(query)}",
                        fetchedIso = nowIso
                    }
                }
            };

            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = false
            };
            var response = JsonSerializer.Serialize(payload, options);
            await ToolResultCache.SetAsync("places_lookup", cacheArgs, response, ToolResultCache.ResolvePlacesAndHolidaysTtl());
            return response;
        }
        catch (OperationCanceledException)
        {
            return SerializeError($"Places lookup timed out after {timeoutMs}ms.", query);
        }
        catch (Exception ex)
        {
            return SerializeError($"Places lookup failed: {ex.GetType().Name}: {ex.Message}", query);
        }

        async Task<string?> FindPlaceIdAsync(
            string inputQuery,
            string languageCode,
            string key,
            CancellationToken ct)
        {
            var findUrl =
                "https://maps.googleapis.com/maps/api/place/findplacefromtext/json" +
                $"?input={Uri.EscapeDataString(inputQuery)}" +
                "&inputtype=textquery" +
                "&fields=place_id,name,formatted_address" +
                $"&language={Uri.EscapeDataString(languageCode)}" +
                $"&key={Uri.EscapeDataString(key)}";

            using var response = await SharedHttp.Value.GetAsync(findUrl, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var statusEl) ? (statusEl.GetString() ?? "") : "";
            if (!status.Equals("OK", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!root.TryGetProperty("candidates", out var candidates) ||
                candidates.ValueKind != JsonValueKind.Array ||
                candidates.GetArrayLength() == 0)
            {
                return null;
            }

            var first = candidates[0];
            if (!first.TryGetProperty("place_id", out var placeIdEl) ||
                placeIdEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return placeIdEl.GetString();
        }

        async Task<(PlacesPlaceDetails? place, string? error)> GetPlaceDetailsAsync(
            string placeId,
            string languageCode,
            string key,
            CancellationToken ct)
        {
            var detailsUrl =
                "https://maps.googleapis.com/maps/api/place/details/json" +
                $"?place_id={Uri.EscapeDataString(placeId)}" +
                "&fields=place_id,name,formatted_address,formatted_phone_number,website,url,rating,user_ratings_total,opening_hours,reviews,geometry" +
                "&reviews_no_translations=true" +
                $"&language={Uri.EscapeDataString(languageCode)}" +
                $"&key={Uri.EscapeDataString(key)}";

            using var response = await SharedHttp.Value.GetAsync(detailsUrl, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return (null, $"Places details request failed with HTTP {(int)response.StatusCode}.");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var statusEl) ? (statusEl.GetString() ?? "") : "";
            if (!status.Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                var message = root.TryGetProperty("error_message", out var messageEl)
                    ? (messageEl.GetString() ?? status)
                    : status;
                return (null, $"Places details status was '{status}': {message}");
            }

            if (!root.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Object)
            {
                return (null, "Places details payload was missing result data.");
            }

            var weekdayText = new List<string>();
            bool? openNow = null;
            if (result.TryGetProperty("opening_hours", out var openingHours) &&
                openingHours.ValueKind == JsonValueKind.Object)
            {
                if (openingHours.TryGetProperty("open_now", out var openNowEl) &&
                    openNowEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    openNow = openNowEl.GetBoolean();
                }

                if (openingHours.TryGetProperty("weekday_text", out var weekdayEl) &&
                    weekdayEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in weekdayEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(item.GetString()))
                        {
                            weekdayText.Add(item.GetString()!);
                        }
                    }
                }
            }

            var reviews = new List<PlacesReviewSnippet>();
            if (result.TryGetProperty("reviews", out var reviewsEl) &&
                reviewsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var review in reviewsEl.EnumerateArray().Take(reviewLimit))
                {
                    reviews.Add(new PlacesReviewSnippet
                    {
                        Author = TryGetString(review, "author_name"),
                        Rating = TryGetDouble(review, "rating"),
                        Text = TryGetString(review, "text"),
                        RelativeTimeDescription = TryGetString(review, "relative_time_description")
                    });
                }
            }

            PlacesGeometry? geometry = null;
            if (result.TryGetProperty("geometry", out var geometryEl) &&
                geometryEl.ValueKind == JsonValueKind.Object &&
                geometryEl.TryGetProperty("location", out var locationEl) &&
                locationEl.ValueKind == JsonValueKind.Object)
            {
                var lat = TryGetDouble(locationEl, "lat");
                var lng = TryGetDouble(locationEl, "lng");
                if (lat.HasValue && lng.HasValue)
                {
                    geometry = new PlacesGeometry
                    {
                        Lat = lat.Value,
                        Lng = lng.Value
                    };
                }
            }

            var place = new PlacesPlaceDetails
            {
                PlaceId = TryGetString(result, "place_id"),
                Name = TryGetString(result, "name"),
                Address = TryGetString(result, "formatted_address"),
                Phone = TryGetString(result, "formatted_phone_number"),
                Website = TryGetString(result, "website"),
                DirectionsUrl = TryGetString(result, "url"),
                Rating = TryGetDouble(result, "rating"),
                UserRatingsTotal = TryGetInt(result, "user_ratings_total"),
                OpenNow = openNow,
                WeekdayText = PlacesNormalization.NormalizeWeekdayText(weekdayText),
                Reviews = reviews,
                Geometry = geometry
            };

            return (place, null);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SirThaddeusCopilot/1.0 (local-runtime)");
        return http;
    }

    private static string SerializeError(string message, string query)
    {
        return JsonSerializer.Serialize(new
        {
            provider = "google_places",
            query,
            fetchedAt = DateTimeOffset.UtcNow.ToString("O"),
            error = message,
            place = (object?)null,
            sources = Array.Empty<object>()
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string ResolveApiKey()
    {
        var preferred = Environment.GetEnvironmentVariable("ST_DEEPDIVE_PLACES_API_KEY");
        if (!string.IsNullOrWhiteSpace(preferred))
            return preferred.Trim();

        var fallback = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY");
        return string.IsNullOrWhiteSpace(fallback) ? "" : fallback.Trim();
    }

    private static string NormalizeLanguage(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return "en";
        var parts = locale.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "en" : parts[0].ToLowerInvariant();
    }

    private static int ParseIntEnv(string key, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (!int.TryParse(raw, out var parsed))
            return fallback;
        return Math.Clamp(parsed, min, max);
    }

    private static string TryGetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return "";
        return value.GetString() ?? "";
    }

    private static double? TryGetDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.TryGetDouble(out var number) ? number : null;
    }

    private static int? TryGetInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.TryGetInt32(out var number) ? number : null;
    }
}
