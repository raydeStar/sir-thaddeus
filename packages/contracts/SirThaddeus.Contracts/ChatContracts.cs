namespace SirThaddeus.Contracts;

public sealed record ChatRequest(
    string Prompt,
    string? ConversationId = null,
    string? SessionId = null);

public sealed record ChatStartResponse(
    string RunId,
    DateTimeOffset StartedAtUtc);

public sealed record CancelRunRequest(
    string? Reason = null);

public sealed record CancelRunResponse(
    string RunId,
    bool Accepted);
