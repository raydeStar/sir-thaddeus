namespace SirThaddeus.Contracts;

/// <summary>
/// A single prior message in the conversation.  Sent by the UI so the
/// runtime can seed the orchestrator's sliding-window history.
/// </summary>
public sealed record ChatHistoryMessage(
    string Role,
    string Content);

public sealed record ChatRequest(
    string Prompt,
    string? ConversationId = null,
    string? SessionId = null,
    IReadOnlyList<ChatHistoryMessage>? Messages = null);

public sealed record ChatStartResponse(
    string RunId,
    DateTimeOffset StartedAtUtc);

public sealed record CancelRunRequest(
    string? Reason = null);

public sealed record CancelRunResponse(
    string RunId,
    bool Accepted);
