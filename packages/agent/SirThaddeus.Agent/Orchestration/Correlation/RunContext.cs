using System.Diagnostics;

namespace SirThaddeus.Agent.Orchestration.Correlation;

/// <summary>
/// Immutable context bag for a single orchestrator turn.
/// Created at the start of <c>ProcessAsync</c> and threaded through
/// every layer: Route → Validate → Execute → Verify → Repair → Respond.
///
/// Design rules:
///   • One <see cref="RunContext"/> per user message — never reused.
///   • Layers read from it; only the orchestrator mutates budgets via
///     <see cref="RecordToolCall"/> / <see cref="RecordLlmRoundTrip"/>.
///   • No LLM state, no history — just correlation + budgets + timing.
/// </summary>
public sealed class RunContext
{
    // ── Identity ─────────────────────────────────────────────────────

    /// <summary>Unique identifier for this turn.</summary>
    public CorrelationId CorrelationId { get; }

    /// <summary>The classified intent for this turn (set after routing).</summary>
    public string Intent { get; set; } = string.Empty;

    // ── Timing ───────────────────────────────────────────────────────

    /// <summary>Wall-clock start time of the turn.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Stopwatch for precise elapsed measurement.</summary>
    private readonly Stopwatch _stopwatch;

    /// <summary>Elapsed time since the turn started.</summary>
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    // ── Budget Tracking ──────────────────────────────────────────────

    /// <summary>Maximum tool calls allowed in this turn.</summary>
    public int MaxToolCalls { get; }

    /// <summary>Maximum LLM round-trips allowed in this turn.</summary>
    public int MaxLlmRoundTrips { get; }

    /// <summary>Maximum repair attempts allowed in this turn.</summary>
    public int MaxRepairs { get; }

    /// <summary>Tool calls executed so far.</summary>
    public int ToolCallCount { get; private set; }

    /// <summary>LLM round-trips so far.</summary>
    public int LlmRoundTripCount { get; private set; }

    /// <summary>Repair attempts so far.</summary>
    public int RepairCount { get; private set; }

    /// <summary>Whether the tool call budget has been exhausted.</summary>
    public bool ToolBudgetExhausted => ToolCallCount >= MaxToolCalls;

    /// <summary>Whether the LLM round-trip budget has been exhausted.</summary>
    public bool LlmBudgetExhausted => LlmRoundTripCount >= MaxLlmRoundTrips;

    /// <summary>Whether the repair budget has been exhausted.</summary>
    public bool RepairBudgetExhausted => RepairCount >= MaxRepairs;

    // ── Default Budgets ──────────────────────────────────────────────

    public const int DefaultMaxToolCalls = 20;
    public const int DefaultMaxLlmRoundTrips = 10;
    public const int DefaultMaxRepairs = 2;

    // ── Lifecycle ────────────────────────────────────────────────────

    private RunContext(
        CorrelationId correlationId,
        int maxToolCalls = DefaultMaxToolCalls,
        int maxLlmRoundTrips = DefaultMaxLlmRoundTrips,
        int maxRepairs = DefaultMaxRepairs)
    {
        CorrelationId = correlationId;
        StartedAt = DateTimeOffset.UtcNow;
        _stopwatch = Stopwatch.StartNew();
        MaxToolCalls = maxToolCalls;
        MaxLlmRoundTrips = maxLlmRoundTrips;
        MaxRepairs = maxRepairs;
    }

    /// <summary>Creates a new run context with a fresh correlation ID.</summary>
    public static RunContext New(
        int maxToolCalls = DefaultMaxToolCalls,
        int maxLlmRoundTrips = DefaultMaxLlmRoundTrips,
        int maxRepairs = DefaultMaxRepairs) =>
        new(CorrelationId.New(), maxToolCalls, maxLlmRoundTrips, maxRepairs);

    /// <summary>Creates a run context with a specific correlation ID (for testing).</summary>
    public static RunContext WithId(
        CorrelationId id,
        int maxToolCalls = DefaultMaxToolCalls,
        int maxLlmRoundTrips = DefaultMaxLlmRoundTrips,
        int maxRepairs = DefaultMaxRepairs) =>
        new(id, maxToolCalls, maxLlmRoundTrips, maxRepairs);

    // ── Budget Mutation ──────────────────────────────────────────────

    /// <summary>Records a tool call. Returns false if budget was already exhausted.</summary>
    public bool RecordToolCall()
    {
        if (ToolBudgetExhausted) return false;
        ToolCallCount++;
        return true;
    }

    /// <summary>Records an LLM round-trip. Returns false if budget was already exhausted.</summary>
    public bool RecordLlmRoundTrip()
    {
        if (LlmBudgetExhausted) return false;
        LlmRoundTripCount++;
        return true;
    }

    /// <summary>Records a repair attempt. Returns false if budget was already exhausted.</summary>
    public bool RecordRepair()
    {
        if (RepairBudgetExhausted) return false;
        RepairCount++;
        return true;
    }

    /// <summary>Stops the internal stopwatch. Call once at the end of the turn.</summary>
    public void Stop() => _stopwatch.Stop();

    public override string ToString() =>
        $"[{CorrelationId}] intent={Intent} tools={ToolCallCount}/{MaxToolCalls} " +
        $"llm={LlmRoundTripCount}/{MaxLlmRoundTrips} repairs={RepairCount}/{MaxRepairs} " +
        $"elapsed={Elapsed.TotalMilliseconds:F0}ms";
}
