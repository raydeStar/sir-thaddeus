namespace SirThaddeus.Agent.Dialogue;

/// <summary>
/// Compact, in-memory dialogue state used for deterministic continuity.
/// Keep payloads small so snapshots stay cheap to render and persist.
/// </summary>
public sealed record DialogueState
{
    public string Topic { get; init; } = "";
    public string? LocationName { get; init; }
    public string? CountryCode { get; init; }
    public string? RegionCode { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? TimeScope { get; init; }
    public bool ContextLocked { get; init; }
    public bool LocationInferred { get; init; }
    public bool GeocodeMismatch { get; init; }
    public string RollingSummary { get; init; } = "";
    public DateTimeOffset UpdatedAtUtc { get; init; }

    public DialogueContextSnapshot ToSnapshot() => new()
    {
        Topic = string.IsNullOrWhiteSpace(Topic) ? null : Topic,
        Location = string.IsNullOrWhiteSpace(LocationName) ? null : LocationName,
        TimeScope = string.IsNullOrWhiteSpace(TimeScope) ? null : TimeScope,
        ContextLocked = ContextLocked,
        GeocodeMismatch = GeocodeMismatch,
        LocationInferred = LocationInferred
    };
}

/// <summary>
/// Minimal context payload surfaced to UI and responses.
/// </summary>
public sealed record DialogueContextSnapshot
{
    public string? Topic { get; init; }
    public string? Location { get; init; }
    public string? TimeScope { get; init; }
    public bool ContextLocked { get; init; }
    public bool GeocodeMismatch { get; init; }
    public bool LocationInferred { get; init; }
}
