using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SirThaddeus.WebSearch.Providers;

/// <summary>
/// Optional hosted search fallback backed by SearchApi's Google Search API.
/// Only activates when a key is configured.
/// </summary>
public sealed class SearchApiProvider : IWebSearchProvider, IDisposable
{
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _engine;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SearchApiProvider(
        string apiKey,
        string baseUrl = "https://www.searchapi.io/api/v1/search",
        string engine = "google",
        HttpClient? httpClient = null)
    {
        _apiKey = (apiKey ?? "").Trim();
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? "https://www.searchapi.io/api/v1/search"
            : baseUrl.Trim();
        _engine = string.IsNullOrWhiteSpace(engine)
            ? "google"
            : engine.Trim().ToLowerInvariant();
        _http = httpClient ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public string Name => "SearchApi";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<SearchResults> SearchAsync(
        string query,
        WebSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new SearchResults { Provider = Name, Errors = ["Empty query"] };

        if (!IsConfigured)
        {
            return new SearchResults
            {
                Provider = Name,
                Errors = ["Search API key is not configured"]
            };
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(options.TimeoutMs);

            var parsed = await RetryHelper.ExecuteAsync(async () =>
            {
                using var request = BuildRequest(query, options);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cts.Token);
                return JsonSerializer.Deserialize<SearchApiResponse>(json, JsonOpts);
            }, cancellationToken);

            var results = parsed?.OrganicResults?
                .Where(r => !string.IsNullOrWhiteSpace(r.Link))
                .Take(options.MaxResults)
                .Select(r => new SearchResult
                {
                    Title = string.IsNullOrWhiteSpace(r.Title) ? "(untitled)" : r.Title!,
                    Url = r.Link!,
                    Snippet = r.Snippet ?? "",
                    Source = !string.IsNullOrWhiteSpace(r.Domain)
                        ? r.Domain!
                        : ExtractDomain(r.Link)
                })
                .ToList() ?? [];

            return new SearchResults
            {
                Provider = Name,
                Results = results
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

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(IsConfigured);

    public void Dispose() => _http.Dispose();

    private HttpRequestMessage BuildRequest(string query, WebSearchOptions options)
    {
        var parameters = new List<string>
        {
            $"engine={Uri.EscapeDataString(_engine)}",
            $"q={Uri.EscapeDataString(query)}",
            $"num={Math.Clamp(options.MaxResults, 1, 10)}"
        };

        var timePeriod = MapRecencyToTimePeriod(options.Recency);
        if (!string.IsNullOrWhiteSpace(timePeriod))
            parameters.Add($"time_period={Uri.EscapeDataString(timePeriod)}");

        var separator = _baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + separator + string.Join("&", parameters));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return request;
    }

    private static string? MapRecencyToTimePeriod(string recency) => recency switch
    {
        "day" => "last_day",
        "week" => "last_week",
        "month" => "last_month",
        _ => null
    };

    private static string ExtractDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";

        try
        {
            return new Uri(url).Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return "";
        }
    }

    private sealed record SearchApiResponse
    {
        [JsonPropertyName("organic_results")]
        public List<SearchApiOrganicResult>? OrganicResults { get; init; }
    }

    private sealed record SearchApiOrganicResult
    {
        public string? Title { get; init; }
        public string? Link { get; init; }
        public string? Snippet { get; init; }
        public string? Domain { get; init; }
    }
}
