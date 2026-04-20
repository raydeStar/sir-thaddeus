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
}
