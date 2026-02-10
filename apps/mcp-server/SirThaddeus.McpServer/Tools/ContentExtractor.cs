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
    private const int GoogleNewsResolveMaxLinks = 40;

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
            var (finalUri, html) = await FetchHtmlAsync(uri, http, cts.Token);

            // Google News RSS links sometimes resolve to an HTML wrapper
            // ("Google News") that requires extracting the real target URL
            // from the page markup. If we detect that wrapper, attempt a
            // single unwrap and re-fetch the actual article.
            if (IsGoogleNewsWrapper(finalUri) && !string.IsNullOrWhiteSpace(html))
            {
                var target = TryResolveGoogleNewsTargetUrl(html, finalUri);
                if (!string.IsNullOrWhiteSpace(target) &&
                    Uri.TryCreate(target, UriKind.Absolute, out var targetUri))
                {
                    try
                    {
                        var resolved = await FetchHtmlAsync(targetUri, http, cts.Token);
                        if (!string.IsNullOrWhiteSpace(resolved.Html))
                        {
                            finalUri = resolved.FinalUri;
                            html     = resolved.Html;
                        }
                    }
                    catch
                    {
                        // Best-effort unwrap; fall back to the wrapper page.
                    }
                }
            }

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

    private static async Task<(Uri FinalUri, string Html)> FetchHtmlAsync(
        Uri uri, HttpClient http, CancellationToken ct)
    {
        return await RetryHelper.ExecuteAsync(async () =>
        {
            using var resp = await http.GetAsync(uri, ct);
            resp.EnsureSuccessStatusCode();
            var body     = await resp.Content.ReadAsStringAsync(ct);
            var resolved = resp.RequestMessage?.RequestUri ?? uri;
            return (resolved, body);
        }, ct);
    }

    private static bool IsGoogleNewsWrapper(Uri uri)
    {
        var host = uri.Host.Replace("www.", "");
        if (!host.Equals("news.google.com", StringComparison.OrdinalIgnoreCase))
            return false;

        // Most wrapper links come from RSS article endpoints.
        return uri.AbsolutePath.Contains("/rss/articles/", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.Contains("/articles/", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.Contains("/rss/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryResolveGoogleNewsTargetUrl(string html, Uri pageUri)
    {
        // 1) Meta refresh
        var meta = TryExtractMetaRefreshUrl(html, pageUri);
        var metaUnwrapped = TryUnwrapGoogleRedirect(meta, pageUri);
        if (!string.IsNullOrWhiteSpace(metaUnwrapped))
            return metaUnwrapped;

        // 2) Canonical link
        var canonical = TryExtractCanonicalUrl(html, pageUri);
        var canonicalUnwrapped = TryUnwrapGoogleRedirect(canonical, pageUri);
        if (!string.IsNullOrWhiteSpace(canonicalUnwrapped))
            return canonicalUnwrapped;

        // 3) Anchor tags — pick the first plausible external article link
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var links = doc.DocumentNode.SelectNodes("//a[@href]");
        if (links is null)
            return null;

        var checkedCount = 0;
        foreach (var node in links)
        {
            if (checkedCount++ > GoogleNewsResolveMaxLinks)
                break;

            var href = node.GetAttributeValue("href", null);
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var candidate = TryUnwrapGoogleRedirect(href, pageUri);
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var u) ||
                (u.Scheme != "http" && u.Scheme != "https"))
                continue;

            if (IsGoogleDomain(u.Host))
                continue;

            return u.ToString();
        }

        return null;
    }

    private static bool IsGoogleDomain(string host)
    {
        var h = (host ?? "").Replace("www.", "").ToLowerInvariant();
        return h == "google.com" ||
               h.EndsWith(".google.com", StringComparison.Ordinal) ||
               h.EndsWith(".googleusercontent.com", StringComparison.Ordinal);
    }

    private static string? TryExtractCanonicalUrl(string html, Uri pageUri)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var node = doc.DocumentNode.SelectSingleNode("//link[@rel='canonical']");
        var href = node?.GetAttributeValue("href", null);
        if (string.IsNullOrWhiteSpace(href))
            return null;

        if (Uri.TryCreate(pageUri, href, out var absolute))
            return absolute.ToString();

        return null;
    }

    private static string? TryExtractMetaRefreshUrl(string html, Uri pageUri)
    {
        // Fast string scan — avoid full DOM parse for a simple pattern.
        var idx = html.IndexOf("http-equiv=\"refresh\"", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            idx = html.IndexOf("http-equiv='refresh'", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        // Find the next content="...".
        var contentIdx = html.IndexOf("content", idx, StringComparison.OrdinalIgnoreCase);
        if (contentIdx < 0)
            return null;

        var quoteIdx = html.IndexOf('"', contentIdx);
        var quoteChar = '"';
        if (quoteIdx < 0)
        {
            quoteIdx = html.IndexOf('\'', contentIdx);
            quoteChar = '\'';
        }
        if (quoteIdx < 0)
            return null;

        var endIdx = html.IndexOf(quoteChar, quoteIdx + 1);
        if (endIdx < 0)
            return null;

        var content = html[(quoteIdx + 1)..endIdx];
        var urlIdx  = content.IndexOf("url=", StringComparison.OrdinalIgnoreCase);
        if (urlIdx < 0)
            return null;

        var raw = content[(urlIdx + 4)..].Trim().Trim('\"', '\'', ' ');
        raw = WebUtility.HtmlDecode(raw);

        if (Uri.TryCreate(pageUri, raw, out var absolute))
            return absolute.ToString();

        return null;
    }

    private static string? TryUnwrapGoogleRedirect(string? href, Uri pageUri)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;

        if (!Uri.TryCreate(pageUri, href, out var uri))
            return null;

        // Direct external link (best case)
        if (!IsGoogleDomain(uri.Host))
            return uri.ToString();

        // Google redirect wrappers (common patterns)
        // Example: https://www.google.com/url?url=https%3A%2F%2Fexample.com%2Farticle&...
        // Also seen: ?q=..., ?u=...
        var query = uri.Query;
        if (string.IsNullOrWhiteSpace(query) || query.Length < 4)
            return null;

        var kv = ParseQueryString(query);
        if (TryGetFirst(kv, out var target, "url", "q", "u") &&
            Uri.TryCreate(target, UriKind.Absolute, out var targetUri) &&
            (targetUri.Scheme == "http" || targetUri.Scheme == "https") &&
            !IsGoogleDomain(targetUri.Host))
        {
            return targetUri.ToString();
        }

        return null;
    }

    private static bool TryGetFirst(
        Dictionary<string, string> map, out string value, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                value = v;
                return true;
            }
        }

        value = "";
        return false;
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var q = query.StartsWith('?') ? query[1..] : query;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0 || idx >= part.Length - 1)
                continue;

            var key = WebUtility.UrlDecode(part[..idx]);
            var val = WebUtility.UrlDecode(part[(idx + 1)..]);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            // First value wins — avoid huge duplicates
            if (!map.ContainsKey(key))
                map[key] = val;
        }

        return map;
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
