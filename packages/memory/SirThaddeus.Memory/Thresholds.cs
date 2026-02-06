namespace SirThaddeus.Memory;

/// <summary>
/// V1 default thresholds for the retrieval system.
/// Tune these based on real-world usage — they're intentionally conservative.
/// </summary>
public static class Thresholds
{
    // ── Retrieval limits ─────────────────────────────────────────────
    public const int ChunksRetrieve   = 20;
    public const int ChunksInject     = 8;
    public const int FactsInject      = 6;
    public const int EventsInject     = 4;

    // ── Similarity thresholds ────────────────────────────────────────
    // Used to decide whether a personal item is relevant enough to
    // surface during a technical query (anti-creepiness gate).
    public const double SimHigh = 0.80;
    public const double LexHigh = 0.65;

    // ── Scoring weights (facts / events) ─────────────────────────────
    public const double FactLexWeight     = 0.55;
    public const double FactRecencyWeight = 0.30;
    public const double FactConfWeight    = 0.15;

    // ── Scoring weights (chunks) ─────────────────────────────────────
    public const double ChunkPrimaryWeight = 0.75;   // embedding sim or BM25
    public const double ChunkRecencyWeight = 0.25;

    // ── Recency curve ────────────────────────────────────────────────
    // Items within FullScoreDays get 1.0.
    // Items older than FadeOutDays get 0.0.
    // Linear decay between the two.
    public const double FullScoreDays = 7;
    public const double FadeOutDays   = 90;

    // ── Chunk excerpt cap ────────────────────────────────────────────
    public const int ChunkExcerptMaxChars = 350;
}
