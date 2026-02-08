using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace SirThaddeus.WebSearch.Providers;

// ─────────────────────────────────────────────────────────────────────────
// DuckDuckGo HTML Provider
//
// Zero-install default. Fetches the DDG lite HTML results page and scrapes
// titles, URLs, and snippets. No API key required.
//
// Limitations:
//   - May be rate-limited or serve a CAPTCHA on high volume.
//   - HTML structure may change without notice (handle gracefully).
//   - Respects a minimum 1-second delay between requests.
// ─────────────────────────────────────────────────────────────────────────

public sealed class DuckDuckGoHtmlProvider : IWebSearchProvider, IDisposable
{
    private const string SearchUrl = "https://html.duckduckgo.com/html/";
    private static readonly SemaphoreSlim RateLimiter = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;

    private readonly HttpClient _http;

    public DuckDuckGoHtmlProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();

        // Generous ceiling — per-request timeouts are enforced via
        // CancellationTokenSource from WebSearchOptions.TimeoutMs.
        _http.Timeout = TimeSpan.FromSeconds(30);

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
    }

    public string Name => "DuckDuckGo";

    public async Task<SearchResults> SearchAsync(
        string query,
        WebSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new SearchResults { Provider = Name, Errors = ["Empty query"] };

        await EnforceRateLimitAsync(cancellationToken);

        try
        {
            // POST form-encoded query to the DDG HTML endpoint.
            // DDG accepts df (date filter): d = past day, w = past week, m = past month.
            var formFields = new Dictionary<string, string> { ["q"] = query };

            var dfValue = MapRecencyToDdg(options.Recency);
            if (dfValue is not null)
                formFields["df"] = dfValue;

            var content = new FormUrlEncodedContent(formFields);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(options.TimeoutMs);

            var results = await RetryHelper.ExecuteAsync(async () =>
            {
                var response = await _http.PostAsync(SearchUrl, content, cts.Token);
                response.EnsureSuccessStatusCode();

                var html = await response.Content.ReadAsStringAsync(cts.Token);
                return ParseResults(html, options.MaxResults);
            }, cancellationToken);

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
            return new SearchResults { Provider = Name, Errors = [$"Parse error: {ex.Message}"] };
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(3_000);

            var response = await _http.GetAsync("https://html.duckduckgo.com/", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // HTML Parsing
    // ─────────────────────────────────────────────────────────────────

    private static List<SearchResult> ParseResults(string html, int maxResults)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<SearchResult>();

        // DDG HTML uses .result class for each search result block
        var resultNodes = doc.DocumentNode.SelectNodes(
            "//div[contains(@class, 'result')]");

        if (resultNodes is null)
            return results;

        foreach (var node in resultNodes)
        {
            if (results.Count >= maxResults)
                break;

            // Title + URL from the primary result link
            var titleNode = node.SelectSingleNode(
                ".//a[contains(@class, 'result__a')]");

            if (titleNode is null)
                continue;

            var title = CleanText(titleNode.InnerText);
            var href  = titleNode.GetAttributeValue("href", "");

            // Resolve DDG redirect URLs
            var url = ResolveUrl(href);
            if (string.IsNullOrEmpty(url))
                continue;

            // Snippet text
            var snippetNode = node.SelectSingleNode(
                ".//a[contains(@class, 'result__snippet')]")
                ?? node.SelectSingleNode(".//td[contains(@class, 'result-snippet')]");

            var snippet = snippetNode is not null
                ? CleanText(snippetNode.InnerText)
                : string.Empty;

            // Domain
            var source = ExtractDomain(url);

            results.Add(new SearchResult
            {
                Title   = title,
                Url     = url,
                Snippet = snippet,
                Source  = source
            });
        }

        return results;
    }

    /// <summary>
    /// DDG wraps result URLs in redirect links. Extract the actual target.
    /// </summary>
    private static string ResolveUrl(string href)
    {
        if (string.IsNullOrEmpty(href))
            return string.Empty;

        // Direct URL (no redirect wrapper)
        if (href.StartsWith("http://") || href.StartsWith("https://"))
            return href;

        // DDG redirect format: //duckduckgo.com/l/?uddg=https%3A%2F%2Factual-url
        if (href.Contains("uddg="))
        {
            var match = Regex.Match(href, @"uddg=([^&]+)");
            if (match.Success)
                return WebUtility.UrlDecode(match.Groups[1].Value);
        }

        return string.Empty;
    }

    private static string ExtractDomain(string url)
    {
        try
        {
            return new Uri(url).Host.Replace("www.", "");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string CleanText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var decoded = WebUtility.HtmlDecode(text);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    // ─────────────────────────────────────────────────────────────────
    // Rate Limiting (1 request per second minimum)
    // ─────────────────────────────────────────────────────────────────

    private static async Task EnforceRateLimitAsync(CancellationToken ct)
    {
        await RateLimiter.WaitAsync(ct);
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequest;
            if (elapsed < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(TimeSpan.FromSeconds(1) - elapsed, ct);
            }
            _lastRequest = DateTime.UtcNow;
        }
        finally
        {
            RateLimiter.Release();
        }
    }

    /// <summary>
    /// Maps our normalized recency token to DDG's df parameter.
    /// Returns null for "any" (no filtering).
    /// </summary>
    private static string? MapRecencyToDdg(string recency) => recency switch
    {
        "day"   => "d",
        "week"  => "w",
        "month" => "m",
        _       => null
    };

    public void Dispose()
    {
        _http.Dispose();
    }
}
