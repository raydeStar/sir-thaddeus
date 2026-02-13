using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.ToolLoop;

/// <summary>
/// Default policy-respecting tool loop implementation.
/// </summary>
public sealed class ToolLoopExecutor : IToolLoopExecutor
{
    private readonly ILlmClient _llm;
    private readonly IMcpToolClient _mcp;

    public ToolLoopExecutor(ILlmClient llm, IMcpToolClient mcp)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
    }

    public async Task<AgentResponse> ExecuteAsync(
        ToolLoopExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.History);
        ArgumentNullException.ThrowIfNull(request.Tools);
        ArgumentNullException.ThrowIfNull(request.ToolCallsMade);
        ArgumentNullException.ThrowIfNull(request.SanitizeAssistantText);

        var log = request.LogEvent ?? ((_, _) => { });
        var tools = request.Tools;
        var roundTrips = request.InitialRoundTrips;

        // Tool availability is fixed by policy filtering upstream.
        // This executor must never add tools.
        var allowedToolNames = new HashSet<string>(
            tools.Select(t => t.Function.Name),
            StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            roundTrips++;

            log("AGENT_LLM_CALL", $"Round trip #{roundTrips}");
            LlmResponse response;
            try
            {
                response = await _llm.ChatAsync(request.History, tools, cancellationToken);
            }
            catch (HttpRequestException ex) when (IsLmStudioRegexFailure(ex) && tools is { Count: > 0 })
            {
                log("AGENT_LLM_REGEX_RETRY", "LM Studio regex failure - retrying without tools");
                response = await _llm.ChatAsync(request.History, tools: null, cancellationToken);
            }

            if (response.IsComplete || response.ToolCalls is not { Count: > 0 })
            {
                var text = request.SanitizeAssistantText(response.Content ?? "[No response]");
                request.History.Add(ChatMessage.Assistant(text));
                log("AGENT_RESPONSE", text);

                return new AgentResponse
                {
                    Text = text,
                    Success = true,
                    ToolCallsMade = request.ToolCallsMade,
                    LlmRoundTrips = roundTrips
                };
            }

            request.History.Add(ChatMessage.AssistantToolCalls(response.ToolCalls));

            // Conflict resolution happens BEFORE any MCP side effect.
            var conflictResolution = ToolConflictMatrix.ResolveTurn(
                response.ToolCalls,
                allowedToolNames);

            foreach (var skipped in conflictResolution.Skipped)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reasonCode = ToolConflictMatrix.ToReasonCode(skipped.Reason);
                var isPolicyForbid = skipped.Reason == ToolConflictReason.PolicyForbid;
                var result = JsonSerializer.Serialize(new
                {
                    error = isPolicyForbid ? "tool_not_permitted" : "tool_conflict_skipped",
                    tool = skipped.ToolCall.Function.Name,
                    winner = skipped.WinnerTool,
                    reason = reasonCode,
                    detail = skipped.Detail
                });

                log(
                    isPolicyForbid ? "AGENT_TOOL_BLOCKED" : "AGENT_TOOL_CONFLICT",
                    $"tool={skipped.ToolCall.Function.Name}, winner={skipped.WinnerTool ?? "none"}, reason={reasonCode}, detail={skipped.Detail}");

                request.ToolCallsMade.Add(new ToolCallRecord
                {
                    ToolName = skipped.ToolCall.Function.Name,
                    Arguments = skipped.ToolCall.Function.Arguments,
                    Result = result,
                    Success = false
                });

                request.History.Add(ChatMessage.ToolResult(skipped.ToolCall.Id, result));
                log("AGENT_TOOL_RESULT", $"{skipped.ToolCall.Function.Name} -> skipped");
            }

            foreach (var toolCall in conflictResolution.Winners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var redactedInput = ToolCallRedactor.RedactInput(
                    toolCall.Function.Name,
                    toolCall.Function.Arguments);
                log("AGENT_TOOL_CALL", $"{toolCall.Function.Name}({redactedInput})");

                string result;
                bool success;
                try
                {
                    result = await _mcp.CallToolAsync(
                        toolCall.Function.Name,
                        toolCall.Function.Arguments,
                        cancellationToken);
                    success = true;
                }
                catch (Exception ex)
                {
                    result = $"Tool error: {ex.Message}";
                    success = false;
                }

                request.ToolCallsMade.Add(new ToolCallRecord
                {
                    ToolName = toolCall.Function.Name,
                    Arguments = toolCall.Function.Arguments,
                    Result = result,
                    Success = success
                });

                request.History.Add(ChatMessage.ToolResult(toolCall.Id, result));
                log("AGENT_TOOL_RESULT", $"{toolCall.Function.Name} -> {(success ? "ok" : "error")}");
            }

            if (roundTrips >= request.MaxRoundTrips)
            {
                const string bailMsg = "Reached maximum tool round-trips. Returning partial result.";
                request.History.Add(ChatMessage.Assistant(bailMsg));
                log("AGENT_MAX_ROUNDS", bailMsg);
                return new AgentResponse
                {
                    Text = bailMsg,
                    Success = true,
                    ToolCallsMade = request.ToolCallsMade,
                    LlmRoundTrips = roundTrips
                };
            }
        }
    }

    private static bool IsLmStudioRegexFailure(HttpRequestException ex)
    {
        var msg = ex.Message ?? "";
        return msg.Contains("Failed to process regex", StringComparison.OrdinalIgnoreCase);
    }
}

