namespace SirThaddeus.WebSearch;

// ─────────────────────────────────────────────────────────────────────────
// Web Search Models
//
// Shared data types for all search providers. Kept flat and serializable
// so they can cross the MCP boundary without ceremony.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// A single search result from any provider.
/// </summary>
public sealed record SearchResult
{
    /// <summary>Page title as reported by the search engine.</summary>
    public required string Title { get; init; }

    /// <summary>Canonical URL of the result.</summary>
    public required string Url { get; init; }

    /// <summary>Short text excerpt / snippet from the search engine.</summary>
    public string Snippet { get; init; } = string.Empty;

    /// <summary>Domain name (e.g. "techcrunch.com").</summary>
    public string Source { get; init; } = string.Empty;
}

/// <summary>
/// Aggregated search response from a provider.
/// </summary>
public sealed record SearchResults
{
    /// <summary>The results returned by the search.</summary>
    public IReadOnlyList<SearchResult> Results { get; init; } = [];

    /// <summary>Which provider actually served these results.</summary>
    public string Provider { get; init; } = "unknown";

    /// <summary>Non-fatal issues encountered during the search.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>
/// Options controlling a search request.
/// </summary>
public sealed record WebSearchOptions
{
    /// <summary>Maximum number of results to return (capped by provider limits).</summary>
    public int MaxResults { get; init; } = 5;

    /// <summary>HTTP timeout for the search request in milliseconds.</summary>
    public int TimeoutMs { get; init; } = 8_000;

    /// <summary>
    /// Recency filter — limits results to a time window.
    /// Accepted values: "day", "week", "month", "any" (default).
    /// Providers that don't support server-side filtering silently
    /// ignore this field.
    /// </summary>
    public string Recency { get; init; } = "any";
}
