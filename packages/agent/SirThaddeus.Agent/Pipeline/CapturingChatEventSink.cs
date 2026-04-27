using System.Collections.Concurrent;

namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// <see cref="IChatEventSink"/> that records every event to in-memory
/// queues. Useful for integration tests (assert that a turn emitted the
/// right sequence of tool/footman events) and for the harness (capture
/// the event stream for post-run assertions / scoring).
///
/// <para>Thread-safe: multiple pipeline steps (ToolLoopStep running tools
/// sequentially, FootmanRouterStep upstream) can write concurrently to a
/// single sink without races. Consumers read snapshots via the
/// <c>Snapshot*</c> methods which allocate new arrays per call.</para>
///
/// <para>This sink never throws. It's a test-facing transport by design —
/// swallowing events silently under load would defeat the purpose, but
/// since we only append to concurrent collections, the failure surface
/// is near-zero.</para>
/// </summary>
public sealed class CapturingChatEventSink : IChatEventSink
{
    private readonly ConcurrentQueue<RecordedEvent> _events = new();

    public IReadOnlyList<RecordedEvent> Snapshot() => _events.ToArray();

    public IReadOnlyList<RecordedEvent> SnapshotOfKind(string kind)
        => _events.Where(e => string.Equals(e.Kind, kind, StringComparison.Ordinal)).ToArray();

    public void Clear()
    {
        while (_events.TryDequeue(out _)) { /* drain */ }
    }

    public Task TurnStartedAsync(string threadId, string messageId, CancellationToken cancellationToken = default)
    {
        _events.Enqueue(new RecordedEvent(
            Kind: "turn.start",
            ThreadId: threadId,
            MessageId: messageId));
        return Task.CompletedTask;
    }

    public Task TurnDeltaAsync(string threadId, string messageId, string text, CancellationToken cancellationToken = default)
    {
        _events.Enqueue(new RecordedEvent(
            Kind: "turn.delta",
            ThreadId: threadId,
            MessageId: messageId,
            Text: text));
        return Task.CompletedTask;
    }

    public Task TurnCompleteAsync(string threadId, string messageId, string finalText, bool cancelled, CancellationToken cancellationToken = default)
    {
        _events.Enqueue(new RecordedEvent(
            Kind: "turn.complete",
            ThreadId: threadId,
            MessageId: messageId,
            Text: finalText,
            Cancelled: cancelled));
        return Task.CompletedTask;
    }

    public Task ToolStartedAsync(string activityId, string threadId, string messageId, string tool, string group, string argsPreview, CancellationToken cancellationToken = default)
    {
        _events.Enqueue(new RecordedEvent(
            Kind: "tool.started",
            ThreadId: threadId,
            MessageId: messageId,
            ActivityId: activityId,
            Tool: tool,
            Group: group,
            ArgsPreview: argsPreview));
        return Task.CompletedTask;
    }

    public Task ToolCompletedAsync(string activityId, string threadId, string messageId, string tool, bool ok, long durationMs, string? resultSnippet, string? error, CancellationToken cancellationToken = default)
    {
        _events.Enqueue(new RecordedEvent(
            Kind: "tool.completed",
            ThreadId: threadId,
            MessageId: messageId,
            ActivityId: activityId,
            Tool: tool,
            Ok: ok,
            DurationMs: durationMs,
            ResultSnippet: resultSnippet,
            Error: error));
        return Task.CompletedTask;
    }

    public Task FootmanDecisionAsync(
        string threadId,
        string messageId,
        string nextState,
        double confidence,
        bool abstain,
        string reasonCode,
        int toolsKept,
        int toolsTotal,
        long elapsedMs,
        CancellationToken cancellationToken = default)
    {
        _events.Enqueue(new RecordedEvent(
            Kind: "footman.decision",
            ThreadId: threadId,
            MessageId: messageId,
            NextState: nextState,
            Confidence: confidence,
            Abstain: abstain,
            ReasonCode: reasonCode,
            ToolsKept: toolsKept,
            ToolsTotal: toolsTotal,
            DurationMs: elapsedMs));
        return Task.CompletedTask;
    }
}

/// <summary>
/// A single event captured by <see cref="CapturingChatEventSink"/>. Fields
/// are all nullable because most events only populate a subset — a
/// <c>turn.start</c> event has no tool info, a <c>tool.completed</c> has
/// no confidence, etc. Consumers match on <see cref="Kind"/> first and
/// read the relevant fields.
/// </summary>
public sealed record RecordedEvent(
    string Kind,
    string? ThreadId = null,
    string? MessageId = null,
    string? Text = null,
    bool? Cancelled = null,
    string? ActivityId = null,
    string? Tool = null,
    string? Group = null,
    string? ArgsPreview = null,
    bool? Ok = null,
    long? DurationMs = null,
    string? ResultSnippet = null,
    string? Error = null,
    string? NextState = null,
    double? Confidence = null,
    bool? Abstain = null,
    string? ReasonCode = null,
    int? ToolsKept = null,
    int? ToolsTotal = null);
