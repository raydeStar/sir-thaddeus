using System.Net;
using System.Text.RegularExpressions;
using System.Xml;

namespace SirThaddeus.WebSearch.Providers;

// ─────────────────────────────────────────────────────────────────────────
// Google News RSS Provider
//
// Uses Google News's public RSS feed endpoints for reliable, automated
// news article discovery. No API keys, no scraping, no JavaScript.
//
// Two modes:
//   1. Search: https://news.google.com/rss/search?q={query}
//      Used for specific topics ("stock market crash", "earthquake Japan")
//
//   2. Top Headlines: https://news.google.com/rss
//      Used for generic "what's happening" queries. Returns individual
//      articles instead of cluster landing pages.
//
// URL Resolution:
//   Google News wraps real article URLs in redirect wrappers (CBMi...).
//   The real article URL is embedded in the <description> HTML as an
//   <a href="..."> tag. We extract it so downstream content extractors
//   can fetch the actual article, not the Google News SPA page.
//
// Results include clean article titles, real article URLs, source outlet
// names, and publication dates from the RSS metadata.
// ─────────────────────────────────────────────────────────────────────────

public sealed partial class GoogleNewsRssProvider : IWebSearchProvider, IDisposable
{
    private const string SearchUrl    = "https://news.google.com/rss/search";
    private const string HeadlinesUrl = "https://news.google.com/rss";
    private readonly HttpClient _http;

    // Generic queries that return junk from the search endpoint
    // but great results from the top headlines feed
    private static readonly string[] HeadlineTriggers =
    [
        "news", "headlines", "current events", "what's happening",
        "today", "breaking", "top stories", "latest", "recent news",
        "whats going on", "what is going on", "whats new", "news feed"
    ];

    public GoogleNewsRssProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();

