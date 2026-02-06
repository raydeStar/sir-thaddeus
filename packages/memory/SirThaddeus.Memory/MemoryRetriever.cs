using System.Text;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Memory;

/// <summary>
/// Full retrieval pipeline: classify intent → fetch candidates → score →
/// apply anti-creepiness gate → rank → dedupe → cap → format MemoryPack.
///
/// Deterministic by design. The only optional non-determinism is the
/// embedding rerank step, which degrades gracefully to BM25-only.
/// </summary>
public sealed class MemoryRetriever
{
    private readonly IMemoryStore _store;
    private readonly IEmbeddingClient? _embeddings;

    public MemoryRetriever(IMemoryStore store, IEmbeddingClient? embeddings = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embeddings = embeddings;
    }

    // ─────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a complete MemoryPack from the query and optional context.
    /// This is the primary entry point for the retrieval system.
    /// </summary>
    public async Task<MemoryPack> BuildMemoryPackAsync(
        string query,
        RetrievalContext? context = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return EmptyPack("Empty query — no retrieval performed.");

        // ── Step 0: Classify intent ──────────────────────────────────
        var intent = IntentClassifier.Classify(query);

        // ── Step 1: Retrieve candidates ──────────────────────────────
        // Fetch more than we inject so the gate has room to filter.
        var factCandidates  = await _store.SearchFactsAsync(
            query, Thresholds.FactsInject * 3, ct);
        var eventCandidates = await _store.SearchEventsAsync(
            query, Thresholds.EventsInject * 3, ct);
        var chunkCandidates = await _store.SearchChunksAsync(
            query, Thresholds.ChunksRetrieve, ct);

        // ── Optional: embed query for semantic reranking ─────────────
        float[]? queryEmbedding = null;
        if (_embeddings is not null)
            queryEmbedding = await _embeddings.EmbedAsync(query, ct);

        // ── Step 1.5: Score all candidates ───────────────────────────
        var scoredFacts  = ScoreFacts(factCandidates, intent);
        var scoredEvents = ScoreEvents(eventCandidates, intent);
        var scoredChunks = ScoreChunks(chunkCandidates, intent, queryEmbedding);

        // ── Step 2: Relevance gate — keep only ALLOW ─────────────────
        var allowedFacts  = scoredFacts
            .Where(c => c.Decision == RelevanceDecision.Allow).ToList();
        var allowedEvents = scoredEvents
            .Where(c => c.Decision == RelevanceDecision.Allow).ToList();
        var allowedChunks = scoredChunks
            .Where(c => c.Decision == RelevanceDecision.Allow).ToList();

        // ── Step 3: Rank + dedupe + cap ──────────────────────────────
        var rankedFacts  = DedupeAndCap(
            allowedFacts, c => c.Item.SourceRef, Thresholds.FactsInject);
        var rankedEvents = DedupeAndCap(
            allowedEvents, c => c.Item.SourceRef, Thresholds.EventsInject);
        var rankedChunks = DedupeAndCap(
            allowedChunks, c => c.Item.SourceRef, Thresholds.ChunksInject);

        // ── Edge case: conflicting facts ─────────────────────────────
        var notes = DetectConflicts(rankedFacts);

        // ── Step 4: Assemble MemoryPack ──────────────────────────────
        var facts    = rankedFacts.Select(c => c.Item).ToList();
        var events   = rankedEvents.Select(c => c.Item).ToList();
        var chunks   = rankedChunks.Select(c => c.Item).ToList();
        var citations = CollectCitations(facts, events, chunks);

        if (facts.Count == 0 && events.Count == 0 && chunks.Count == 0)
            return EmptyPack("No relevant memory found.");

        var packText = FormatPackText(facts, events, chunks, citations, notes);

        return new MemoryPack
        {
            Facts     = facts,
            Events    = events,
            Chunks    = chunks,
            Notes     = notes,
            Citations = citations,
            PackText  = packText
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Scoring
    // ─────────────────────────────────────────────────────────────────

    private static List<ScoredCandidate<MemoryFact>> ScoreFacts(
        IReadOnlyList<StoreCandidate<MemoryFact>> candidates,
        MemoryIntent intent)
    {
        return candidates.Select(c =>
        {
            var recency  = Scoring.ComputeRecency(c.Item.UpdatedAt);
            var score    = Scoring.ScoreFactOrEvent(c.LexicalScore, recency, c.Item.Confidence);
            var decision = RelevanceGate.Evaluate(
                c.Item.Sensitivity, intent, c.LexicalScore, similarityScore: 0.0);

            return new ScoredCandidate<MemoryFact>
            {
                Item            = c.Item,
                Score           = score,
                LexicalScore    = c.LexicalScore,
                RecencyScore    = recency,
                SimilarityScore = 0.0,
                Decision        = decision
            };
        })
        .OrderByDescending(c => c.Score)
        .ToList();
    }

    private static List<ScoredCandidate<MemoryEvent>> ScoreEvents(
        IReadOnlyList<StoreCandidate<MemoryEvent>> candidates,
        MemoryIntent intent)
    {
        return candidates.Select(c =>
        {
            var recency  = Scoring.ComputeRecency(c.Item.WhenIso);
            var score    = Scoring.ScoreFactOrEvent(c.LexicalScore, recency, c.Item.Confidence);
            var decision = RelevanceGate.Evaluate(
                c.Item.Sensitivity, intent, c.LexicalScore, similarityScore: 0.0);

            return new ScoredCandidate<MemoryEvent>
            {
                Item            = c.Item,
                Score           = score,
                LexicalScore    = c.LexicalScore,
                RecencyScore    = recency,
                SimilarityScore = 0.0,
                Decision        = decision
            };
        })
        .OrderByDescending(c => c.Score)
        .ToList();
    }

    private List<ScoredCandidate<MemoryChunk>> ScoreChunks(
        IReadOnlyList<StoreCandidate<MemoryChunk>> candidates,
        MemoryIntent intent,
        float[]? queryEmbedding)
    {
        return candidates.Select(c =>
        {
            var recency = Scoring.ComputeRecency(c.Item.WhenIso);

            // If we have embeddings for both query and chunk, use cosine sim
            double similarity   = 0.0;
            double primaryScore = c.LexicalScore;

            if (queryEmbedding is not null && c.Item.Embedding is not null)
            {
                similarity   = Scoring.CosineSimilarity(queryEmbedding, c.Item.Embedding);
                primaryScore = similarity;   // Embeddings take priority over BM25
            }

            var score    = Scoring.ScoreChunk(primaryScore, recency);
            var decision = RelevanceGate.Evaluate(
                c.Item.Sensitivity, intent, c.LexicalScore, similarity);

            return new ScoredCandidate<MemoryChunk>
            {
                Item            = c.Item,
                Score           = score,
                LexicalScore    = c.LexicalScore,
                RecencyScore    = recency,
                SimilarityScore = similarity,
                Decision        = decision
            };
        })
        .OrderByDescending(c => c.Score)
        .ToList();
    }

    // ─────────────────────────────────────────────────────────────────
    // Dedupe + Cap
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deduplicates by source_ref (same source → same memory), then
    /// caps to the maximum injection count. Preserves score ordering.
    /// </summary>
    private static List<ScoredCandidate<T>> DedupeAndCap<T>(
        List<ScoredCandidate<T>> candidates,
        Func<ScoredCandidate<T>, string?> sourceRefSelector,
        int maxItems)
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ScoredCandidate<T>>();

        foreach (var c in candidates)
        {
            var sourceRef = sourceRefSelector(c);
            if (!string.IsNullOrEmpty(sourceRef) && !seen.Add(sourceRef))
                continue;   // Duplicate source — skip

            result.Add(c);
            if (result.Count >= maxItems)
                break;
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────
    // Conflict Detection
    // ─────────────────────────────────────────────────────────────────

    private static string DetectConflicts(List<ScoredCandidate<MemoryFact>> facts)
    {
        var hasConflict = facts
            .GroupBy(f => (
                f.Item.Subject.ToLowerInvariant(),
                f.Item.Predicate.ToLowerInvariant()))
            .Any(g => g.Count() > 1);

        return hasConflict
            ? "Potential conflict detected in retrieved facts."
            : "";
    }

    // ─────────────────────────────────────────────────────────────────
    // Citations
    // ─────────────────────────────────────────────────────────────────

    private static List<string> CollectCitations(
        IReadOnlyList<MemoryFact> facts,
        IReadOnlyList<MemoryEvent> events,
        IReadOnlyList<MemoryChunk> chunks)
    {
        var citations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in facts)
            if (!string.IsNullOrEmpty(f.SourceRef)) citations.Add(f.SourceRef);
        foreach (var e in events)
            if (!string.IsNullOrEmpty(e.SourceRef)) citations.Add(e.SourceRef);
        foreach (var c in chunks)
            if (!string.IsNullOrEmpty(c.SourceRef)) citations.Add(c.SourceRef);

        return [.. citations];
    }

    // ─────────────────────────────────────────────────────────────────
    // Pack Formatting
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Formats the Memory Pack as a compact text block suitable for
    /// injection into the LLM system prompt.
    /// </summary>
    private static string FormatPackText(
        IReadOnlyList<MemoryFact> facts,
        IReadOnlyList<MemoryEvent> events,
        IReadOnlyList<MemoryChunk> chunks,
        IReadOnlyList<string> citations,
        string notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("[MEMORY CONTEXT]");
        sb.AppendLine("You recalled the following from your memory. Stay fully " +
                       "in character when referencing these — your personality, " +
                       "wit, and warmth should come through. Do not read them " +
                       "back like a list; weave them into your reply as though " +
                       "you naturally remember.");

        if (facts.Count > 0)
        {
            sb.AppendLine("FACTS:");
            foreach (var f in facts)
            {
                var when = f.UpdatedAt.ToString("yyyy-MM-dd");
                sb.AppendLine($"  - {f.Subject} {f.Predicate} {f.Object} (learned {when})");
            }
        }

        if (events.Count > 0)
        {
            sb.AppendLine("EVENTS:");
            foreach (var e in events)
            {
                var when    = e.WhenIso?.ToString("yyyy-MM-dd") ?? "unknown";
                var summary = string.IsNullOrEmpty(e.Summary) ? "" : $" — {e.Summary}";
                sb.AppendLine($"  - {e.Type}: {e.Title}{summary} ({when})");
            }
        }

        if (chunks.Count > 0)
        {
            sb.AppendLine("CONTEXT CHUNKS:");
            foreach (var c in chunks)
            {
                var excerpt = c.Text.Length > Thresholds.ChunkExcerptMaxChars
                    ? c.Text[..Thresholds.ChunkExcerptMaxChars] + "\u2026"
                    : c.Text;
                sb.AppendLine($"  - \"{excerpt}\"");
            }
        }

        if (citations.Count > 0)
        {
            sb.AppendLine("CITATIONS:");
            foreach (var cit in citations)
                sb.AppendLine($"  - {cit}");
        }

        if (!string.IsNullOrEmpty(notes))
            sb.AppendLine($"NOTE: {notes}");

        sb.AppendLine("[/MEMORY CONTEXT]");
        return sb.ToString();
    }

    private static MemoryPack EmptyPack(string notes) => new()
    {
        Notes    = notes,
        PackText = ""
    };
}
