using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using SirThaddeus.WebSearch;
using SmartReader;

namespace SirThaddeus.McpServer.Tools;

// ─────────────────────────────────────────────────────────────────────────
// Content Extraction Utility
//
// Shared logic for fetching and extracting readable text from web pages.
// Used by both BrowserNavigate (full page read) and WebSearch (auto-read
// top results). Keeps extraction consistent across tools.
//
// Pipeline:
//   1. HTTP GET with browser-like headers
//   2. SmartReader (Mozilla Readability port) for article extraction
//   3. HtmlAgilityPack fallback for non-article pages
//   4. Favicon extraction from <link rel="icon"> (bounded: 64KB / 3s)
// ─────────────────────────────────────────────────────────────────────────

public static class ContentExtractor
{
    private const int DefaultTimeoutSecs  = 15;
    private const int FaviconMaxBytes     = 64 * 1024;  // 64 KB cap
    private const int FaviconTimeoutSecs  = 3;

    /// <summary>
    /// Extracted content from a single web page.
    /// </summary>
    public sealed record ExtractionResult
    {
        public required string Url     { get; init; }
        public string Title            { get; init; } = "(untitled)";
        public string? Author          { get; init; }
        public DateTime? PublishedDate { get; init; }
        public string Domain           { get; init; } = "";
        public string TextContent      { get; init; } = "";
        public int WordCount           { get; init; }
        public bool IsArticle          { get; init; }
        public string? Error           { get; init; }

        /// <summary>
        /// Base64-encoded favicon image data (e.g. "data:image/png;base64,...").
        /// Null if not found or extraction failed — UI should use letter avatar fallback.
        /// </summary>
        public string? FaviconBase64   { get; init; }

        /// <summary>
        /// Open Graph / Twitter Card thumbnail URL (og:image or twitter:image).
        /// Passed as a URL for the UI to load — kept lightweight in the JSON.
        /// </summary>
        public string? ThumbnailUrl    { get; init; }

        public bool Succeeded => Error is null && TextContent.Length > 0;
    }

    /// <summary>
    /// Fetches a URL and extracts clean readable text + favicon.
    /// Returns a result even on partial failure (e.g. title but no body).
    /// </summary>
    public static async Task<ExtractionResult> ExtractAsync(
        string url,
        int timeoutSecs = DefaultTimeoutSecs,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return new ExtractionResult { Url = url, Error = "Invalid URL" };
        }

        var domain = uri.Host.Replace("www.", "");

