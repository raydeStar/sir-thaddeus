using SirThaddeus.Agent;
using SirThaddeus.LlmClient;

namespace SirThaddeus.DirectEval;

/// <summary>
/// Evaluation-only equal-tools control. It gives the model the production
/// prompt and an allowlisted tool surface, but deliberately provides no
/// routing, retrieval policy, retries, verification, repair, or synthesis.
/// </summary>
internal sealed class DirectToolLoop
{
    private readonly ILlmClient _llm;
    private readonly IMcpToolClient _mcp;
    private readonly int _maxRounds;

    public DirectToolLoop(ILlmClient llm, IMcpToolClient mcp, int maxRounds = 8)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _maxRounds = Math.Max(1, maxRounds);
    }

    public async Task<DirectToolLoopResult> ExecuteAsync(
        IReadOnlyList<ChatMessage> initialMessages,
        IReadOnlyList<ToolDefinition> availableTools,
        IReadOnlyCollection<string> allowedTools,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        var allowed = new HashSet<string>(allowedTools, StringComparer.OrdinalIgnoreCase);
        var tools = availableTools.Where(tool => allowed.Contains(tool.Function.Name)).ToList();
        var messages = initialMessages.ToList();
        var trace = new List<DirectToolCallTrace>();
        var promptTokens = 0;
        var completionTokens = 0;

        for (var round = 1; round <= _maxRounds; round++)
        {
            var response = await _llm.ChatAsync(
                messages,
                tools,
                maxOutputTokens,
                cancellationToken).ConfigureAwait(false);
            promptTokens += response.Usage?.PromptTokens ?? 0;
            completionTokens += response.Usage?.CompletionTokens ?? 0;

            if (response.IsComplete || response.ToolCalls is not { Count: > 0 })
            {
                return new DirectToolLoopResult(
                    response.Content ?? string.Empty,
                    round,
                    promptTokens,
                    completionTokens,
                    trace,
                    null);
            }

            messages.Add(ChatMessage.AssistantToolCalls(response.ToolCalls));
            foreach (var call in response.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string result;
                string? error = null;
                if (!allowed.Contains(call.Function.Name))
                {
                    error = "tool_not_allowlisted";
                    result = $"Error: Tool '{call.Function.Name}' is not available in this case.";
                }
                else
                {
                    try
                    {
                        result = await _mcp.CallToolAsync(
                            call.Function.Name,
                            call.Function.Arguments,
                            cancellationToken).ConfigureAwait(false);
                        if (IsFailedToolResult(result))
                            error = "tool_returned_failure";
                    }
                    catch (Exception ex)
                    {
                        error = $"{ex.GetType().Name}: {ex.Message}";
                        result = $"Error: {error}";
                    }
                }

                trace.Add(new DirectToolCallTrace(
                    round,
                    call.Id,
                    call.Function.Name,
                    call.Function.Arguments,
                    result,
                    error));
                messages.Add(ChatMessage.ToolResult(call.Id, result));
            }
        }

        return new DirectToolLoopResult(
            string.Empty,
            _maxRounds,
            promptTokens,
            completionTokens,
            trace,
            "max_tool_rounds_exceeded");
    }

    private static bool IsFailedToolResult(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return true;
        if (result.TrimStart().StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(result);
            return document.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind == System.Text.Json.JsonValueKind.False;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

internal sealed record DirectToolLoopResult(
    string Text,
    int CallCount,
    int PromptTokens,
    int CompletionTokens,
    IReadOnlyList<DirectToolCallTrace> ToolCalls,
    string? RuntimeError);

internal sealed record DirectToolCallTrace(
    int Round,
    string CallId,
    string Tool,
    string ArgumentsJson,
    string Result,
    string? Error);
