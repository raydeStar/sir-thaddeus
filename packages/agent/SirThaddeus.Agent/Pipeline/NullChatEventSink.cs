namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// No-op <see cref="IChatEventSink"/> that swallows every event. Useful for
/// tests and for minimal runtime setups that don't need live event
/// publication (e.g. harness runs that capture audit logs instead).
/// </summary>
public sealed class NullChatEventSink : IChatEventSink
{
    /// <summary>Shared singleton — the sink is stateless.</summary>
    public static readonly NullChatEventSink Instance = new();

    public Task TurnStartedAsync(string threadId, string messageId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task TurnDeltaAsync(string threadId, string messageId, string text, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task TurnCompleteAsync(string threadId, string messageId, string finalText, bool cancelled, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ToolStartedAsync(string activityId, string threadId, string messageId, string tool, string group, string argsPreview, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ToolCompletedAsync(string activityId, string threadId, string messageId, string tool, bool ok, long durationMs, string? resultSnippet, string? error, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task FootmanDecisionAsync(string threadId, string messageId, string nextState, double confidence, bool abstain, string reasonCode, int toolsKept, int toolsTotal, long elapsedMs, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