        try
        {
            using var http = CreateHttpClient(timeoutSecs);
            using var cts  = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSecs));

            // Use GetAsync (not GetStringAsync) so we can capture the final
            // URL after redirects — critical for Google News redirect links.
            // Wrapped in retry for transient failures (DNS hiccups, 502s, etc.)
            var (finalUri, html) = await RetryHelper.ExecuteAsync(async () =>
            {
                using var resp = await http.GetAsync(uri, cts.Token);
                resp.EnsureSuccessStatusCode();
                var body     = await resp.Content.ReadAsStringAsync(cts.Token);
                var resolved = resp.RequestMessage?.RequestUri ?? uri;
                return (resolved, body);
            }, cancellationToken);

            url    = finalUri.ToString();
            domain = finalUri.Host.Replace("www.", "");

            if (string.IsNullOrWhiteSpace(html))
                return new ExtractionResult { Url = url, Domain = domain, Error = "Empty response" };

            // Favicon + thumbnail extraction runs alongside content parsing
            var faviconTask  = ExtractFaviconAsync(html, finalUri, http, cts.Token);
            var thumbnailUrl = ExtractOgImage(html, finalUri);

            // ── SmartReader first ────────────────────────────────────
            try
            {
                var article = Reader.ParseArticle(url, html);

                if (article.IsReadable && !string.IsNullOrWhiteSpace(article.TextContent))
                {
                    var favicon = await SafeAwaitFavicon(faviconTask);
                    return new ExtractionResult
                    {
                        Url           = url,
                        Title         = article.Title ?? "(untitled)",
                        Author        = article.Author,
                        PublishedDate = article.PublicationDate,
                        Domain        = domain,
                        TextContent   = article.TextContent,
                        WordCount     = CountWords(article.TextContent),
                        IsArticle     = true,
                        FaviconBase64 = favicon,
                        ThumbnailUrl  = thumbnailUrl
                    };
                }
            }
            catch
            {
                // SmartReader failed; fall through to basic extraction
            }

            // ── HtmlAgilityPack fallback ─────────────────────────────
            var text  = StripHtmlToText(html);
            var title = ExtractTitle(html);
            var fallbackFavicon = await SafeAwaitFavicon(faviconTask);

            return new ExtractionResult
            {
                Url           = url,
                Title         = title,
                Domain        = domain,
                TextContent   = text,
                WordCount     = CountWords(text),
                IsArticle     = false,
                FaviconBase64 = fallbackFavicon,
                ThumbnailUrl  = thumbnailUrl
            };
        }
        catch (TaskCanceledException)
        {
            return new ExtractionResult { Url = url, Domain = domain, Error = "Timed out" };
        }
        catch (HttpRequestException ex)
        {
            return new ExtractionResult { Url = url, Domain = domain, Error = ex.Message };
        }
        catch (Exception ex)
        {
            return new ExtractionResult { Url = url, Domain = domain, Error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    /// <summary>
    /// Fetches multiple URLs in parallel with bounded concurrency.
    /// </summary>
    public static async Task<List<ExtractionResult>> ExtractManyAsync(
        IEnumerable<string> urls,
        int maxConcurrency = 3,
        int perPageTimeoutSecs = 10,
        CancellationToken cancellationToken = default)
    {
        var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = urls.Select(async url =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await ExtractAsync(url, perPageTimeoutSecs, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// Truncates text content to a character limit with a clean word break.
    /// </summary>
    public static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            return text;

        var cutoff = text.LastIndexOf(' ', maxChars);
        if (cutoff < maxChars / 2) cutoff = maxChars;

        return text[..cutoff] + "...";
    }

    // ─────────────────────────────────────────────────────────────────
    // Favicon Extraction
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts favicon URL from HTML link tags, downloads the image bytes,
    /// and returns a data URI (base64). All within the MCP process — no
    /// network calls leak into the UI.
    /// </summary>
    private static async Task<string?> ExtractFaviconAsync(
        string html, Uri pageUri, HttpClient http, CancellationToken ct)
    {
        try
        {
            var faviconUrl = ParseFaviconUrl(html, pageUri);
            if (faviconUrl is null)
                return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(FaviconTimeoutSecs));

            var response = await http.GetAsync(faviconUrl, cts.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            // Enforce size cap
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > FaviconMaxBytes)
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
            if (bytes.Length == 0 || bytes.Length > FaviconMaxBytes)
                return null;

            // Determine MIME type from content-type header or URL extension
            var mime = response.Content.Headers.ContentType?.MediaType ?? GuessMime(faviconUrl);

            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            // Favicon is best-effort; never block content extraction
            return null;
        }
    }

    /// <summary>
    /// Parses the favicon URL from HTML link tags. Checks, in order:
    ///   1. link[rel="icon"]
    ///   2. link[rel="shortcut icon"]
    ///   3. /favicon.ico at the domain root (common default)
    /// </summary>
    private static string? ParseFaviconUrl(string html, Uri pageUri)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Try rel="icon" first, then "shortcut icon"
        var selectors = new[]
        {
            "//link[contains(@rel, 'icon') and not(contains(@rel, 'apple'))]",
            "//link[@rel='shortcut icon']"
        };

        foreach (var selector in selectors)
        {
            var node = doc.DocumentNode.SelectSingleNode(selector);
            var href = node?.GetAttributeValue("href", null);

            if (!string.IsNullOrWhiteSpace(href))
            {
                // Resolve relative URLs against the page URI
                if (Uri.TryCreate(pageUri, href, out var absolute))
                    return absolute.ToString();
            }
        }

        // Fallback: try /favicon.ico at the domain root
        return $"{pageUri.Scheme}://{pageUri.Host}/favicon.ico";
    }

    /// <summary>
    /// Extracts the Open Graph or Twitter Card image URL from the page HTML.
    /// These are the standard article preview/thumbnail images used by
    /// social media, search engines, and link previews.
    /// </summary>
    private static string? ExtractOgImage(string html, Uri pageUri)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Try og:image first (most common), then twitter:image
        var metaNode =
            doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']")
            ?? doc.DocumentNode.SelectSingleNode("//meta[@name='twitter:image']")
            ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:image:url']");

        var content = metaNode?.GetAttributeValue("content", null);
        if (string.IsNullOrWhiteSpace(content))
            return null;

        // Resolve relative URLs against the page URI
        if (Uri.TryCreate(pageUri, content, out var absolute) &&
            (absolute.Scheme == "http" || absolute.Scheme == "https"))
        {
            return absolute.ToString();
        }

        return null;
    }

    private static string GuessMime(string url)
    {
        if (url.Contains(".svg", StringComparison.OrdinalIgnoreCase)) return "image/svg+xml";
        if (url.Contains(".png", StringComparison.OrdinalIgnoreCase)) return "image/png";
        if (url.Contains(".jpg", StringComparison.OrdinalIgnoreCase) ||
            url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        if (url.Contains(".gif", StringComparison.OrdinalIgnoreCase)) return "image/gif";
        if (url.Contains(".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
        return "image/x-icon";  // .ico default
    }

    /// <summary>
    /// Safely awaits a favicon task, returning null on any failure.
    /// Favicon extraction must never block or crash content extraction.
    /// </summary>
    private static async Task<string?> SafeAwaitFavicon(Task<string?> faviconTask)
    {
        try { return await faviconTask; }
        catch { return null; }
    }

    // ─────────────────────────────────────────────────────────────────
    // Internal Helpers
    // ─────────────────────────────────────────────────────────────────

    private static HttpClient CreateHttpClient(int timeoutSecs)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSecs) };

        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        return http;
    }

    internal static string StripHtmlToText(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var removeSelectors = new[]
        {
            "//script", "//style", "//nav", "//footer", "//header",
            "//aside", "//noscript", "//svg", "//iframe",
            "//form", "//*[contains(@class,'cookie')]",
            "//*[contains(@class,'sidebar')]", "//*[contains(@class,'menu')]"
        };

        foreach (var selector in removeSelectors)
        {
            var nodes = doc.DocumentNode.SelectNodes(selector);
            if (nodes is null) continue;
            foreach (var node in nodes)
                node.Remove();
        }

        var rawText = doc.DocumentNode.InnerText;
        var decoded = WebUtility.HtmlDecode(rawText);
        var cleaned = Regex.Replace(decoded, @"[ \t]+", " ");
        var lines   = cleaned.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);

        return string.Join("\n", lines);
    }

    internal static string ExtractTitle(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        if (titleNode is not null)
        {
            var title = WebUtility.HtmlDecode(titleNode.InnerText).Trim();
            if (!string.IsNullOrEmpty(title))
                return title;
        }

        return "(untitled)";
    }

    private static int CountWords(string text) =>
        text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
}
