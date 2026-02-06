namespace SirThaddeus.Memory;

/// <summary>
/// Per-candidate relevance gate: decides whether a memory item should
/// be injected into the prompt, kept silently, or blocked entirely.
///
/// Core rule: personal items are blocked during technical queries
/// unless they exceed the high-similarity threshold.
/// </summary>
public static class RelevanceGate
{
    /// <summary>
    /// Evaluates a single candidate against the current intent.
    /// </summary>
    /// <param name="sensitivity">The item's sensitivity level.</param>
    /// <param name="intent">The classified query intent.</param>
    /// <param name="lexicalScore">Lexical/BM25 relevance score (0..1).</param>
    /// <param name="similarityScore">Embedding cosine similarity (0..1), or 0 if unavailable.</param>
    public static RelevanceDecision Evaluate(
        Sensitivity sensitivity,
        MemoryIntent intent,
        double lexicalScore,
        double similarityScore)
    {
        // ── Secret items are always blocked (V1) ─────────────────────
        if (sensitivity == Sensitivity.Secret)
            return RelevanceDecision.Block;

        // ── Public items: allow if they have any relevance ───────────
        if (sensitivity == Sensitivity.Public)
            return RelevanceDecision.Allow;

        // ── Personal items: intent-dependent gating ──────────────────
        return intent switch
        {
            MemoryIntent.Personal => RelevanceDecision.Allow,
            MemoryIntent.Planning => RelevanceDecision.Allow,
            MemoryIntent.Technical => EvaluatePersonalForTechnical(lexicalScore, similarityScore),

            // General queries: allow personal only if clearly relevant
            MemoryIntent.General => lexicalScore > Thresholds.LexHigh
                                    || similarityScore > Thresholds.SimHigh
                ? RelevanceDecision.Allow
                : RelevanceDecision.AllowSilent,

            _ => RelevanceDecision.Block
        };
    }

    /// <summary>
    /// During technical queries, personal items need to clear a high bar.
    /// Borderline items are downgraded to ALLOW_SILENT to prevent
    /// personal details from leaking into a code discussion.
    /// </summary>
    private static RelevanceDecision EvaluatePersonalForTechnical(
        double lexicalScore, double similarityScore)
    {
        // Strong topical match: allow injection
        if (similarityScore >= Thresholds.SimHigh)
            return RelevanceDecision.Allow;

        if (lexicalScore >= Thresholds.LexHigh)
            return RelevanceDecision.Allow;

        // Mild relevance: keep for internal reasoning, don't inject
        if (lexicalScore > 0.3 || similarityScore > 0.5)
            return RelevanceDecision.AllowSilent;

        return RelevanceDecision.Block;
    }
}
