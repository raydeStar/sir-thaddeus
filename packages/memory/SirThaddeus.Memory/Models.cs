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
