using SirThaddeus.Agent;
using SirThaddeus.Harness.Tracing;

namespace SirThaddeus.Harness.Execution;

public sealed class ExecutorBackedMcpClient : IMcpToolClient
{
    private readonly IToolExecutor _toolExecutor;
    private readonly TraceRecorder _traceRecorder;
    private readonly HashSet<string> _allowedTools;

    public ExecutorBackedMcpClient(
        IToolExecutor toolExecutor,
        TraceRecorder traceRecorder,
        IEnumerable<string> allowedTools)
    {
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _traceRecorder = traceRecorder ?? throw new ArgumentNullException(nameof(traceRecorder));
        _allowedTools = new HashSet<string>(allowedTools ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
        => _toolExecutor.ListToolsAsync(cancellationToken);

    public async Task<string> CallToolAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        var callId = _traceRecorder.NextCallId("tool");
        var startedAt = DateTimeOffset.UtcNow;
        _traceRecorder.RecordToolCall(callId, toolName, argumentsJson);

        if (_allowedTools.Count > 0 && !_allowedTools.Contains(toolName))
        {
            var denied = ToolResultPayloads.BuildErrorJson(
                code: "tool_not_allowed",
                message: $"Tool '{toolName}' is not in the allowed_tools whitelist for this test.",
                retriable: false);

            _traceRecorder.RecordToolResult(
                callId,
                toolName,
                argumentsJson,
                denied,
                new TraceError
                {
                    Code = "tool_not_allowed",
                    Message = $"Tool '{toolName}' blocked by harness allowlist.",
                    Retriable = false
                },
                startedAt,
                DateTimeOffset.UtcNow);

            return denied;
        }

        var envelope = await _toolExecutor.ExecuteAsync(toolName, argumentsJson, cancellationToken);
        var traceError = envelope.Error is null
            ? null
            : new TraceError
            {
                Code = envelope.Error.Code,
                Message = envelope.Error.Message,
                Retriable = envelope.Error.Retriable
            };

        _traceRecorder.RecordToolResult(
            callId,
            toolName,
            argumentsJson,
            envelope.ResultText,
            traceError,
            envelope.StartedAt,
            envelope.EndedAt);

        return envelope.ResultText;
    }
}
