namespace SirThaddeus.WebSearch;

// ─────────────────────────────────────────────────────────────────────────
// Search Provider Contract
//
// Each provider implements this interface. The WebSearchRouter picks the
// best available provider at runtime based on configuration and
// reachability probes.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// A pluggable web search backend.
/// </summary>
public interface IWebSearchProvider
{
    /// <summary>
    /// Human-readable name of this provider (e.g. "DuckDuckGo", "SearxNG").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Executes a web search and returns aggregated results.
    /// Implementations must respect <paramref name="options"/> bounds.
    /// </summary>
    Task<SearchResults> SearchAsync(
        string query,
        WebSearchOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lightweight check to see if this provider is currently reachable.
    /// Should complete quickly (1-2 seconds max).
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
