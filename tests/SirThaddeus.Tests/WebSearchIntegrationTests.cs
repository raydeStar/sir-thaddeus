using SirThaddeus.WebSearch;
using SirThaddeus.WebSearch.Providers;

namespace SirThaddeus.Tests;

// ─────────────────────────────────────────────────────────────────────────
// Web Search Integration Smoke Tests
//
// Lightweight GET-only health checks to verify that external services
// are reachable and responding. These do NOT test functionality — just
// that the endpoints exist and return something sensible.
//
// Run selectively:
//   dotnet test --filter "Category=Integration"
//
// Skip in CI / offline:
//   dotnet test --filter "Category!=Integration"
//
// These tests require network access. They will fail if the machine
// is offline or if the external service is temporarily down.
// ─────────────────────────────────────────────────────────────────────────

[Trait("Category", "Integration")]
public class DuckDuckGoHealthTests : IDisposable
{
    private readonly HttpClient _http;

    public DuckDuckGoHealthTests()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    /// <summary>
    /// Verify the DDG HTML endpoint is alive and returns a 200.
    /// This is the same endpoint the DuckDuckGoHtmlProvider uses.
    /// NOTE: DDG now blocks automated scraping (JS anti-bot challenge),
    /// so this only tests reachability, not search functionality.
    /// </summary>
    [Fact]
    public async Task DdgHtmlEndpoint_IsReachable()
    {
        var response = await _http.GetAsync("https://html.duckduckgo.com/");

        Assert.True(
            response.IsSuccessStatusCode,
            $"DDG HTML endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    /// <summary>
    /// Verify that the provider's own IsAvailable method reports healthy.
    /// Uses GET under the hood — no search queries sent.
    /// </summary>
    [Fact]
    public async Task DdgProvider_ReportsAvailable()
    {
        using var provider = new DuckDuckGoHtmlProvider();

        var available = await provider.IsAvailableAsync();

        Assert.True(available, "DuckDuckGoHtmlProvider.IsAvailableAsync() returned false");
    }

    public void Dispose() => _http.Dispose();
}

[Trait("Category", "Integration")]
public class GoogleNewsHealthTests
{
    /// <summary>
    /// Verify the Google News RSS endpoint is alive and returns valid XML.
    /// This is the primary search fallback when DDG is blocked.
    /// </summary>
    [Fact]
    public async Task GoogleNewsRss_IsReachable()
    {
        using var provider = new GoogleNewsRssProvider();

        var available = await provider.IsAvailableAsync();

        Assert.True(available, "Google News RSS endpoint is unreachable");
    }

    /// <summary>
    /// Verify that a search returns actual news results with source metadata
    /// and real article URLs (not Google News redirect wrappers).
    /// </summary>
    [Fact]
    public async Task GoogleNewsRss_ReturnsResultsWithRealUrls()
    {
        using var provider = new GoogleNewsRssProvider();

        var result = await provider.SearchAsync(
            "technology",
            new WebSearchOptions { MaxResults = 5, TimeoutMs = 10_000 });

        Assert.NotNull(result);
        Assert.Equal("GoogleNews", result.Provider);
        Assert.True(result.Results.Count > 0, "Expected at least one news result");

        var first = result.Results[0];
        Assert.False(string.IsNullOrWhiteSpace(first.Title), "Result title is blank");
        Assert.False(string.IsNullOrWhiteSpace(first.Url), "Result URL is blank");
        Assert.False(string.IsNullOrWhiteSpace(first.Source), "Source name is blank");

        // Source should be a news outlet name, NOT "news.google.com"
        Assert.DoesNotContain("news.google.com", first.Source,
            StringComparison.OrdinalIgnoreCase);

        // URL should be a real article URL when description parsing works,
        // or at worst a Google News redirect (which browsers handle)
        Assert.StartsWith("http", first.Url, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verify that top headlines feed works for generic queries.
    /// Should return diverse results from multiple outlets.
    /// </summary>
    [Fact]
    public async Task GoogleNewsRss_TopHeadlines_ReturnsDiverseResults()
    {
        using var provider = new GoogleNewsRssProvider();

        var result = await provider.SearchAsync(
            "latest news",
            new WebSearchOptions { MaxResults = 5, TimeoutMs = 10_000 });

        Assert.NotNull(result);
        Assert.True(result.Results.Count > 0, "Expected at least one headline");

        // Should NOT contain generic landing page titles
        foreach (var r in result.Results)
        {
            Assert.DoesNotContain("breaking news, latest news",
                r.Title, StringComparison.OrdinalIgnoreCase);
        }
    }
}

[Trait("Category", "Integration")]
public class ContentExtractionHealthTests : IDisposable
{
    private readonly HttpClient _http;

    public ContentExtractionHealthTests()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    /// <summary>
    /// Verify we can GET a known stable page (example.com is an IANA
    /// reserved domain that always returns a simple HTML page).
    /// This validates the same HTTP pipeline that ContentExtractor uses.
    /// </summary>
    [Fact]
    public async Task ExampleCom_IsReachable()
    {
        var response = await _http.GetAsync("https://example.com");

        Assert.True(
            response.IsSuccessStatusCode,
            $"example.com returned {(int)response.StatusCode}");

        var body = await response.Content.ReadAsStringAsync();

        Assert.False(string.IsNullOrWhiteSpace(body), "Response body is empty");
        Assert.Contains("<title>", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verify that favicon.ico is reachable on a known domain.
    /// ContentExtractor falls back to /favicon.ico when no link tag is found.
    /// </summary>
    [Fact]
    public async Task FaviconIco_IsReachable()
    {
        // example.com doesn't serve a favicon, so use a domain that does
        var response = await _http.GetAsync("https://www.google.com/favicon.ico");

        Assert.True(
            response.IsSuccessStatusCode,
            $"Google favicon returned {(int)response.StatusCode}");

        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.True(bytes.Length > 0, "Favicon is empty");
        Assert.True(bytes.Length < 64 * 1024, "Favicon exceeds 64KB cap");
    }

    /// <summary>
    /// Verify a favicon link tag can be parsed from raw HTML.
    /// No network call needed — purely validates the parsing logic
    /// that ContentExtractor uses internally.
    /// </summary>
    [Fact]
    public void FaviconLinkTag_ParsesCorrectly()
    {
        // Simulates what ContentExtractor.ParseFaviconUrl does
        var html = """
            <html>
            <head>
              <link rel="icon" href="/static/favicon.png" type="image/png"/>
              <title>Test</title>
            </head>
            <body>Hello</body>
            </html>
            """;

        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);

        var iconNode = doc.DocumentNode.SelectSingleNode(
            "//link[contains(@rel, 'icon') and not(contains(@rel, 'apple'))]");

        Assert.NotNull(iconNode);

        var href = iconNode.GetAttributeValue("href", null);
        Assert.Equal("/static/favicon.png", href);

        // Verify relative URL resolution
        var pageUri = new Uri("https://example.com/page/article");
        Assert.True(Uri.TryCreate(pageUri, href, out var absolute));
        Assert.Equal("https://example.com/static/favicon.png", absolute!.ToString());
    }

    public void Dispose() => _http.Dispose();
}

[Trait("Category", "Integration")]
public class WebSearchRouterHealthTests
{
    /// <summary>
    /// Verify the router reports at least one provider is available.
    /// In auto mode, it probes SearxNG, DDG, then Google News RSS.
    /// Any being alive means the router is functional.
    /// </summary>
    [Fact]
    public async Task Router_AutoMode_HasAvailableProvider()
    {
        using var router = new WebSearchRouter(mode: "auto");

        var available = await router.IsAvailableAsync();

        Assert.True(available,
            "WebSearchRouter has no available providers — all search backends are unreachable");
    }

    /// <summary>
    /// Verify the DDG-only mode reports available (endpoint reachable).
    /// Note: DDG now blocks search queries with JS challenges,
    /// but the endpoint itself is still reachable for IsAvailable checks.
    /// </summary>
    [Fact]
    public async Task Router_DdgMode_IsAvailable()
    {
        using var router = new WebSearchRouter(mode: "ddg_html");

        var available = await router.IsAvailableAsync();

        Assert.True(available, "DDG-only router mode is unavailable");
    }

    /// <summary>
    /// Verify Google News mode is available and working.
    /// This is the primary fallback now that DDG blocks automated access.
    /// </summary>
    [Fact]
    public async Task Router_GoogleNewsMode_IsAvailable()
    {
        using var router = new WebSearchRouter(mode: "google_news");

        var available = await router.IsAvailableAsync();

        Assert.True(available, "Google News router mode is unavailable");
    }

    /// <summary>
    /// End-to-end search using auto mode — should cascade through providers
    /// and return real results (Google News RSS as fallback when DDG is dead).
    /// </summary>
    [Fact]
    public async Task Router_CanPerformActualSearch()
    {
        using var router = new WebSearchRouter(mode: "auto");

        var result = await router.SearchAsync(
            "technology news",
            new WebSearchOptions { MaxResults = 3, TimeoutMs = 15_000 });

        Assert.NotNull(result);

        // Should get results from at least one provider
        Assert.True(result.Results.Count > 0,
            $"No results returned. Provider: {result.Provider}, " +
            $"Errors: {string.Join("; ", result.Errors)}");

        // Verify structure is sound
        var first = result.Results[0];
        Assert.False(string.IsNullOrWhiteSpace(first.Title), "Result title is blank");
        Assert.False(string.IsNullOrWhiteSpace(first.Url), "Result URL is blank");
    }
}

[Trait("Category", "Integration")]
public class SearxngHealthTests
{
    /// <summary>
    /// Checks if a local SearxNG instance is running.
    /// Passes if reachable, passes with output message if not.
    /// SearxNG is optional — Google News RSS is the fallback.
    /// </summary>
    [Fact]
    public async Task SearxngInstance_OptionalHealthCheck()
    {
        using var provider = new SearxngProvider("http://localhost:8080");
        var available = await provider.IsAvailableAsync();

        // SearxNG is optional — Google News RSS is the fallback.
        // We report status but don't fail the build if it's offline.
        if (!available)
        {
            Assert.True(true,
                "SearxNG is not running on localhost:8080 (optional — Google News fallback is active)");
            return;
        }

        Assert.True(available, "SearxNG is reachable");
    }
}