        // Generous ceiling — per-request timeouts are enforced via
        // CancellationTokenSource from WebSearchOptions.TimeoutMs.
        _http.Timeout = TimeSpan.FromSeconds(30);

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        }
    }

    public string Name => "GoogleNews";

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

            // Pick the right feed: top headlines for generic queries,
            // search for specific topics
            var url = IsGenericNewsQuery(query)
                ? $"{HeadlinesUrl}?hl=en-US&gl=US&ceid=US:en"
                : $"{SearchUrl}?q={Uri.EscapeDataString(query)}&hl=en-US&gl=US&ceid=US:en";

            var results = await RetryHelper.ExecuteAsync(async () =>
            {
                var xml = await _http.GetStringAsync(url, cts.Token);
                return ParseRssResults(xml, options.MaxResults);
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
            cts.CancelAfter(5_000);

            var response = await _http.GetAsync(
                $"{HeadlinesUrl}?hl=en-US&gl=US&ceid=US:en", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Query Classification
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Detects generic "show me the news" queries that produce junk
    /// from the search endpoint but great individual articles from
    /// the top headlines feed.
    /// </summary>
    private static bool IsGenericNewsQuery(string query)
    {
        var normalized = query.Trim().ToLowerInvariant();

        return HeadlineTriggers.Any(trigger =>
            normalized.Equals(trigger, StringComparison.Ordinal) ||
            normalized.Contains(trigger, StringComparison.Ordinal));
    }

    // ─────────────────────────────────────────────────────────────────
    // RSS XML Parsing
    //
    // Google News RSS items contain:
    //   <title>Article Title - Source Name</title>
    //   <link>https://news.google.com/rss/articles/CBMi...</link>
    //   <description>&lt;a href="https://real-article-url.com/..."&gt;...
    //   <source url="https://sourcedomain.com">Source Name</source>
    //   <pubDate>Thu, 06 Feb 2025 12:00:00 GMT</pubDate>
    //
    // The <link> is a JS-redirect wrapper (useless for scraping).
    // The real article URL lives inside the <description> HTML.
    // ─────────────────────────────────────────────────────────────────

    private static List<SearchResult> ParseRssResults(string xml, int maxResults)
    {
        var results    = new List<SearchResult>();
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenUrls   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var items = doc.SelectNodes("//item");
            if (items is null) return results;

            foreach (XmlNode item in items)
            {
                if (results.Count >= maxResults) break;

                var title       = WebUtility.HtmlDecode(
                    item.SelectSingleNode("title")?.InnerText?.Trim() ?? "");
                var redirectUrl = item.SelectSingleNode("link")?.InnerText?.Trim() ?? "";
                var source      = item.SelectSingleNode("source")?.InnerText?.Trim() ?? "";
                var sourceUrl   = item.SelectSingleNode("source")?.Attributes?["url"]?.Value ?? "";
                var description = WebUtility.HtmlDecode(
                    item.SelectSingleNode("description")?.InnerText?.Trim() ?? "");
                var pubDate     = item.SelectSingleNode("pubDate")?.InnerText?.Trim() ?? "";

                if (string.IsNullOrEmpty(title))
                    continue;

                // Skip generic landing-page titles
                if (IsGenericPageTitle(title))
                    continue;

                // Deduplicate by title
                if (!seenTitles.Add(title))
                    continue;

                // ── Extract the REAL article URL from description HTML ─────
                // The <description> field contains HTML like:
                //   <a href="https://realsite.com/article">Title</a>
                // This is the actual article URL we can fetch and read.
                var realUrl = ExtractUrlFromDescription(description);
                var url     = !string.IsNullOrEmpty(realUrl) ? realUrl : redirectUrl;

                if (string.IsNullOrEmpty(url))
                    continue;

                // Deduplicate by URL
                if (!seenUrls.Add(url))
                    continue;

                // Domain: prefer source URL attribute, then source name, then URL
                var domain = !string.IsNullOrEmpty(sourceUrl)
                    ? ExtractDomain(sourceUrl)
                    : !string.IsNullOrEmpty(source)
                        ? source
                        : ExtractDomain(url);

                // Strip " - SourceName" suffix from title
                var cleanTitle = title;
                if (!string.IsNullOrEmpty(source) &&
                    cleanTitle.EndsWith($" - {source}", StringComparison.OrdinalIgnoreCase))
                {
                    cleanTitle = cleanTitle[..^($" - {source}".Length)].Trim();
                }

                // Parse publication date if available
                var snippet = "";
                if (DateTime.TryParse(pubDate, out var parsedDate))
                    snippet = parsedDate.ToString("yyyy-MM-dd HH:mm");

                results.Add(new SearchResult
                {
                    Title   = cleanTitle,
                    Url     = url,
                    Snippet = snippet,
                    Source  = domain
                });
            }
        }
        catch
        {
            // Malformed XML — return whatever we parsed so far
        }

        return results;
    }

    // ─────────────────────────────────────────────────────────────────
    // URL Extraction from Description HTML
    //
    // The <description> element contains HTML-encoded markup like:
    //   <ol><li><a href="https://real-url.com/article">Title</a>
    //   &nbsp;&nbsp;<font color="#6f6f6f">Source</font></li></ol>
    //
    // We extract the href from the first <a> tag — that's the actual
    // article URL that ContentExtractor can fetch and parse.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the first href from HTML anchor tags in the description.
    /// Returns null if no valid URL is found.
    /// </summary>
    public static string? ExtractUrlFromDescription(string descriptionHtml)
    {
        if (string.IsNullOrWhiteSpace(descriptionHtml))
            return null;

        var match = HrefRegex().Match(descriptionHtml);
        if (!match.Success)
            return null;

        var href = match.Groups[1].Value.Trim();

        // Validate it's a real HTTP URL, not another Google redirect
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https") &&
            !href.Contains("news.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return href;
        }

        return null;
    }

    [GeneratedRegex(@"href\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    /// <summary>
    /// Detects generic homepage/landing-page titles that provide
    /// zero information value.
    /// </summary>
    private static bool IsGenericPageTitle(string title)
    {
        var lower = title.ToLowerInvariant();
        return lower.Contains("breaking news, latest news") ||
               lower.Contains("latest news and videos") ||
               lower.Contains("homepage") ||
               lower.Equals("news", StringComparison.Ordinal) ||
               lower.Equals("home", StringComparison.Ordinal);
    }

    private static string ExtractDomain(string url)
    {
        try
        {
            return new Uri(url).Host.Replace("www.", "");
        }
        catch
        {
            return "";
        }
    }

    public void Dispose() => _http.Dispose();
}
