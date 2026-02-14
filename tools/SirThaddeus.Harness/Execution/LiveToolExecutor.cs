using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.Config;

namespace SirThaddeus.Harness.Execution;

public sealed class LiveToolExecutor : IToolExecutor
{
    private readonly HarnessMcpProcessClient _client;
    private bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LiveToolExecutor(AppSettings settings)
    {
        _client = new HarnessMcpProcessClient(settings);
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var payload = await _client.ListToolsAsync(cancellationToken);
        return ParseToolList(payload);
    }

    public async Task<ToolExecutionEnvelope> ExecuteAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            var payload = await _client.CallToolAsync(toolName, argumentsJson, cancellationToken);
            var text = ExtractToolContent(payload);
            var success = !LooksLikeToolFailure(text, out var inferredCode);

            if (success)
            {
                return new ToolExecutionEnvelope
                {
                    ToolName = toolName,
                    ArgumentsJson = argumentsJson,
                    ResultText = ToolResultPayloads.BuildSuccess(text),
                    Success = true,
                    StartedAt = startedAt,
                    EndedAt = DateTimeOffset.UtcNow
                };
            }

            var structuredError = new ToolExecutionError
            {
                Code = inferredCode,
                Message = text,
                Retriable = inferredCode is "timeout" or "tool_unavailable"
            };

            return new ToolExecutionEnvelope
            {
                ToolName = toolName,
                ArgumentsJson = argumentsJson,
                ResultText = ToolResultPayloads.BuildErrorJson(structuredError.Code, structuredError.Message, structuredError.Retriable),
                Success = false,
                Error = structuredError,
                StartedAt = startedAt,
                EndedAt = DateTimeOffset.UtcNow
            };
        }
        catch (OperationCanceledException)
        {
            var error = new ToolExecutionError
            {
                Code = "timeout",
                Message = $"Tool '{toolName}' timed out or was cancelled.",
                Retriable = true
            };

            return new ToolExecutionEnvelope
            {
                ToolName = toolName,
                ArgumentsJson = argumentsJson,
                ResultText = ToolResultPayloads.BuildErrorJson(error.Code, error.Message, error.Retriable),
                Success = false,
                Error = error,
                StartedAt = startedAt,
                EndedAt = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            var error = new ToolExecutionError
            {
                Code = InferErrorCode(ex.Message),
                Message = ex.Message,
                Retriable = false
            };

            return new ToolExecutionEnvelope
            {
                ToolName = toolName,
                ArgumentsJson = argumentsJson,
                ResultText = ToolResultPayloads.BuildErrorJson(error.Code, error.Message, error.Retriable),
                Success = false,
                Error = error,
                StartedAt = startedAt,
                EndedAt = DateTimeOffset.UtcNow
            };
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _client.InitializeAsync(cancellationToken);
        _initialized = true;
    }

    private static IReadOnlyList<McpToolInfo> ParseToolList(JsonElement payload)
    {
        var tools = new List<McpToolInfo>();
        if (!payload.TryGetProperty("tools", out var toolsArray) || toolsArray.ValueKind != JsonValueKind.Array)
            return tools;

        foreach (var item in toolsArray.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            var description = item.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";
            var inputSchema = item.TryGetProperty("inputSchema", out var schemaProp)
                ? schemaProp.GetRawText()
                : "{}";

            if (!string.IsNullOrWhiteSpace(name))
            {
                tools.Add(new McpToolInfo
                {
                    Name = name,
                    Description = description,
                    InputSchema = inputSchema
                });
            }
        }

        return tools;
    }

    private static string ExtractToolContent(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return payload.GetRawText();

        var textParts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (item.TryGetProperty("type", out var typeProp) &&
                string.Equals(typeProp.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("text", out var textProp))
            {
                var value = textProp.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    textParts.Add(value);
            }
        }

        return textParts.Count == 0 ? payload.GetRawText() : string.Join(Environment.NewLine, textParts);
    }

    private static bool LooksLikeToolFailure(string text, out string code)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            code = "empty_result";
            return false;
        }

        var normalized = text.Trim();
        if (normalized.StartsWith("error", StringComparison.OrdinalIgnoreCase))
        {
            code = InferErrorCode(normalized);
            return true;
        }

        code = "";
        return false;
    }

    private static string InferErrorCode(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "tool_error";

        var lower = message.ToLowerInvariant();
        if (lower.Contains("permission") || lower.Contains("denied"))
            return "permission_denied";
        if (lower.Contains("timeout") || lower.Contains("timed out") || lower.Contains("cancel"))
            return "timeout";
        if (lower.Contains("not found") || lower.Contains("unknown tool") || lower.Contains("unavailable"))
            return "tool_unavailable";

        return "tool_error";
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
