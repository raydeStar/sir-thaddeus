namespace SirThaddeus.Memory;

/// <summary>
/// Abstraction over the memory data layer. The retrieval engine
/// depends on this interface, not on a specific storage backend.
///
/// Contract:
///   - Implementations must exclude is_deleted=1 items.
///   - Implementations must exclude sensitivity=secret items.
///   - LexicalScore in returned candidates should be normalized to [0, 1].
/// </summary>
public interface IMemoryStore
{
    /// <summary>
    /// Searches for relevant facts using keyword/lexical matching.
    /// Returns candidates ordered by descending lexical relevance.
    /// </summary>
    Task<IReadOnlyList<StoreCandidate<MemoryFact>>> SearchFactsAsync(
        string query, int maxResults, CancellationToken ct = default);

    /// <summary>
    /// Searches for relevant events using keyword/lexical matching.
    /// Returns candidates ordered by descending lexical relevance.
    /// </summary>
    Task<IReadOnlyList<StoreCandidate<MemoryEvent>>> SearchEventsAsync(
        string query, int maxResults, CancellationToken ct = default);

    /// <summary>
    /// Searches for relevant chunks using FTS5/BM25 (or equivalent).
    /// Returns candidates ordered by descending relevance, with
    /// normalized BM25 scores in [0, 1].
    /// </summary>
    Task<IReadOnlyList<StoreCandidate<MemoryChunk>>> SearchChunksAsync(
        string query, int maxResults, CancellationToken ct = default);

    // ── Write operations ───────────────────────────────────────────
    // All writes use upsert semantics (INSERT OR REPLACE) keyed on
    // the item's primary ID, making them idempotent and retry-safe.

    /// <summary>
    /// Upserts a fact. If a fact with the same memory_id exists,
    /// it is replaced entirely.
    /// </summary>
    Task StoreFactAsync(MemoryFact fact, CancellationToken ct = default);

    /// <summary>
    /// Upserts an event. If an event with the same event_id exists,
    /// it is replaced entirely.
    /// </summary>
    Task StoreEventAsync(MemoryEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Upserts a chunk. If a chunk with the same chunk_id exists,
    /// it is replaced entirely.
    /// </summary>
    Task StoreChunkAsync(MemoryChunk chunk, CancellationToken ct = default);

    // ── Browse operations ──────────────────────────────────────────
    // Paginated listing for UI browsers. These do NOT use FTS — they
    // return raw rows with optional LIKE filtering across text columns.
    // Unlike search methods, these return domain records directly
    // (no scoring wrapper), plus the total matching count for pagination.

    /// <summary>
    /// Lists facts with optional keyword filter. Ordered by updated_at DESC.
    /// </summary>
    Task<(IReadOnlyList<MemoryFact> Items, int TotalCount)> ListFactsAsync(
        string? filter, int skip, int take, CancellationToken ct = default);

    /// <summary>
    /// Lists events with optional keyword filter. Ordered by when_iso DESC.
    /// </summary>
    Task<(IReadOnlyList<MemoryEvent> Items, int TotalCount)> ListEventsAsync(
        string? filter, int skip, int take, CancellationToken ct = default);

    /// <summary>
    /// Lists chunks with optional keyword filter. Ordered by when_iso DESC.
    /// </summary>
    Task<(IReadOnlyList<MemoryChunk> Items, int TotalCount)> ListChunksAsync(
        string? filter, int skip, int take, CancellationToken ct = default);

    // ── Delete operations ───────────────────────────────────────────
    // Soft-delete: sets is_deleted = 1. Idempotent — deleting an
    // already-deleted item is a no-op.

    /// <summary>Soft-deletes a fact by memory_id.</summary>
    Task DeleteFactAsync(string memoryId, CancellationToken ct = default);

    /// <summary>Soft-deletes an event by event_id.</summary>
    Task DeleteEventAsync(string eventId, CancellationToken ct = default);

    /// <summary>Soft-deletes a chunk by chunk_id.</summary>
    Task DeleteChunkAsync(string chunkId, CancellationToken ct = default);

    /// <summary>
    /// Ensures the backing store's schema exists. Idempotent —
    /// safe (and recommended) to call on every startup.
    /// </summary>
    Task EnsureSchemaAsync(CancellationToken ct = default);
}
