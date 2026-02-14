using SirThaddeus.Agent;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Harness.Execution;

public sealed class StubToolExecutor : IToolExecutor
{
    private readonly HarnessStubConfig _config;
    private readonly IToolExecutor? _fallbackExecutor;
    private readonly IReadOnlyList<McpToolInfo> _toolInfos;

    public StubToolExecutor(HarnessStubConfig config, IEnumerable<string> allowedTools, IToolExecutor? fallbackExecutor = null)
    {
        _config = config ?? new HarnessStubConfig();
        _fallbackExecutor = fallbackExecutor;
        _toolInfos = (allowedTools ?? [])
            .Select(name => new McpToolInfo
            {
                Name = name,
                Description = "Stubbed tool executor",
                InputSchema = "{}"
            })
            .ToList();
    }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_toolInfos);

    public async Task<ToolExecutionEnvelope> ExecuteAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var mode = ResolveFailureMode(toolName);

        switch (mode)
        {
            case "empty_result":
                return new ToolExecutionEnvelope
                {
                    ToolName = toolName,
                    ArgumentsJson = argumentsJson,
                    ResultText = "{}",
                    Success = true,
                    StartedAt = startedAt,
                    EndedAt = DateTimeOffset.UtcNow
                };
            case "timeout":
                return BuildFailure(toolName, argumentsJson, "timeout", $"Stub timeout for '{toolName}'.", true, startedAt);
            case "permission_denied":
                return BuildFailure(toolName, argumentsJson, "permission_denied", $"Stub permission denied for '{toolName}'.", false, startedAt);
            case "tool_unavailable":
                return BuildFailure(toolName, argumentsJson, "tool_unavailable", $"Stub unavailable tool '{toolName}'.", false, startedAt);
            case "delegate":
                if (_fallbackExecutor is not null)
                    return await _fallbackExecutor.ExecuteAsync(toolName, argumentsJson, cancellationToken);
                return BuildFailure(toolName, argumentsJson, "tool_error", "Stub mode requested delegate but no fallback executor is configured.", false, startedAt);
            default:
                return BuildFailure(toolName, argumentsJson, "tool_error", $"Unsupported stub failure mode '{mode}'.", false, startedAt);
        }
    }

    private string ResolveFailureMode(string toolName)
    {
        if (_config.PerToolFailures.TryGetValue(toolName, out var perToolMode) &&
            !string.IsNullOrWhiteSpace(perToolMode))
        {
            return perToolMode.Trim().ToLowerInvariant();
        }

        return string.IsNullOrWhiteSpace(_config.DefaultFailure)
            ? "timeout"
            : _config.DefaultFailure.Trim().ToLowerInvariant();
    }

    private static ToolExecutionEnvelope BuildFailure(
        string toolName,
        string argumentsJson,
        string code,
        string message,
        bool retriable,
        DateTimeOffset startedAt)
    {
        var error = new ToolExecutionError
        {
            Code = code,
            Message = message,
            Retriable = retriable
        };

        return new ToolExecutionEnvelope
        {
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            ResultText = ToolResultPayloads.BuildErrorJson(code, message, retriable),
            Success = false,
            Error = error,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_fallbackExecutor is not null)
            await _fallbackExecutor.DisposeAsync();
    }
}
