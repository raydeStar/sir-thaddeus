using SirThaddeus.WebSearch.Providers;

namespace SirThaddeus.WebSearch;

// ─────────────────────────────────────────────────────────────────────────
// Web Search Router
//
// Orchestrates search requests across available providers.
//
// Probe order (auto mode):
//   1. SearxNG (if configured and available)
//   2. DuckDuckGo HTML (if not blocked)
//   3. Google News RSS (reliable fallback — always works)
//
// Modes:
//   "auto"        — probe in order, cache availability
//   "searxng"     — SearxNG only
//   "ddg_html"    — DDG only (currently broken — DDG blocks automated access)
//   "google_news" — Google News RSS only
//   "manual"      — return "paste URLs manually" message
// ─────────────────────────────────────────────────────────────────────────

public sealed class WebSearchRouter : IWebSearchProvider, IDisposable
{
    private readonly string _mode;
    private readonly DuckDuckGoHtmlProvider _ddg;
    private readonly SearxngProvider _searxng;
    private readonly GoogleNewsRssProvider _googleNews;

    private bool? _searxngAvailable;
    private bool? _ddgAvailable;
    private DateTime _lastProbeTime = DateTime.MinValue;
    private readonly SemaphoreSlim _probeLock = new(1, 1);

    /// <summary>
    /// How long a probe result is considered valid before re-checking.
    /// SearxNG may start up after the router — 5 minutes keeps latency
    /// low while allowing recovery within a reasonable window.
    /// </summary>
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromMinutes(5);

    public WebSearchRouter(
        string mode = "auto",
        string searxngBaseUrl = "http://localhost:8080")
    {
        _mode       = mode.ToLowerInvariant();
        _ddg        = new DuckDuckGoHtmlProvider();
        _searxng    = new SearxngProvider(searxngBaseUrl);
        _googleNews = new GoogleNewsRssProvider();
    }

    public string Name => "WebSearchRouter";

    public async Task<SearchResults> SearchAsync(
        string query,
        WebSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        return _mode switch
        {
            "searxng"     => await SearchWithSearxngAsync(query, options, cancellationToken),
            "ddg_html"    => await SearchWithDdgAsync(query, options, cancellationToken),
            "google_news" => await SearchWithGoogleNewsAsync(query, options, cancellationToken),
            "manual"      => ManualModeResult(),
            _             => await SearchAutoAsync(query, options, cancellationToken)
        };
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_mode == "manual") return true;
        if (_mode == "searxng") return await _searxng.IsAvailableAsync(cancellationToken);
        if (_mode == "ddg_html") return await _ddg.IsAvailableAsync(cancellationToken);
        if (_mode == "google_news") return await _googleNews.IsAvailableAsync(cancellationToken);

        // Auto: any provider works
        return await _searxng.IsAvailableAsync(cancellationToken)
            || await _ddg.IsAvailableAsync(cancellationToken)
            || await _googleNews.IsAvailableAsync(cancellationToken);
    }

    // ─────────────────────────────────────────────────────────────────
    // Auto Mode — Cascade through providers
    // ─────────────────────────────────────────────────────────────────

    private static bool LooksLikeNewsIntent(string query) =>
        SearchIntentPatterns.LooksLikeNewsIntent(query);

    private async Task<SearchResults> SearchAutoAsync(
        string query, WebSearchOptions options, CancellationToken ct)
    {
        // ── News queries: prefer Google News RSS first ────────────────
        // Generic "give me the news" queries often produce low-signal
        // homepage results from meta-search engines (\"Breaking news...\").
        // Google News RSS returns actual article items with publish dates,
        // which we can summarize into real events.
        if (LooksLikeNewsIntent(query))
        {
            var google = await SearchWithGoogleNewsAsync(query, options, ct);
            if (google.Results.Count > 0)
                return google;
        }

        await ProbeProvidersAsync(ct);

        // 1. SearxNG (if available)
        if (_searxngAvailable == true)
        {
            var result = await _searxng.SearchAsync(query, options, ct);
            if (result.Results.Count > 0)
                return result;

            // SearxNG was "available" but failed to deliver — invalidate
            // the cache so the next request re-probes instead of assuming
            // it's still up.
            InvalidateProbeCache();
        }

        // 2. DuckDuckGo (if not known to be blocked)
        if (_ddgAvailable != false)
        {
            var result = await _ddg.SearchAsync(query, options, ct);
            if (result.Results.Count > 0)
                return result;

            // DDG returned nothing — mark as temporarily unavailable.
            // The probe cache TTL will allow re-evaluation later instead
            // of permanently blacklisting.
            _ddgAvailable = false;
        }

        // 3. Google News RSS (reliable fallback)
        return await SearchWithGoogleNewsAsync(query, options, ct);
    }

    /// <summary>
    /// Probes provider availability with a time-based cache. Results are
    /// valid for <see cref="ProbeTtl"/> — after that, we re-probe so
    /// providers that came up late (e.g. SearxNG container) can be
    /// discovered without restarting the process.
    /// </summary>
    private async Task ProbeProvidersAsync(CancellationToken ct)
    {
        if (_searxngAvailable is not null &&
            DateTime.UtcNow - _lastProbeTime < ProbeTtl)
        {
            return; // Cache is still fresh
        }

        await _probeLock.WaitAsync(ct);
        try
        {
            // Double-check inside the lock (another thread may have probed)
            if (_searxngAvailable is not null &&
                DateTime.UtcNow - _lastProbeTime < ProbeTtl)
            {
                return;
            }

            _searxngAvailable = await _searxng.IsAvailableAsync(ct);

            // DDG gets a fresh chance on every re-probe cycle
            _ddgAvailable = null;

            _lastProbeTime = DateTime.UtcNow;
        }
        finally
        {
            _probeLock.Release();
        }
    }

    /// <summary>
    /// Forces the next <see cref="SearchAutoAsync"/> call to re-probe
    /// all providers. Called when a "known available" provider fails
    /// to deliver results.
    /// </summary>
    private void InvalidateProbeCache()
    {
        _lastProbeTime = DateTime.MinValue;
    }

    // ─────────────────────────────────────────────────────────────────
    // Individual Provider Wrappers
    // ─────────────────────────────────────────────────────────────────

    private async Task<SearchResults> SearchWithSearxngAsync(
        string query, WebSearchOptions options, CancellationToken ct)
    {
        var result = await _searxng.SearchAsync(query, options, ct);

        if (result.Results.Count == 0 && result.Errors.Count > 0)
        {
            return result with
            {
                Errors = [..result.Errors, "SearxNG mode is set but the instance may be down."]
            };
        }

        return result;
    }

    private async Task<SearchResults> SearchWithDdgAsync(
        string query, WebSearchOptions options, CancellationToken ct)
    {
        return await _ddg.SearchAsync(query, options, ct);
    }

    private async Task<SearchResults> SearchWithGoogleNewsAsync(
        string query, WebSearchOptions options, CancellationToken ct)
    {
        return await _googleNews.SearchAsync(query, options, ct);
    }

    private static SearchResults ManualModeResult()
    {
        return new SearchResults
        {
            Provider = "Manual",
            Results  = [],
            Errors   = ["Search is in manual mode. Paste URLs directly and use BrowserNavigate to read them."]
        };
    }

    public void Dispose()
    {
        _ddg.Dispose();
        _searxng.Dispose();
        _googleNews.Dispose();
        _probeLock.Dispose();
    }
}
