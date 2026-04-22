using Thaddeus.Runtime.Events;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Helper that wraps <see cref="IEventBus"/> with chat-turn-specific publish methods.
/// Producers (the stub assistant in 3.4 and the real LLM client later) call into this
/// rather than constructing event envelopes by hand.
/// </summary>
public sealed class ChatTurnPublisher
{
    private readonly IEventBus _bus;

    public ChatTurnPublisher(IEventBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    }

    public Task PublishStartAsync(string threadId, string messageId, CancellationToken ct = default) =>
        _bus.PublishAsync(
            ChatTurnEvents.Start,
            new ChatTurnStart(threadId, messageId, DateTimeOffset.UtcNow),
            correlationId: messageId,
            ct);

    public Task PublishDeltaAsync(string threadId, string messageId, string text, CancellationToken ct = default) =>
        _bus.PublishAsync(
            ChatTurnEvents.Delta,
            new ChatTurnDelta(threadId, messageId, text),
            correlationId: messageId,
            ct);

    public Task PublishCompleteAsync(
        string threadId,
        string messageId,
        string finalText,
        bool cancelled,
        CancellationToken ct = default) =>
        _bus.PublishAsync(
            ChatTurnEvents.Complete,
            new ChatTurnComplete(threadId, messageId, finalText, DateTimeOffset.UtcNow, cancelled),
            correlationId: messageId,
            ct);

    public Task PublishToolStartedAsync(
        string activityId,
        string threadId,
        string messageId,
        string tool,
        string group,
        string argsPreview,
        CancellationToken ct = default) =>
        _bus.PublishAsync(
            ChatTurnEvents.ToolStarted,
            new ChatToolStarted(activityId, threadId, messageId, tool, group, argsPreview, DateTimeOffset.UtcNow),
            correlationId: messageId,
            ct);

    public Task PublishToolCompletedAsync(
        string activityId,
        string threadId,
        string messageId,
        string tool,
        bool ok,
        long durationMs,
        string? resultSnippet,
        string? error,
        CancellationToken ct = default) =>
        _bus.PublishAsync(
            ChatTurnEvents.ToolCompleted,
            new ChatToolCompleted(activityId, threadId, messageId, tool, ok, durationMs,
                resultSnippet, error, DateTimeOffset.UtcNow),
            correlationId: messageId,
            ct);
    public Task PublishUserMessageAppendedAsync(
        string threadId,
        string messageId,
        string text,
        DateTimeOffset createdAt,
        CancellationToken ct = default) =>
        _bus.PublishAsync(
            ChatTurnEvents.UserMessageAppended,
            new ChatUserMessageAppended(threadId, messageId, text, createdAt),
            correlationId: messageId,
            ct);

    public Task PublishFootmanDecisionAsync(
        string threadId,
        string messageId,
        string nextState,
        double confidence,
        bool abstain,
        string reasonCode,
        int toolsKept,
        int toolsTotal,
        long elapsedMs,
        CancellationToken ct = default) =>
        _bus.PublishAsync(
            ChatTurnEvents.FootmanDecision,
            new ChatFootmanDecision(
                threadId, messageId, nextState, confidence, abstain, reasonCode,
                toolsKept, toolsTotal, elapsedMs, DateTimeOffset.UtcNow),
            correlationId: messageId,
            ct);

    public Task PublishAutomationProposedAsync(
        string proposalId,
        string threadId,
        string messageId,
        string name,
        string? description,
        IReadOnlyList<string> steps,
        AutomationSchedule? schedule,
        CancellationToken ct = default) =>
        _bus.PublishAsync(
            ChatTurnEvents.AutomationProposed,
            new ChatAutomationProposed(
                proposalId, threadId, messageId, name, description, steps, schedule,
                DateTimeOffset.UtcNow),
            correlationId: messageId,
            ct);
}