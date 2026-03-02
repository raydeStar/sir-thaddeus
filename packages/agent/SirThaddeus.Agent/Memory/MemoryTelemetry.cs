namespace SirThaddeus.Agent.Memory;

/// <summary>
/// Lightweight telemetry counters for memory subsystem observability.
/// Thread-safe via Interlocked operations. Consumed by audit logs,
/// diagnostic UI, and the activity drawer.
/// </summary>
public sealed class MemoryTelemetry
{
    // ── Extraction metrics ────────────────────────────────────────────
    private long _extractionAttempts;
    private long _extractionSuccesses;
    private long _extractionFailures;
    private long _factsExtracted;
    private long _eventsExtracted;
    private long _nuggetsExtracted;

    // ── Retrieval metrics ────────────────────────────────────────────
    private long _retrievalAttempts;
    private long _retrievalSuccesses;
    private long _retrievalTimeouts;
    private long _retrievalSuppressed;

    // ── Consolidation metrics ────────────────────────────────────────
    private long _consolidationRuns;
    private long _nuggetsConsolidated;
    private long _nuggetsRejectedNoCitation;

    // ── Store decision metrics ──────────────────────────────────────
    private long _storeDecisionsTotal;
    private long _storeDecisionsDuplicateSkipped;

    // ── Extraction ──────────────────────────────────────────────────

    public void RecordExtractionAttempt() => Interlocked.Increment(ref _extractionAttempts);
    public void RecordExtractionSuccess(int facts, int events, int nuggets)
    {
        Interlocked.Increment(ref _extractionSuccesses);
        Interlocked.Add(ref _factsExtracted, facts);
        Interlocked.Add(ref _eventsExtracted, events);
        Interlocked.Add(ref _nuggetsExtracted, nuggets);
    }
    public void RecordExtractionFailure() => Interlocked.Increment(ref _extractionFailures);

    // ── Retrieval ──────────────────────────────────────────────────

    public void RecordRetrievalAttempt() => Interlocked.Increment(ref _retrievalAttempts);
    public void RecordRetrievalSuccess() => Interlocked.Increment(ref _retrievalSuccesses);
    public void RecordRetrievalTimeout() => Interlocked.Increment(ref _retrievalTimeouts);
    public void RecordRetrievalSuppressed() => Interlocked.Increment(ref _retrievalSuppressed);

    // ── Consolidation ──────────────────────────────────────────────

    public void RecordConsolidationRun(int nuggetsProduced, int rejected)
    {
        Interlocked.Increment(ref _consolidationRuns);
        Interlocked.Add(ref _nuggetsConsolidated, nuggetsProduced);
        Interlocked.Add(ref _nuggetsRejectedNoCitation, rejected);
    }

    // ── Store decisions ────────────────────────────────────────────

    public void RecordStoreDecision(bool wasDuplicate)
    {
        Interlocked.Increment(ref _storeDecisionsTotal);
        if (wasDuplicate) Interlocked.Increment(ref _storeDecisionsDuplicateSkipped);
    }

    // ── Snapshot ────────────────────────────────────────────────────

    /// <summary>
    /// Returns a point-in-time snapshot of all counters.
    /// </summary>
    public MemoryTelemetrySnapshot GetSnapshot() => new()
    {
        ExtractionAttempts     = Interlocked.Read(ref _extractionAttempts),
        ExtractionSuccesses    = Interlocked.Read(ref _extractionSuccesses),
        ExtractionFailures     = Interlocked.Read(ref _extractionFailures),
        FactsExtracted         = Interlocked.Read(ref _factsExtracted),
        EventsExtracted        = Interlocked.Read(ref _eventsExtracted),
        NuggetsExtracted       = Interlocked.Read(ref _nuggetsExtracted),
        RetrievalAttempts      = Interlocked.Read(ref _retrievalAttempts),
        RetrievalSuccesses     = Interlocked.Read(ref _retrievalSuccesses),
        RetrievalTimeouts      = Interlocked.Read(ref _retrievalTimeouts),
        RetrievalSuppressed    = Interlocked.Read(ref _retrievalSuppressed),
        ConsolidationRuns      = Interlocked.Read(ref _consolidationRuns),
        NuggetsConsolidated    = Interlocked.Read(ref _nuggetsConsolidated),
        NuggetsRejectedNoCitation = Interlocked.Read(ref _nuggetsRejectedNoCitation),
        StoreDecisionsTotal    = Interlocked.Read(ref _storeDecisionsTotal),
        StoreDecisionsDuplicateSkipped = Interlocked.Read(ref _storeDecisionsDuplicateSkipped)
    };
}

/// <summary>
/// Immutable point-in-time snapshot of memory telemetry counters.
/// </summary>
public sealed record MemoryTelemetrySnapshot
{
    public long ExtractionAttempts     { get; init; }
    public long ExtractionSuccesses    { get; init; }
    public long ExtractionFailures     { get; init; }
    public long FactsExtracted         { get; init; }
    public long EventsExtracted        { get; init; }
    public long NuggetsExtracted       { get; init; }

    public long RetrievalAttempts      { get; init; }
    public long RetrievalSuccesses     { get; init; }
    public long RetrievalTimeouts      { get; init; }
    public long RetrievalSuppressed    { get; init; }

    public long ConsolidationRuns      { get; init; }
    public long NuggetsConsolidated    { get; init; }
    public long NuggetsRejectedNoCitation { get; init; }

    public long StoreDecisionsTotal    { get; init; }
    public long StoreDecisionsDuplicateSkipped { get; init; }

    /// <summary>
    /// Returns a human-readable summary for diagnostic/activity log display.
    /// </summary>
    public string ToSummaryString() =>
        $"Memory Stats — Extractions: {ExtractionSuccesses}/{ExtractionAttempts} ok, " +
        $"Facts: {FactsExtracted}, Events: {EventsExtracted}, Nuggets: {NuggetsExtracted} | " +
        $"Retrieval: {RetrievalSuccesses}/{RetrievalAttempts} ok, " +
        $"{RetrievalTimeouts} timeout, {RetrievalSuppressed} suppressed | " +
        $"Consolidation: {ConsolidationRuns} runs, {NuggetsConsolidated} produced, " +
        $"{NuggetsRejectedNoCitation} rejected (no citation)";
}
