using System.Text.Json.Serialization;

namespace SirThaddeus.Memory;

// ─────────────────────────────────────────────────────────────────────────
// Enums
// ─────────────────────────────────────────────────────────────────────────

public enum Sensitivity { Public, Personal, Secret }

public enum MemoryIntent { Personal, Technical, Planning, General }

public enum RelevanceDecision { Allow, AllowSilent, Block }

// ─────────────────────────────────────────────────────────────────────────
// Structured Memory Items
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// A single subject-predicate-object fact stored in memory_facts.
/// </summary>
public sealed record MemoryFact
{
    public required string MemoryId   { get; init; }

    /// <summary>
    /// Optional link to the profile_cards row that owns this fact.
    /// Null for legacy/unscoped facts. When set, retrieval and
    /// conflict detection are scoped to this profile.
    /// </summary>
    public string?         ProfileId   { get; init; }

    public required string Subject    { get; init; }
    public required string Predicate  { get; init; }

    [JsonPropertyName("object")]
    public required string Object     { get; init; }

    public double          Confidence  { get; init; } = 1.0;
    public Sensitivity     Sensitivity { get; init; } = Sensitivity.Public;
    public DateTimeOffset  CreatedAt   { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset  UpdatedAt   { get; init; } = DateTimeOffset.UtcNow;
    public string?         SourceRef   { get; init; }
}

/// <summary>
/// A timestamped event stored in memory_events.
/// </summary>
public sealed record MemoryEvent
{
    public required string  EventId     { get; init; }

    /// <summary>
    /// Optional link to the profile_cards row that owns this event.
    /// Null for legacy/unscoped events.
    /// </summary>
    public string?          ProfileId   { get; init; }

    public required string  Type        { get; init; }
    public required string  Title       { get; init; }
    public string?          Summary     { get; init; }
    public DateTimeOffset?  WhenIso     { get; init; }
    public double           Confidence  { get; init; } = 1.0;
    public Sensitivity      Sensitivity { get; init; } = Sensitivity.Public;
    public string?          SourceRef   { get; init; }
}

/// <summary>
/// A text chunk (conversation fragment or document excerpt) with
/// optional embedding vector for semantic retrieval.
/// </summary>
public sealed record MemoryChunk
{
    public required string  ChunkId     { get; init; }
    public required string  SourceType  { get; init; }   // conversation | doc
    public string?          SourceRef   { get; init; }
    public required string  Text        { get; init; }
    public DateTimeOffset?  WhenIso     { get; init; }
    public Sensitivity      Sensitivity { get; init; } = Sensitivity.Public;
    public float[]?         Embedding   { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────
// Profile Cards
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// A lightweight identity card for the user or someone they mention.
/// <c>Kind</c> is "user" for the primary user, "person" for others.
/// <c>ProfileJson</c> holds structured fields (preferred name, pronouns,
/// timezone, style prefs, privacy lists) as a JSON blob so the schema
/// doesn't migrate for every new field.
/// </summary>
public sealed record ProfileCard
{
    public required string  ProfileId    { get; init; }
    public string           Kind         { get; init; } = "user";
    public required string  DisplayName  { get; init; }
    public string?          Relationship { get; init; }
    public string?          Aliases      { get; init; }   // semicolon-delimited
    public string           ProfileJson  { get; init; } = "{}";
    public DateTimeOffset   UpdatedAt    { get; init; } = DateTimeOffset.UtcNow;
}

// ─────────────────────────────────────────────────────────────────────────
// Memory Nuggets
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// A short, atomic, composable personal fact that can be injected into
/// the LLM context. Not a full memory — more like a sticky note.
///
/// Scoring: <c>pin_level</c> (user-pinned), <c>weight</c> (global
/// importance), <c>use_count</c> + <c>last_used_at</c> (reinforcement
/// and recency). Tags are semicolon-delimited for cheap LIKE filtering
/// (e.g. <c>;identity;preference;</c>).
/// </summary>
public sealed record MemoryNugget
{
    public required string  NuggetId    { get; init; }
    public required string  Text        { get; init; }
    public string?          Tags        { get; init; }     // ;identity;preference;
    public double           Weight      { get; init; } = 0.65;
    public int              PinLevel    { get; init; }     // 0=normal, 1=pinned, 2=system
    public string           Sensitivity { get; init; } = "low";  // low|med|high
    public int              UseCount    { get; init; }
    public DateTimeOffset?  LastUsedAt  { get; init; }
    public DateTimeOffset   CreatedAt   { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Nugget sensitivity levels. Kept as strings in the DB for readability.
/// </summary>
public static class NuggetSensitivity
{
    public const string Low    = "low";
    public const string Medium = "med";
    public const string High   = "high";
}

// ─────────────────────────────────────────────────────────────────────────
// Scored Candidate Wrapper
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// A memory item annotated with retrieval scores and a relevance decision.
/// Produced by the scoring/gating pipeline; consumed by the formatter.
/// </summary>
public sealed record ScoredCandidate<T>
{
    public required T          Item            { get; init; }
    public double              Score           { get; init; }
    public double              LexicalScore    { get; init; }
    public double              RecencyScore    { get; init; }
    public double              SimilarityScore { get; init; }
    public RelevanceDecision   Decision        { get; init; } = RelevanceDecision.Block;
}

// ─────────────────────────────────────────────────────────────────────────
// Memory Pack (final retrieval output)
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// The assembled retrieval result: facts, events, and context chunks
/// ready for injection into the LLM system prompt.
/// </summary>
public sealed record MemoryPack
{
    public IReadOnlyList<MemoryFact>  Facts     { get; init; } = [];
    public IReadOnlyList<MemoryEvent> Events    { get; init; } = [];
    public IReadOnlyList<MemoryChunk> Chunks    { get; init; } = [];
    public string                     Notes     { get; init; } = "";
    public IReadOnlyList<string>      Citations { get; init; } = [];

    /// <summary>
    /// Pre-formatted text block for direct injection into the system prompt.
    /// Empty when no relevant memories were found.
    /// </summary>
    public string PackText { get; init; } = "";

    /// <summary>
    /// True if the pack contains at least one retrieved item.
    /// </summary>
    public bool HasContent => Facts.Count > 0 || Events.Count > 0 || Chunks.Count > 0;
}

// ─────────────────────────────────────────────────────────────────────────
// Retrieval Context
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Optional context provided alongside the query to influence retrieval.
/// </summary>
public sealed record RetrievalContext
{
    public string?                ConversationId  { get; init; }
    public IReadOnlyList<string>? RecentMessages  { get; init; }
    public string?                Mode            { get; init; }  // chat | planning | technical
}

// ─────────────────────────────────────────────────────────────────────────
// Store Candidate (returned by IMemoryStore)
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// A candidate returned from the data store with its raw lexical/BM25 score.
/// The retrieval engine computes the final composite score from this.
/// </summary>
public sealed record StoreCandidate<T>(T Item, double LexicalScore);

// ─────────────────────────────────────────────────────────────────────────
// Shared Parsing Utilities
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Parsing helpers shared across storage and tool layers.
/// Avoids duplicating enum resolution logic in multiple assemblies.
/// </summary>
public static class MemoryParsing
{
    /// <summary>
    /// Resolves a sensitivity string ("public", "personal", "secret")
    /// to its <see cref="Sensitivity"/> enum value. Defaults to
    /// <see cref="Sensitivity.Public"/> for unknown/null inputs.
    /// </summary>
    public static Sensitivity ParseSensitivity(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "personal" => Sensitivity.Personal,
            "secret"   => Sensitivity.Secret,
            _          => Sensitivity.Public
        };
}
