namespace SirThaddeus.Agent.Validation.Completion;

/// <summary>
/// Defines what kind of evidence (source URLs, citations, etc.)
/// a completion contract requires to consider a response trustworthy.
/// </summary>
public sealed record EvidenceRequirement
{
    /// <summary>
    /// Minimum number of source URLs required.
    /// Zero means no URL evidence is needed (e.g. chat-only, math).
    /// </summary>
    public int MinSourceUrls { get; init; }

    /// <summary>
    /// Whether the response must cite at least one source by name
    /// (not necessarily a URL — could be "according to Wikipedia").
    /// </summary>
    public bool RequiresNamedSource { get; init; }

    /// <summary>
    /// When true, the checker treats tool results that contain
    /// only error messages as failing the evidence requirement,
    /// even if a URL is technically present in the error payload.
    /// </summary>
    public bool RejectErrorOnlyResults { get; init; } = true;

    /// <summary>No evidence required (suitable for chat, math, utility).</summary>
    public static readonly EvidenceRequirement None = new();

    /// <summary>At least one source URL (suitable for fact/news lookups).</summary>
    public static readonly EvidenceRequirement AtLeastOneUrl = new()
    {
        MinSourceUrls = 1,
        RequiresNamedSource = false
    };

    /// <summary>At least one named source and one URL (suitable for deep dives).</summary>
    public static readonly EvidenceRequirement NamedWithUrl = new()
    {
        MinSourceUrls = 1,
        RequiresNamedSource = true
    };
}
