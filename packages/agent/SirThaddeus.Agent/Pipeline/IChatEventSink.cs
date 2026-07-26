namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Abstraction over the runtime's event transport for chat turns. Pipeline
/// steps and the runtime facade call into this port to publish
/// user-visible turn events — start/delta/complete, tool activity, and
/// footman (gatekeeper) decisions — without depending on any specific
/// transport (WebSocket, stdout, audit log, test capture).
///
/// <para>Adapters live per-runtime:</para>
/// <list type="bullet">
///   <item><b>UI runtime</b> — forwards to <c>ChatTurnPublisher</c>, which
///         publishes over the event bus to the desktop WebSocket clients.</item>
///   <item><b>CLI runtime</b> — writes human-readable lines to stdout so
///         the "thinking cadence" is visible in terminal runs.</item>
///   <item><b>Harness</b> — records events for post-run assertion. No live
///         clients to notify.</item>
/// </list>
///
/// <para>Implementations must be safe to call from multiple threads in
/// arbitrary order. Individual methods must not throw — the sink is a
/// best-effort transport; a failed publish must not derail a turn. Wrap
/// transport-level exceptions and log them internally instead.</para>
///
/// <para>All methods take a <see cref="CancellationToken"/> so adapters
/// that block on I/O (HTTP, disk) honour shutdown. A cancelled token on a
/// best-effort event may be swallowed silently.</para>
/// </summary>
public interface IChatEventSink
{
    /// <summary>The assistant turn has begun. Called once per turn, before
    /// any other event for the same <paramref name="messageId"/>.</summary>
    Task TurnStartedAsync(string threadId, string messageId, CancellationToken cancellationToken = default);

    /// <summary>An incremental text fragment of the assistant reply is
    /// available. May be called many times per turn; fragments are
    /// concatenated in order by the receiver. <paramref name="text"/>
    /// is a raw fragment, not markdown-safe.</summary>
    Task TurnDeltaAsync(string threadId, string messageId, string text, CancellationToken cancellationToken = default);

    /// <summary>The assistant turn has finished — either with a full reply
    /// (<paramref name="cancelled"/> false) or stopped mid-stream
    /// (<paramref name="cancelled"/> true). <paramref name="finalText"/>
    /// is the assembled reply as the runtime will persist it; receivers
    /// may ignore any earlier deltas and redraw from this value.</summary>
    Task TurnCompleteAsync(
        string threadId,
        string messageId,
        string finalText,
        bool cancelled,
        CancellationToken cancellationToken = default);

    /// <summary>A tool call has been issued to the MCP layer. The
    /// <paramref name="activityId"/> is stable across the paired
    /// <see cref="ToolCompletedAsync"/>; UIs use it to correlate the
    /// two events into a single chip.</summary>
    Task ToolStartedAsync(
        string activityId,
        string threadId,
        string messageId,
        string tool,
        string group,
        string argsPreview,
        CancellationToken cancellationToken = default);

    /// <summary>A tool call has finished. <paramref name="ok"/> is false
    /// when the call threw, was denied by a permission gate, or returned
    /// a structured error payload. <paramref name="durationMs"/> is
    /// wall-clock time between the paired
    /// <see cref="ToolStartedAsync"/> and this call.</summary>
    Task ToolCompletedAsync(
        string activityId,
        string threadId,
        string messageId,
        string tool,
        bool ok,
        long durationMs,
        string? resultSnippet,
        string? error,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A structured, user-legible effect preview emitted before permission
    /// evaluation and execution. The default keeps older headless adapters
    /// source-compatible while the desktop runtime persists the event.
    /// </summary>
    Task EffectProposedAsync(
        string activityId,
        string threadId,
        string messageId,
        string tool,
        ToolEffectDescriptor effect,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// The truthful effect outcome emitted after the tool returns. A successful
    /// call is only independently verified when the outcome says so.
    /// </summary>
    Task EffectCompletedAsync(
        string activityId,
        string threadId,
        string messageId,
        string tool,
        ToolEffectDescriptor effect,
        ToolEffectOutcome outcome,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>The footman (gatekeeper) has classified this turn. Carries
    /// the resulting agent state, confidence, reason code, and the
    /// before/after tool counts so the UI can show "kept N of M".</summary>
    Task FootmanDecisionAsync(
        string threadId,
        string messageId,
        string nextState,
        double confidence,
        bool abstain,
        string reasonCode,
        int toolsKept,
        int toolsTotal,
        long elapsedMs,
        CancellationToken cancellationToken = default);
}
