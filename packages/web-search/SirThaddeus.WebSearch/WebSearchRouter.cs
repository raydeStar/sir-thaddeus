using SirThaddeus.WebSearch.Providers;

namespace SirThaddeus.WebSearch;

// Web Search Router
//
// Orchestrates search requests across available providers.
//
// Probe/fallback order (auto mode):
//   1. SearxNG (if configured and available)
//   2. SearchApi (hosted fallback)
//   3. Google News RSS fallback
//   4. DuckDuckGo HTML (last resort)
//
// Modes:
//   "auto"        - probe in order, cache availability
//   "searxng"     - SearxNG only
//   "search_api"  - hosted search API only
//   "api"         - alias for search_api
//   "ddg_html"    - DDG only (may be rate-limited — DDG blocks heavy automated access)
//                   Also used as last-resort fallback in auto mode
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
            return await SafeIsAvailableAsync(_searxng, cancellationToken);

        if (_mode == "search_api")
            return await SafeIsAvailableAsync(_searchApi, cancellationToken);

        if (_mode == "ddg_html")
            return await SafeIsAvailableAsync(_ddg, cancellationToken);

        if (_mode == "google_news")
            return await SafeIsAvailableAsync(_googleNews, cancellationToken);

        return await SafeIsAvailableAsync(_searxng, cancellationToken)
            || await SafeIsAvailableAsync(_ddg, cancellationToken);
    }

    private async Task<SearchResults> SearchAutoAsync(
        string query,
        WebSearchOptions options,
        CancellationToken ct)
    {
        var diagnostics = new List<SearchDiagnosticEntry>();
        await ProbeProvidersAsync(ct);
        diagnostics.Add(BuildProbeDiagnostic(_searxng.Name, _searxngAvailable));
        diagnostics.Add(BuildProbeDiagnostic(_searchApi.Name, _searchApiAvailable));

        if (_searxngAvailable == true)
        {
            var result = await SafeSearchAsync(_searxng, query, options, ct);
            result = AttachSearchDiagnostic(result, _searxng.Name, "search", diagnostics);
            if (result.Results.Count > 0)
                return result;

            // SearxNG was available during probing but failed to deliver
            // results. Force the next request to probe again.
            InvalidateProbeCache();
            diagnostics = [.. result.Diagnostics];
        }

        if (_searchApiAvailable == true)
        {
            var hostedFallback = await SafeSearchAsync(_searchApi, query, options, ct);
            hostedFallback = AttachSearchDiagnostic(hostedFallback, _searchApi.Name, "search", diagnostics);
            if (hostedFallback.Results.Count > 0)
                return hostedFallback;

            diagnostics = [.. hostedFallback.Diagnostics];
        }

        // DDG is the zero-install default and genuinely searches. Try it
        // BEFORE GoogleNews — the latter's RSS endpoint returns current
        // headlines regardless of query for anything its "generic news"
        // heuristic claims, which silently poisons non-news searches
        // with unrelated current headlines.
        var ddgFallback = await SafeSearchAsync(_ddg, query, options, ct);
        ddgFallback = AttachSearchDiagnostic(ddgFallback, _ddg.Name, "fallback", diagnostics);
        if (ddgFallback.Results.Count > 0)
            return ddgFallback;

        diagnostics = [.. ddgFallback.Diagnostics];

        // Last-resort: GoogleNews RSS. Useful for genuine news asks and
        // when DDG is blocked (anti-bot throttling), but its headlines-
        // mode fallback is off-topic for general queries.
        var googleFallback = await SafeSearchAsync(_googleNews, query, options, ct);
        return AttachSearchDiagnostic(googleFallback, _googleNews.Name, "fallback", diagnostics);
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

            _searxngAvailable = await SafeIsAvailableAsync(_searxng, ct);
            _searchApiAvailable = await SafeIsAvailableAsync(_searchApi, ct);
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
        var result = await SafeSearchAsync(_searxng, query, options, ct);

        if (result.Results.Count == 0 && result.Errors.Count > 0)
        {
            return result with
            {
                Errors = [.. result.Errors, "SearxNG mode is set but the instance may be down."]
            };
        }

        return AttachSearchDiagnostic(result, _searxng.Name, "search");
    }

    private async Task<SearchResults> SearchWithSearchApiAsync(
        string query,
        WebSearchOptions options,
        CancellationToken ct)
    {
        var result = await SafeSearchAsync(_searchApi, query, options, ct);
        return AttachSearchDiagnostic(result, _searchApi.Name, "search");
    }

    private async Task<SearchResults> SearchWithDdgAsync(
        string query,
        WebSearchOptions options,
        CancellationToken ct)
    {
        var result = await SafeSearchAsync(_ddg, query, options, ct);
        return AttachSearchDiagnostic(result, _ddg.Name, "search");
    }

    private async Task<SearchResults> SearchWithGoogleNewsAsync(
        string query,
        WebSearchOptions options,
        CancellationToken ct)
    {
        var result = await SafeSearchAsync(_googleNews, query, options, ct);
        return AttachSearchDiagnostic(result, _googleNews.Name, "search");
    }

    private static async Task<bool> SafeIsAvailableAsync(
        IWebSearchProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.IsAvailableAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<SearchResults> SafeSearchAsync(
        IWebSearchProvider provider,
        string query,
        WebSearchOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.SearchAsync(query, options, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new SearchResults
            {
                Provider = provider.Name,
                Errors = ["Search timed out"]
            };
        }
        catch (Exception ex)
        {
            return new SearchResults
            {
                Provider = provider.Name,
                Errors = [$"{provider.Name} search failed: {ex.GetType().Name}: {ex.Message}"]
            };
        }
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

    private static SearchResults AttachSearchDiagnostic(
        SearchResults result,
        string providerName,
        string phase,
        IReadOnlyList<SearchDiagnosticEntry>? prefixDiagnostics = null)
    {
        var diagnostics = new List<SearchDiagnosticEntry>();
        if (prefixDiagnostics is not null && prefixDiagnostics.Count > 0)
            diagnostics.AddRange(prefixDiagnostics);
        if (result.Diagnostics.Count > 0)
            diagnostics.AddRange(result.Diagnostics);

        diagnostics.Add(new SearchDiagnosticEntry
        {
            Provider = providerName,
            Phase = phase,
            Outcome = DetermineOutcome(result),
            Message = BuildDiagnosticMessage(result),
            ResultCount = result.Results.Count
        });

        return result with { Diagnostics = diagnostics };
    }

    private static SearchDiagnosticEntry BuildProbeDiagnostic(string providerName, bool? available)
    {
        var outcome = available == true ? "available" : "unavailable";
        return new SearchDiagnosticEntry
        {
            Provider = providerName,
            Phase = "probe",
            Outcome = outcome,
            Message = $"probe returned {outcome}",
            ResultCount = 0
        };
    }

    private static string DetermineOutcome(SearchResults result)
    {
        if (result.Results.Count > 0)
            return "results";

        if (result.Errors.Count > 0)
            return "error";

        return "no_results";
    }

    private static string BuildDiagnosticMessage(SearchResults result)
    {
        if (result.Errors.Count == 0)
            return result.Results.Count > 0
                ? $"returned {result.Results.Count} result(s)"
                : "returned no results";

        return string.Join("; ", result.Errors);
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
