using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Harness.Execution;

public sealed class ReplayToolExecutor : IToolExecutor
{
    private readonly HarnessFixture _fixture;
    private int _toolIndex;

    public ReplayToolExecutor(HarnessFixture fixture)
    {
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
    }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var list = _fixture.AvailableTools
            .Select(name => new McpToolInfo
            {
                Name = name,
                Description = "Replay fixture tool",
                InputSchema = "{}"
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<McpToolInfo>>(list);
    }

    public Task<ToolExecutionEnvelope> ExecuteAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;

        if (_toolIndex >= _fixture.ToolTurns.Count)
        {
            return Task.FromResult(BuildFailureEnvelope(
                toolName,
                argumentsJson,
                "replay_exhausted",
                "Replay fixture has no remaining tool turns.",
                retriable: false,
                startedAt));
        }

        var turn = _fixture.ToolTurns[_toolIndex++];
        if (!string.Equals(turn.ToolName, toolName, StringComparison.OrdinalIgnoreCase) ||
            !JsonEquivalent(turn.ArgumentsJson, argumentsJson))
        {
            return Task.FromResult(BuildFailureEnvelope(
                toolName,
                argumentsJson,
                "replay_mismatch",
                $"Replay mismatch. Expected '{turn.ToolName}' with fixture args, got '{toolName}'.",
                retriable: false,
                startedAt));
        }

        var error = turn.Success
            ? null
            : new ToolExecutionError
            {
                Code = InferErrorCode(turn.ResultText),
                Message = turn.ResultText,
                Retriable = false
            };

        var payload = turn.Success
            ? turn.ResultText
            : ToolResultPayloads.BuildErrorJson(error!.Code, error.Message, error.Retriable);

        return Task.FromResult(new ToolExecutionEnvelope
        {
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            ResultText = payload,
            Success = turn.Success,
            Error = error,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow
        });
    }

    private static ToolExecutionEnvelope BuildFailureEnvelope(
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

    private static string InferErrorCode(string value)
    {
        var lower = value.ToLowerInvariant();
        if (lower.Contains("permission") || lower.Contains("denied"))
            return "permission_denied";
        if (lower.Contains("timeout"))
            return "timeout";
        return "tool_error";
    }

    private static bool JsonEquivalent(string left, string right)
    {
        try
        {
            using var leftDoc = JsonDocument.Parse(left);
            using var rightDoc = JsonDocument.Parse(right);
            var normalizedLeft = JsonSerializer.Serialize(leftDoc.RootElement);
            var normalizedRight = JsonSerializer.Serialize(rightDoc.RootElement);
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
        }
        catch
        {
            return string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
