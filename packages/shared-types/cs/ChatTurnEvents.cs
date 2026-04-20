namespace Thaddeus.SharedTypes;

/// <summary>
/// Base shape for a chat-turn event sent over the runtime WebSocket. Concrete payload
/// records are <see cref="ChatTurnStart"/>, <see cref="ChatTurnDelta"/>, and
/// <see cref="ChatTurnComplete"/>. Each rides as the <c>payload</c> of a
/// <see cref="RuntimeEvent{T}"/> with a matching <c>type</c> field
/// (<c>chat.turn.start</c>, <c>chat.turn.delta</c>, <c>chat.turn.complete</c>).
/// </summary>
public static class ChatTurnEvents
{
    /// <summary>Emitted when an assistant turn begins streaming.</summary>
    public const string Start = "chat.turn.start";
    /// <summary>Emitted for each text fragment of a streaming assistant turn.</summary>
    public const string Delta = "chat.turn.delta";
    /// <summary>Emitted when an assistant turn finishes (successfully or not).</summary>
    public const string Complete = "chat.turn.complete";
}

/// <summary>Payload for <see cref="ChatTurnEvents.Start"/>.</summary>
public sealed record ChatTurnStart(string ThreadId, string MessageId, DateTimeOffset StartedAt);

/// <summary>Payload for <see cref="ChatTurnEvents.Delta"/>.</summary>
public sealed record ChatTurnDelta(string ThreadId, string MessageId, string Text);

/// <summary>Payload for <see cref="ChatTurnEvents.Complete"/>.</summary>
/// <param name="ThreadId">The thread the turn belongs to.</param>
/// <param name="MessageId">The id of the assistant message whose stream completed.</param>
/// <param name="FinalText">The full assembled assistant text after streaming ends.</param>
/// <param name="CompletedAt">UTC timestamp the turn finished.</param>
/// <param name="Cancelled">True if the turn was stopped before natural completion.</param>
public sealed record ChatTurnComplete(
    string ThreadId,
    string MessageId,
    string FinalText,
    DateTimeOffset CompletedAt,
    bool Cancelled);
