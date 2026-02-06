using System.Text.Json;

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
        _http.Timeout = TimeSpan.FromSeconds(15);
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

            var response = await _http.GetAsync(url, cts.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var parsed = JsonSerializer.Deserialize<SearxngResponse>(json, JsonOpts);

            if (parsed?.Results is null)
                return new SearchResults { Provider = Name, Errors = ["No results in response"] };

            var results = parsed.Results
                .Take(options.MaxResults)
                .Select(r => new SearchResult
                {
                    Title   = r.Title ?? "(untitled)",
                    Url     = r.Url ?? "",
                    Snippet = r.Content ?? "",
                    Source  = ExtractDomain(r.Url)
                })
                .Where(r => !string.IsNullOrEmpty(r.Url))
                .ToList();

            return new SearchResults
            {
                Results  = results,
                Provider = Name
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
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(2_000);

            var response = await _http.GetAsync(_baseUrl, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Maps our normalized recency token to SearxNG's time_range param.
    /// Returns null for "any" (no filtering).
    /// </summary>
    private static string? MapRecencyToSearxng(string recency) => recency switch
    {
        "day"   => "day",
        "week"  => "week",
        "month" => "month",
        _       => null
    };

    private static string ExtractDomain(string? url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        try { return new Uri(url).Host.Replace("www.", ""); }
        catch { return string.Empty; }
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
        public string? Title   { get; init; }
        public string? Url     { get; init; }
        public string? Content { get; init; }
    }
}
