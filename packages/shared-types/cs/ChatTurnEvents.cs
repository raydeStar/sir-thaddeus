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
    /// <summary>Emitted when a tool call begins executing inside a turn.</summary>
    public const string ToolStarted = "chat.tool.started";
    /// <summary>Emitted when a tool call finishes (success, error, or denied).</summary>
    public const string ToolCompleted = "chat.tool.completed";

    /// <summary>
    /// Emitted when a user message is appended server-side (not via the
    /// chat HTTP POST the UI already knows about). Reserved for future server-
    /// initiated user turns; currently unused after the removal of automation
    /// runs, but the event surface stays so the web store stays tolerant of
    /// either source.
    /// </summary>
    public const string UserMessageAppended = "chat.user.message";

    /// <summary>
    /// Emitted when the footman (gatekeeper) pre-classifies a turn and
    /// narrows the tool list handed to the primary model. The UI renders
    /// this as a compact chip above the assistant reply so the user can
    /// see that the gatekeeper actually ran and what it decided.
    /// </summary>
    public const string FootmanDecision = "chat.footman.decision";
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
/// <param name="Sources">Optional structured citations surfaced with the
/// assistant reply so the live chat UI can render rich cards without
/// waiting for a thread reload.</param>
public sealed record ChatTurnComplete(
    string ThreadId,
    string MessageId,
    string FinalText,
    DateTimeOffset CompletedAt,
    bool Cancelled,
    IReadOnlyList<ChatMessageSource>? Sources = null);

/// <summary>Payload for <see cref="ChatTurnEvents.ToolStarted"/>.</summary>
/// <param name="ActivityId">Unique id for this tool run (matches the completed event).</param>
/// <param name="ThreadId">Thread the turn belongs to.</param>
/// <param name="MessageId">Id of the assistant message that triggered the tool call.</param>
/// <param name="Tool">Registered tool name the model called.</param>
/// <param name="Group">Policy group (Web, Files, Screen, …) for UI grouping and icons.</param>
/// <param name="ArgsPreview">Short pretty-printed argument preview (safe for display).</param>
/// <param name="StartedAt">UTC timestamp the call began.</param>
public sealed record ChatToolStarted(
    string ActivityId,
    string ThreadId,
    string MessageId,
    string Tool,
    string Group,
    string ArgsPreview,
    DateTimeOffset StartedAt);

/// <summary>Payload for <see cref="ChatTurnEvents.ToolCompleted"/>.</summary>
/// <param name="ActivityId">Matches the started event.</param>
/// <param name="ThreadId">Thread the turn belongs to.</param>
/// <param name="MessageId">Id of the assistant message that triggered the call.</param>
/// <param name="Tool">Registered tool name.</param>
/// <param name="Ok">True when the tool returned normally; false on error or deny.</param>
/// <param name="DurationMs">Wall-clock time from start to finish.</param>
/// <param name="ResultSnippet">Short preview of the tool's output (truncated). Null on error/deny.</param>
/// <param name="Error">Human-readable error or denial reason. Null on success.</param>
/// <param name="CompletedAt">UTC timestamp the call returned.</param>
public sealed record ChatToolCompleted(
    string ActivityId,
    string ThreadId,
    string MessageId,
    string Tool,
    bool Ok,
    long DurationMs,
    string? ResultSnippet,
    string? Error,
    DateTimeOffset CompletedAt);

/// <summary>
/// Payload for <see cref="ChatTurnEvents.UserMessageAppended"/>. Lets the web
/// chat store insert a user message that was appended by the server into the
/// active thread's rendered messages.
/// </summary>
public sealed record ChatUserMessageAppended(
    string ThreadId,
    string MessageId,
    string Text,
    DateTimeOffset CreatedAt);

/// <summary>
/// Payload for <see cref="ChatTurnEvents.FootmanDecision"/>. Reports the
/// footman's routing verdict plus the before/after tool counts so the UI
/// can show "kept N of M tools" and the reason code (heuristic_greeting,
/// low_confidence, footman_timeout, …) without re-running the classifier.
/// </summary>
/// <param name="ThreadId">Thread the turn belongs to.</param>
/// <param name="MessageId">Id of the assistant message this decision gates.</param>
/// <param name="NextState">The footman's chosen agent state (e.g. "WebResearch", "Fallback").</param>
/// <param name="Confidence">Footman's confidence in the decision (0.0–1.0).</param>
/// <param name="Abstain">True when the footman explicitly abstained.</param>
/// <param name="ReasonCode">Machine-readable rationale (e.g. "heuristic_greeting", "low_confidence").</param>
/// <param name="ToolsKept">Number of tools the primary model received after filtering.</param>
/// <param name="ToolsTotal">Number of tools before footman filtering.</param>
/// <param name="ElapsedMs">Wall-clock time spent in the footman call.</param>
/// <param name="DecidedAt">UTC timestamp the decision was emitted.</param>
public sealed record ChatFootmanDecision(
    string ThreadId,
    string MessageId,
    string NextState,
    double Confidence,
    bool Abstain,
    string ReasonCode,
    int ToolsKept,
    int ToolsTotal,
    long ElapsedMs,
    DateTimeOffset DecidedAt);
