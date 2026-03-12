using SirThaddeus.WebSearch.Providers;

namespace SirThaddeus.WebSearch;

// Web Search Router
//
// Orchestrates search requests across available providers.
//
// Probe order (auto mode):
//   1. SearxNG (if configured and available)
//   2. Search API (if configured)
//   3. Google News RSS (reliable fallback for news-ish lookups)
//
// Modes:
//   "auto"        - probe in order, cache availability
//   "searxng"     - SearxNG only
//   "search_api"  - hosted search API only
//   "api"         - alias for search_api
//   "ddg_html"    - DDG only (currently broken - DDG blocks automated access)
//   "google_news" - Google News RSS only
//   "manual"      - return "paste URLs manually" message

public sealed class WebSearchRouter : IWebSearchProvider, IDisposable
{
    private readonly string _mode;
    private readonly IWebSearchProvider _ddg;
    private readonly IWebSearchProvider _searxng;
    private readonly IWebSearchProvider _searchApi;
    private readonly IWebSearchProvider _googleNews;
    private readonly List<IDisposable> _ownedDisposables = [];

    private bool? _searxngAvailable;
    private bool? _searchApiAvailable;
    private DateTime _lastProbeTime = DateTime.MinValue;
    private readonly SemaphoreSlim _probeLock = new(1, 1);

    /// <summary>
    /// How long a probe result is considered valid before re-checking.
    /// SearxNG may start up after the router - 5 minutes keeps latency
    /// low while allowing recovery within a reasonable window.
    /// </summary>
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromMinutes(5);

    public WebSearchRouter(
        string mode = "auto",
        string searxngBaseUrl = "http://localhost:8080",
        string searchApiKey = "",
        string searchApiBaseUrl = "https://www.searchapi.io/api/v1/search",
        string searchApiEngine = "google")
    {
        _mode = NormalizeMode(mode);

        var ddg = new DuckDuckGoHtmlProvider();
        var searxng = new SearxngProvider(searxngBaseUrl);
        var searchApi = new SearchApiProvider(searchApiKey, searchApiBaseUrl, searchApiEngine);
        var googleNews = new GoogleNewsRssProvider();

        _ddg = ddg;
        _searxng = searxng;
        _searchApi = searchApi;
        _googleNews = googleNews;

        _ownedDisposables.Add(ddg);
        _ownedDisposables.Add(searxng);
        _ownedDisposables.Add(searchApi);
        _ownedDisposables.Add(googleNews);
    }

    internal WebSearchRouter(
        string mode,
        IWebSearchProvider searxng,
        IWebSearchProvider searchApi,
        IWebSearchProvider ddg,
        IWebSearchProvider googleNews)
    {
        _mode = NormalizeMode(mode);
        _searxng = searxng;
        _searchApi = searchApi;
        _ddg = ddg;
        _googleNews = googleNews;
    }

    public string Name => "WebSearchRouter";

    public async Task<SearchResults> SearchAsync(
        string query,
        WebSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        return _mode switch
        {
            "searxng" => await SearchWithSearxngAsync(query, options, cancellationToken),
            "search_api" => await SearchWithSearchApiAsync(query, options, cancellationToken),
            "ddg_html" => await SearchWithDdgAsync(query, options, cancellationToken),
            "google_news" => await SearchWithGoogleNewsAsync(query, options, cancellationToken),
            "manual" => ManualModeResult(),
            _ => await SearchAutoAsync(query, options, cancellationToken)
        };
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_mode == "manual")
            return true;

        if (_mode == "searxng")
            return await _searxng.IsAvailableAsync(cancellationToken);

        if (_mode == "search_api")
            return await _searchApi.IsAvailableAsync(cancellationToken);

        if (_mode == "ddg_html")
            return await _ddg.IsAvailableAsync(cancellationToken);

        if (_mode == "google_news")
            return await _googleNews.IsAvailableAsync(cancellationToken);

