using System.Security.Cryptography;
using System.Text;

namespace SirThaddeus.Agent.Search;

// ─────────────────────────────────────────────────────────────────────────
// Search Session — Formal State for the Search Pipeline
//
// Replaces the scattered _lastSearchSources / _lastSearchRecency fields
// with a typed state object that survives history trimming and provides
// the anchor for follow-up resolution.
//
// Lifetime:
//   Created per-conversation. Cleared on conversation reset or when
//   stale (> SessionTtl since last update). Pinned independently
//   from chat history — trimming _history does NOT affect session state.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Pipeline mode — determines which search pipeline handles the request.
/// </summary>
public enum SearchMode
{
    /// <summary>Multiple sources, story clustering, recency-biased.</summary>
    NewsAggregate,

    /// <summary>Canonical answer, entity-focused, stable references.</summary>
    WebFactFind,

    /// <summary>Deep dive or more-sources on a prior result.</summary>
    FollowUp
}

/// <summary>
/// Sub-mode for <see cref="SearchMode.FollowUp"/> — determines whether
/// we open a prior source or search for additional coverage.
/// </summary>
public enum FollowUpBranch
{
    /// <summary>Browse the prior source, extract, and summarize.</summary>
    DeepDive,

    /// <summary>Search for related coverage using source title + entity.</summary>
    MoreSources
}

/// <summary>
/// A single source from search results, identified by a stable hash.
/// </summary>
public sealed record SourceItem
{
    /// <summary>
    /// Truncated SHA-256 of the normalized URL. Stable across runs.
    /// </summary>
    public string SourceId { get; init; } = "";

    public required string Url   { get; init; }
    public required string Title { get; init; }
    public string Domain         { get; init; } = "";
    public DateTimeOffset? PublishedAt    { get; init; }
    public int?   ExtractedWordCount      { get; init; }
    public string Snippet                 { get; init; } = "";

    /// <summary>
    /// Computes a stable, truncated SHA-256 hash from a normalized URL.
    /// </summary>
    public static string ComputeSourceId(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "empty";

        var normalized = url.Trim().ToLowerInvariant()
            .TrimEnd('/');

        var bytes  = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}

/// <summary>
/// A group of related sources clustered by story similarity.
/// Produced by <see cref="StoryClustering"/> during the news pipeline.
/// </summary>
public sealed record StoryCluster
{
    /// <summary>Representative title for this story group.</summary>
    public required string RepresentativeTitle { get; init; }

    /// <summary>All sources covering this story.</summary>
    public required IReadOnlyList<SourceItem> Sources { get; init; }
}

/// <summary>
/// Formal search state — pinned independently from conversation history.
/// Survives history trimming and provides the anchor for follow-ups.
/// </summary>
public sealed class SearchSession
{
    /// <summary>Time-to-live — sessions older than this are considered stale.</summary>
    public static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(15);

    public SearchMode?  LastMode                 { get; set; }
    public string?      LastQuery                { get; set; }
    public string?      LastRecency              { get; set; }

    // ── Entity resolution cache ──────────────────────────────────────
    public string?      LastEntityCanonical      { get; set; }
    public string?      LastEntityType           { get; set; }
    public string?      LastEntityDisambiguation { get; set; }

    // ── Source tracking ──────────────────────────────────────────────
    public List<SourceItem> LastResults   { get; set; } = [];
    public string?          PrimarySourceId  { get; set; }
    public string?          SelectedSourceId { get; set; }

    // ── Story clusters (news pipeline only) ──────────────────────────
    public List<StoryCluster> LastClusters { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True if the session has results and hasn't expired.</summary>
    public bool HasRecentResults(DateTimeOffset now) =>
        LastResults.Count > 0 &&
        (now - UpdatedAt) < SessionTtl;

    /// <summary>
    /// Updates the session after a search completes. Timestamps and
    /// sets the primary source to the first result if not already set.
    /// </summary>
    public void RecordSearchResults(
        SearchMode mode,
        string query,
        string recency,
        IReadOnlyList<SourceItem> results,
        DateTimeOffset now)
    {
        LastMode    = mode;
        LastQuery   = query;
        LastRecency = recency;
        LastResults = [..results];
        UpdatedAt   = now;

        PrimarySourceId = results.Count > 0
            ? results[0].SourceId
            : null;

        // Don't clear entity — it may carry over to follow-ups.
        // Don't clear SelectedSourceId — UI may have set it.
    }

    /// <summary>Appends new results without replacing existing ones.</summary>
    public void AppendResults(IReadOnlyList<SourceItem> newResults, DateTimeOffset now)
    {
        var existingIds = new HashSet<string>(LastResults.Select(r => r.SourceId));
        foreach (var r in newResults)
        {
            if (existingIds.Add(r.SourceId))
                LastResults.Add(r);
        }
        UpdatedAt = now;
    }

    /// <summary>
    /// Resets all state. Called on conversation clear or new session.
    /// </summary>
    public void Clear()
    {
        LastMode                 = null;
        LastQuery                = null;
        LastRecency              = null;
        LastEntityCanonical      = null;
        LastEntityType           = null;
        LastEntityDisambiguation = null;
        LastResults.Clear();
        LastClusters.Clear();
        PrimarySourceId  = null;
        SelectedSourceId = null;
        UpdatedAt        = default;
    }
}
