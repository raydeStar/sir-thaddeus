namespace SirThaddeus.Memory;

/// <summary>
/// Deterministic scoring functions for memory retrieval candidates.
/// All outputs are clamped to [0, 1].
/// </summary>
public static class Scoring
{
    /// <summary>
    /// Composite score for a fact or event.
    /// Formula: 0.55 * lexical + 0.30 * recency + 0.15 * confidence
    /// </summary>
    public static double ScoreFactOrEvent(
        double lexicalScore, double recencyScore, double confidence)
    {
        return Thresholds.FactLexWeight     * Math.Clamp(lexicalScore, 0, 1)
             + Thresholds.FactRecencyWeight * Math.Clamp(recencyScore, 0, 1)
             + Thresholds.FactConfWeight    * Math.Clamp(confidence, 0, 1);
    }

    /// <summary>
    /// Composite score for a chunk.
    /// Primary signal is either embedding similarity or BM25 score.
    /// Formula: 0.75 * primary + 0.25 * recency
    /// </summary>
    public static double ScoreChunk(double primaryScore, double recencyScore)
    {
        return Thresholds.ChunkPrimaryWeight * Math.Clamp(primaryScore, 0, 1)
             + Thresholds.ChunkRecencyWeight * Math.Clamp(recencyScore, 0, 1);
    }

    /// <summary>
    /// Computes a recency score from a timestamp.
    ///   - Items within the last 7 days  → 1.0
    ///   - Items older than 90 days      → 0.0
    ///   - Linear decay between
    /// </summary>
    public static double ComputeRecency(DateTimeOffset? timestamp)
    {
        if (timestamp is null)
            return 0.0;

        var ageDays = (DateTimeOffset.UtcNow - timestamp.Value).TotalDays;
        if (ageDays < 0) ageDays = 0;   // Future dates treated as "now"

        if (ageDays <= Thresholds.FullScoreDays)
            return 1.0;

        if (ageDays >= Thresholds.FadeOutDays)
            return 0.0;

        return 1.0 - (ageDays - Thresholds.FullScoreDays)
                    / (Thresholds.FadeOutDays - Thresholds.FullScoreDays);
    }

    /// <summary>
    /// Cosine similarity between two embedding vectors.
    /// Returns 0.0 if either is null, empty, or mismatched in dimension.
    /// </summary>
    public static double CosineSimilarity(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length != b.Length || a.Length == 0)
            return 0.0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot   += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator < 1e-10 ? 0.0 : dot / denominator;
    }
}
