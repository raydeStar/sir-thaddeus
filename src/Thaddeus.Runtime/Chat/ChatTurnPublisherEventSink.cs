using Microsoft.Extensions.Logging;
using SirThaddeus.Agent.Pipeline;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Runtime adapter that implements the agent-package <see cref="IChatEventSink"/>
/// port on top of the existing <see cref="ChatTurnPublisher"/> transport.
/// Pipeline steps talk to the <see cref="IChatEventSink"/> interface so they
/// stay decoupled from the runtime's WebSocket bus; this adapter forwards
/// every event to the publisher without adding any logic of its own.
///
/// <para>Publishing is best-effort by contract. Transport failures are
/// logged as warnings and swallowed so a broken socket can't derail a turn.
/// Downstream behavior should not depend on an event actually arriving at
/// a client.</para>
/// </summary>
public sealed class ChatTurnPublisherEventSink : IChatEventSink
{
    private readonly ChatTurnPublisher _publisher;
    private readonly ILogger<ChatTurnPublisherEventSink> _logger;

    public ChatTurnPublisherEventSink(
        ChatTurnPublisher publisher,
        ILogger<ChatTurnPublisherEventSink> logger)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task TurnStartedAsync(string threadId, string messageId, CancellationToken cancellationToken = default)
        => SafePublishAsync(
            nameof(TurnStartedAsync),
            () => _publisher.PublishStartAsync(threadId, messageId, cancellationToken));

    public Task TurnDeltaAsync(string threadId, string messageId, string text, CancellationToken cancellationToken = default)
        => SafePublishAsync(
            nameof(TurnDeltaAsync),
            () => _publisher.PublishDeltaAsync(threadId, messageId, text, cancellationToken));

    public Task TurnCompleteAsync(string threadId, string messageId, string finalText, bool cancelled, CancellationToken cancellationToken = default)
        => SafePublishAsync(
            nameof(TurnCompleteAsync),
            () => _publisher.PublishCompleteAsync(threadId, messageId, finalText, cancelled, cancellationToken));

    public Task ToolStartedAsync(
        string activityId,
        string threadId,
        string messageId,
        string tool,
        string group,
        string argsPreview,
        CancellationToken cancellationToken = default)
        => SafePublishAsync(
            nameof(ToolStartedAsync),
            () => _publisher.PublishToolStartedAsync(activityId, threadId, messageId, tool, group, argsPreview, cancellationToken));

    public Task ToolCompletedAsync(
        string activityId,
        string threadId,
        string messageId,
        string tool,
        bool ok,
        long durationMs,
        string? resultSnippet,
        string? error,
        CancellationToken cancellationToken = default)
        => SafePublishAsync(
            nameof(ToolCompletedAsync),
            () => _publisher.PublishToolCompletedAsync(activityId, threadId, messageId, tool, ok, durationMs, resultSnippet, error, cancellationToken));

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
        => SafePublishAsync(
            nameof(FootmanDecisionAsync),
            () => _publisher.PublishFootmanDecisionAsync(
                threadId, messageId, nextState, confidence, abstain, reasonCode,
                toolsKept, toolsTotal, elapsedMs, cancellationToken));

    private async Task SafePublishAsync(string eventName, Func<Task> publish)
    {
        try
        {
            await publish().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown in flight — silent drop is correct here.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "chat_event_sink.publish_failed event={Event}", eventName);
        }
    }
}
