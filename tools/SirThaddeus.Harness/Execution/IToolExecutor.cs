using SirThaddeus.Agent;

namespace SirThaddeus.Harness.Execution;

public interface IToolExecutor : IAsyncDisposable
{
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default);

    Task<ToolExecutionEnvelope> ExecuteAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default);
}

public sealed record ToolExecutionEnvelope
{
    public string ToolName { get; init; } = "";
    public string ArgumentsJson { get; init; } = "{}";
    public string ResultText { get; init; } = "";
    public bool Success { get; init; } = true;
    public ToolExecutionError? Error { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset EndedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ToolExecutionError
{
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public bool Retriable { get; init; }
}