        return await _searxng.IsAvailableAsync(cancellationToken)
            || await _searchApi.IsAvailableAsync(cancellationToken)
            || await _googleNews.IsAvailableAsync(cancellationToken);
    }

    private async Task<SearchResults> SearchAutoAsync(
        string query,
        WebSearchOptions options,
        CancellationToken ct)
    {
        await ProbeProvidersAsync(ct);

        if (_searxngAvailable == true)
        {
            var result = await _searxng.SearchAsync(query, options, ct);
            if (result.Results.Count > 0)
                return result;

            // SearxNG was available during probing but failed to deliver
            // results. Force the next request to probe again.
            InvalidateProbeCache();
        }

        if (_searchApiAvailable == true)
        {
            var result = await _searchApi.SearchAsync(query, options, ct);
            if (result.Results.Count > 0)
                return result;

            // Search API returned no usable results or errored out.
            // Mark it stale so the next request re-probes.
            _searchApiAvailable = false;
        }

        return await SearchWithGoogleNewsAsync(query, options, ct);
    }

    /// <summary>
    /// Probes provider availability with a time-based cache. Results are
    /// valid for <see cref="ProbeTtl"/>; after that, we re-probe so
    /// providers that came up late can be discovered without restart.
    /// </summary>
    private async Task ProbeProvidersAsync(CancellationToken ct)
    {
        if (_searxngAvailable is not null &&
            _searchApiAvailable is not null &&
            DateTime.UtcNow - _lastProbeTime < ProbeTtl)
        {
            return;
        }

        await _probeLock.WaitAsync(ct);
        try
        {
            if (_searxngAvailable is not null &&
                _searchApiAvailable is not null &&
                DateTime.UtcNow - _lastProbeTime < ProbeTtl)
            {
                return;
            }

            _searxngAvailable = await _searxng.IsAvailableAsync(ct);
            _searchApiAvailable = await _searchApi.IsAvailableAsync(ct);
            _lastProbeTime = DateTime.UtcNow;
        }
        finally
        {
            _probeLock.Release();
        }
    }

    /// <summary>
    /// Forces the next auto-mode search to re-probe providers.
    /// </summary>
    private void InvalidateProbeCache()
    {
        _lastProbeTime = DateTime.MinValue;
    }

    private async Task<SearchResults> SearchWithSearxngAsync(
        string query,
        WebSearchOptions options,
        CancellationToken ct)
    {
        var result = await _searxng.SearchAsync(query, options, ct);

        if (result.Results.Count == 0 && result.Errors.Count > 0)
        {
            return result with
            {
                Errors = [.. result.Errors, "SearxNG mode is set but the instance may be down."]
            };
        }

        return result;
    }

    private async Task<SearchResults> SearchWithSearchApiAsync(
        string query,
        WebSearchOptions options,
        CancellationToken ct)
    {
        return await _searchApi.SearchAsync(query, options, ct);
    }

    private async Task<SearchResults> SearchWithDdgAsync(
        string query,
        WebSearchOptions options,
        CancellationToken ct)
    {
        return await _ddg.SearchAsync(query, options, ct);
    }

    private async Task<SearchResults> SearchWithGoogleNewsAsync(
        string query,
        WebSearchOptions options,
        CancellationToken ct)
    {
        return await _googleNews.SearchAsync(query, options, ct);
    }

    private static SearchResults ManualModeResult()
    {
        return new SearchResults
        {
            Provider = "Manual",
            Results = [],
            Errors = ["Search is in manual mode. Paste URLs directly and use BrowserNavigate to read them."]
        };
    }

    public void Dispose()
    {
        foreach (var disposable in _ownedDisposables)
            disposable.Dispose();

        _probeLock.Dispose();
    }

    private static string NormalizeMode(string mode)
    {
        var normalized = (mode ?? "auto").Trim().ToLowerInvariant();
        return normalized switch
        {
            "auto" => "auto",
            "searxng" => "searxng",
            "search_api" => "search_api",
            "api" => "search_api",
            "ddg_html" => "ddg_html",
            "google_news" => "google_news",
            "manual" => "manual",
            _ => "auto"
        };
    }
}
