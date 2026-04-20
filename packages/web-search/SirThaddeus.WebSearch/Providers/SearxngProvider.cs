using System.Text.Json;
using Serilog;

namespace SirThaddeus.WebSearch.Providers;

// ─────────────────────────────────────────────────────────────────────────
// SearxNG Provider
//
// Optional power-user backend. Uses SearxNG's JSON API for high-quality,
// privacy-focused meta-search. Requires the user to run a SearxNG
// instance (Docker or native install).
//
// Auto-detected by the WebSearchRouter via health check.
// ─────────────────────────────────────────────────────────────────────────

public sealed class SearxngProvider : IWebSearchProvider, IDisposable
{
    private readonly string _baseUrl;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SearxngProvider(string baseUrl, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = httpClient ?? new HttpClient();

        // Generous ceiling — per-request timeouts are enforced via
        // CancellationTokenSource from WebSearchOptions.TimeoutMs.
        // This just prevents leaked HttpClient instances from hanging.
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public string Name => "SearxNG";

    public async Task<SearchResults> SearchAsync(
        string query,
        WebSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new SearchResults { Provider = Name, Errors = ["Empty query"] };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(options.TimeoutMs);

            var encodedQuery = Uri.EscapeDataString(query);
            var url = $"{_baseUrl}/search?q={encodedQuery}&format=json";

            // SearxNG supports time_range: day, week, month, year
            var timeRange = MapRecencyToSearxng(options.Recency);
            if (timeRange is not null)
                url += $"&time_range={timeRange}";

            if (!string.IsNullOrWhiteSpace(options.Categories))
                url += $"&categories={Uri.EscapeDataString(options.Categories)}";

            var parsed = await RetryHelper.ExecuteAsync(async () =>
            {
                var response = await _http.GetAsync(url, cts.Token);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cts.Token);

                // SearxNG returns application/json — if we get HTML,
                // an upstream engine likely served a CAPTCHA page through.
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                    || json.TrimStart().StartsWith('<'))
                {
                    throw new InvalidOperationException(
                        "SearxNG returned HTML instead of JSON — upstream CAPTCHA or block page likely");
                }

                return JsonSerializer.Deserialize<SearxngResponse>(json, JsonOpts);
            }, cancellationToken);

            if (parsed?.Results is null)
                return new SearchResults { Provider = Name, Errors = ["No results in response"] };

            var results = parsed.Results
                .Take(options.MaxResults)
                .Select(r => new SearchResult
                {
                    Title = r.Title ?? "(untitled)",
                    Url = r.Url ?? "",
                    Snippet = r.Content ?? "",
                    Source = ExtractDomain(r.Url)
                })
                .Where(r => !string.IsNullOrEmpty(r.Url))
                .ToList();

            // SearxNG was reachable but its upstream engines returned
            // nothing — often caused by CAPTCHAs or rate-limits.
            var errors = new List<string>();
            if (results.Count == 0 && parsed.Results.Count == 0)
                errors.Add("SearxNG returned 0 results — upstream engines may be rate-limited or CAPTCHA-blocked");

            return new SearchResults
            {
                Results = results,
                Provider = Name,
                Errors = errors
            };
        }
        catch (OperationCanceledException)
        {
            return new SearchResults { Provider = Name, Errors = ["Search timed out"] };
        }
        catch (HttpRequestException ex)
        {
            return new SearchResults { Provider = Name, Errors = [$"HTTP error: {ex.Message}"] };
        }
        catch (Exception ex)
        {
            return new SearchResults { Provider = Name, Errors = [$"Error: {ex.Message}"] };
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        // Two attempts — the first cold-start request from a freshly spawned
        // process can be slow on Windows due to IPv6 DNS resolution for
        // "localhost" before falling back to IPv4 127.0.0.1.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(4_000);

                var response = await _http.GetAsync(_baseUrl, cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                if (attempt == 0)
                    await Task.Delay(500, cancellationToken);
            }
        }

        return false;
    }

    /// <summary>
    /// Maps our normalized recency token to SearxNG's time_range param.
    /// Returns null for "any" (no filtering).
    /// </summary>
    private static string? MapRecencyToSearxng(string recency) => recency switch
    {
        "day" => "day",
        "week" => "week",
        "month" => "month",
        _ => null
    };

    private static string ExtractDomain(string? url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        try { return new Uri(url).Host.Replace("www.", ""); }
        catch (Exception ex)
        {
            Log.ForContext<SearxngProvider>()
                .Debug(ex, "Could not parse URL into a Uri while extracting domain: {Url}", url);
            return string.Empty;
        }
    }

    public void Dispose() => _http.Dispose();

    // ─────────────────────────────────────────────────────────────────
    // SearxNG JSON Response DTOs
    // ─────────────────────────────────────────────────────────────────

    private sealed record SearxngResponse
    {
        public List<SearxngResult>? Results { get; init; }
    }

    private sealed record SearxngResult
    {
        public string? Title { get; init; }
        public string? Url { get; init; }
        public string? Content { get; init; }
    }
}
