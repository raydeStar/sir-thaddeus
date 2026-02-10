using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SirThaddeus.WebSearch;

// ─────────────────────────────────────────────────────────────────────────
// RSS / Atom Feed Provider
//
// Fetches a feed URL and parses a bounded set of items with strict limits:
//   - HTTP GET only
//   - Payload byte cap
//   - XML parser with DTD disabled
//   - SSRF guard (localhost/private by default)
//   - Coalescing + cache + polite throttling
// ─────────────────────────────────────────────────────────────────────────

public sealed partial class FeedProvider : IFeedProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly PublicApiServiceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly ProviderThrottle _throttle;

    private readonly ConcurrentDictionary<string, CacheEntry<FeedSnapshot>> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<FeedSnapshot>>> _inflight =
        new(StringComparer.OrdinalIgnoreCase);

    public FeedProvider(
        PublicApiServiceOptions? options = null,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new PublicApiServiceOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheTtl = TimeSpan.FromMinutes(Math.Max(1, _options.FeedCacheMinutes));

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

    public async Task<FeedSnapshot> FetchAsync(
        string url,
        int maxItems = 5,
        CancellationToken cancellationToken = default)
    {
        maxItems = Math.Clamp(maxItems, 1, 20);
        var guard = await PublicApiUrlGuard.NormalizeAndValidateAsync(
            url,
            _options.BlockPrivateNetworkTargets,
            cancellationToken).ConfigureAwait(false);

        if (!guard.Allowed || guard.Uri is null)
            throw new InvalidOperationException(guard.Error ?? "Feed URL is not allowed.");

        var normalizedUrl = guard.Uri.ToString();
        var now = _timeProvider.GetUtcNow();
        if (PublicApiCacheHelper.TryGetFresh(_cache, normalizedUrl, _cacheTtl, now, out var cached, out var age))
        {
            return cached with
            {
                Cache = new PublicApiCacheMetadata { Hit = true, AgeSeconds = age }
            };
        }

        return await RequestCoalescer.CoalesceAsync(
            _inflight,
            normalizedUrl,
            async () =>
            {
                var insideNow = _timeProvider.GetUtcNow();
                if (PublicApiCacheHelper.TryGetFresh(_cache, normalizedUrl, _cacheTtl, insideNow, out var second, out var secondAge))
                {
                    return second with
                    {
                        Cache = new PublicApiCacheMetadata { Hit = true, AgeSeconds = secondAge }
                    };
                }

                var xmlBytes = await FetchXmlBytesAsync(guard.Uri, cancellationToken).ConfigureAwait(false);
                var parsed = ParseFeed(xmlBytes, guard.Uri, maxItems);
                var result = parsed with
                {
                    Url = normalizedUrl,
                    SourceHost = guard.Uri.Host,
                    Cache = new PublicApiCacheMetadata { Hit = false, AgeSeconds = 0 }
                };

                _cache[normalizedUrl] = new CacheEntry<FeedSnapshot>(result, insideNow);
                return result;
            }).ConfigureAwait(false);
    }

    private async Task<byte[]> FetchXmlBytesAsync(Uri uri, CancellationToken cancellationToken)
    {
        await _throttle.WaitTurnAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(Math.Clamp(_options.RequestTimeoutMs, 2_000, 25_000));

            var response = await RetryHelper.ExecuteAsync(async () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rss+xml"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/atom+xml"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));

                var res = await _http.SendAsync(
                    req,
                    HttpCompletionOption.ResponseHeadersRead,
                    linked.Token).ConfigureAwait(false);

                if (!res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                    throw new HttpRequestException(
                        $"Feed endpoint returned {(int)res.StatusCode} ({res.StatusCode}). Body: {Truncate(body, 220)}",
                        null,
                        res.StatusCode);
                }

                return res;
            }, linked.Token).ConfigureAwait(false);

            using (response)
            {
                var maxBytes = Math.Clamp(_options.FeedMaxBytes, 8 * 1024, 2 * 1024 * 1024);
                if (response.Content.Headers.ContentLength is long length &&
                    length > maxBytes)
                {
                    throw new InvalidOperationException(
                        $"Feed payload exceeds limit ({length} bytes > {maxBytes} bytes).");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                return await ReadBoundedBytesAsync(stream, maxBytes, linked.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _throttle.Release();
        }
    }

    private FeedSnapshot ParseFeed(byte[] xmlBytes, Uri feedUri, int maxItems)
    {
        using var ms = new MemoryStream(xmlBytes);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024
        };

        using var reader = XmlReader.Create(ms, settings);
        var doc = XDocument.Load(reader, LoadOptions.None);
        var root = doc.Root ?? throw new InvalidOperationException("Feed XML has no root element.");
        var rootName = root.Name.LocalName.ToLowerInvariant();

        return rootName switch
        {
            "rss" => ParseRss(root, feedUri, maxItems),
            "rdf" => ParseRss(root, feedUri, maxItems), // RSS 1.0 (RDF root)
            "feed" => ParseAtom(root, feedUri, maxItems),
            _ => throw new InvalidOperationException(
                $"Unsupported feed root '{root.Name.LocalName}'. Expected RSS or Atom.")
        };
    }

    private FeedSnapshot ParseRss(XElement root, Uri feedUri, int maxItems)
    {
        var channel = root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("channel", StringComparison.OrdinalIgnoreCase))
                      ?? root;

        var feedTitle = CleanText(GetChildValue(channel, "title"));
        var description = CleanText(GetChildValue(channel, "description"));

        var allItems = channel.Elements()
            .Where(e => e.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var parsed = allItems
            .Take(maxItems)
            .Select(item => ParseRssItem(item, feedUri))
            .Where(item => !string.IsNullOrWhiteSpace(item.Title) || !string.IsNullOrWhiteSpace(item.Link))
            .ToList();

        return new FeedSnapshot
        {
            Url = feedUri.ToString(),
            FeedTitle = string.IsNullOrWhiteSpace(feedTitle) ? feedUri.Host : feedTitle,
            Description = description,
            SourceHost = feedUri.Host,
            Source = "rss",
            Truncated = allItems.Count > maxItems,
            Items = parsed,
            Cache = new PublicApiCacheMetadata()
        };
    }

    private FeedSnapshot ParseAtom(XElement root, Uri feedUri, int maxItems)
    {
        var ns = root.Name.Namespace;
        var feedTitle = CleanText(root.Element(ns + "title")?.Value ?? "");
        var description = CleanText(root.Element(ns + "subtitle")?.Value ?? "");

        var allEntries = root.Elements(ns + "entry").ToList();
        var parsed = allEntries
            .Take(maxItems)
            .Select(entry => ParseAtomEntry(entry, ns, feedUri))
            .Where(item => !string.IsNullOrWhiteSpace(item.Title) || !string.IsNullOrWhiteSpace(item.Link))
            .ToList();

        return new FeedSnapshot
        {
            Url = feedUri.ToString(),
            FeedTitle = string.IsNullOrWhiteSpace(feedTitle) ? feedUri.Host : feedTitle,
            Description = description,
            SourceHost = feedUri.Host,
            Source = "atom",
            Truncated = allEntries.Count > maxItems,
            Items = parsed,
            Cache = new PublicApiCacheMetadata()
        };
    }

    private FeedItem ParseRssItem(XElement item, Uri feedUri)
    {
        var title = CleanText(GetChildValue(item, "title"));
        var link = ResolveLink(feedUri, GetChildValue(item, "link"));
        var summary = CleanText(
            StripHtml(GetChildValue(item, "description")));
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = CleanText(StripHtml(GetChildValue(item, "encoded")));
        }

        var author = CleanText(GetChildValue(item, "author"));
        if (string.IsNullOrWhiteSpace(author))
            author = CleanText(GetChildValue(item, "creator"));

        var published = ParseDate(
            GetChildValue(item, "pubDate"),
            GetChildValue(item, "date"));

        return new FeedItem
        {
            Title = title,
            Link = link,
            Summary = TrimSummary(summary),
            Author = author,
            PublishedAt = published
        };
    }

    private FeedItem ParseAtomEntry(XElement entry, XNamespace ns, Uri feedUri)
    {
        var title = CleanText(entry.Element(ns + "title")?.Value ?? "");
        var summary = CleanText(StripHtml(
            entry.Element(ns + "summary")?.Value ??
            entry.Element(ns + "content")?.Value ??
            ""));

        var author = CleanText(
            entry.Element(ns + "author")?.Element(ns + "name")?.Value ?? "");

        var published = ParseDate(
            entry.Element(ns + "published")?.Value ?? "",
            entry.Element(ns + "updated")?.Value ?? "");

        var linkEl = entry.Elements(ns + "link")
            .FirstOrDefault(l =>
            {
                var rel = (l.Attribute("rel")?.Value ?? "").Trim();
                return string.IsNullOrWhiteSpace(rel) ||
                       rel.Equals("alternate", StringComparison.OrdinalIgnoreCase);
            })
            ?? entry.Elements(ns + "link").FirstOrDefault();

        var href = linkEl?.Attribute("href")?.Value ?? "";
        var link = ResolveLink(feedUri, href);

        return new FeedItem
        {
            Title = title,
            Link = link,
            Summary = TrimSummary(summary),
            Author = author,
            PublishedAt = published
        };
    }

    private string TrimSummary(string value)
    {
        var max = Math.Clamp(_options.FeedSummaryMaxChars, 120, 1_200);
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return value.Length <= max ? value : value[..max] + "\u2026";
    }

    private static string GetChildValue(XElement parent, string localName)
    {
        var match = parent.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
        return match?.Value ?? "";
    }

    private static string ResolveLink(Uri baseUri, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var value = raw.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.ToString();
        }

        if (Uri.TryCreate(baseUri, value, out var relative) &&
            (relative.Scheme == Uri.UriSchemeHttp || relative.Scheme == Uri.UriSchemeHttps))
        {
            return relative.ToString();
        }

        return "";
    }

    private static DateTimeOffset? ParseDate(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (DateTimeOffset.TryParse(candidate, out var parsed))
                return parsed;
        }

        return null;
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var total = 0;
        using var ms = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException(
                    $"Feed payload exceeds limit ({total} bytes > {maxBytes} bytes).");
            }

            await ms.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return ms.ToArray();
    }

    private static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var decoded = WebUtility.HtmlDecode(text);
        var collapsed = WhitespaceRegex().Replace(decoded, " ").Trim();
        return collapsed;
    }

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var noTags = HtmlTagRegex().Replace(html, " ");
        return WebUtility.HtmlDecode(noTags);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return s.Length <= max ? s : s[..max] + "\u2026";
    }

    [GeneratedRegex("<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
        _throttle.Dispose();
    }
}
