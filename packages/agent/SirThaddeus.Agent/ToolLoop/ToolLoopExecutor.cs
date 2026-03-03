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
    private readonly Orchestration.IPlanValidator _planValidator;

    public ToolLoopExecutor(ILlmClient llm, IMcpToolClient mcp, Orchestration.IPlanValidator? planValidator = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _planValidator = planValidator ?? new Orchestration.PlanValidator();
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

        const int MaxPlanRejections = 2;
        var planRejections = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            roundTrips++;

            log("AGENT_LLM_CALL", $"Round trip #{roundTrips}");
            LlmResponse response;
            
            var messagesToSend = request.History.ToList();
            AgentOrchestrator.InjectFewShotExamplesInPlace(messagesToSend, request.FewShotExamples);

            try
            {
                response = await _llm.ChatAsync(messagesToSend, tools, cancellationToken);
            }
            catch (HttpRequestException ex) when (IsLmStudioRegexFailure(ex) && tools is { Count: > 0 })
            {
                log("AGENT_LLM_REGEX_RETRY", "LM Studio regex failure - retrying without tools");
                response = await _llm.ChatAsync(messagesToSend, tools: null, cancellationToken);
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

            // Validate the proposed plan against the IntentV2 contract
            var proposedCalls = response.ToolCalls.Select(tc => new Orchestration.ProposedToolCall(tc.Function.Name, tc.Function.Arguments, tc.Id)).ToList();
            var validationResult = _planValidator.Validate(request.Decision, proposedCalls, request.Tools);

            if (!validationResult.IsValid)
            {
                planRejections++;
                log("AGENT_PLAN_REJECTED", $"reason={validationResult.RejectReasonCode}, attempt={planRejections}/{MaxPlanRejections}, details={validationResult.RepairPrompt}");

                if (planRejections >= MaxPlanRejections)
                {
                    var bailMsg = $"I tried to use tools for this request but kept selecting ones that aren't appropriate. Let me answer without tools instead.";
                    // Inject error results so the LLM sees the rejection, then fall through to a chat-only response.
                    foreach (var toolCall in response.ToolCalls)
                        request.History.Add(ChatMessage.ToolResult(toolCall.Id, $"System Error: {validationResult.RepairPrompt}"));

                    request.History.Add(ChatMessage.Assistant(bailMsg));
                    log("AGENT_PLAN_REJECTED_BAIL", bailMsg);
                    return new AgentResponse
                    {
                        Text = bailMsg,
                        Success = true,
                        ToolCallsMade = request.ToolCallsMade,
                        LlmRoundTrips = roundTrips
                    };
                }

                // Add tool results as errors for all tools so the LLM doesn't hang waiting for them
                foreach (var toolCall in response.ToolCalls)
                {
                    request.History.Add(ChatMessage.ToolResult(toolCall.Id, $"System Error: {validationResult.RepairPrompt}"));
                }
                
                // Continue loop so the LLM can try again
                continue;
            }

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

            var executedWinnerCount = 0;
            var successfulPayloadCount = 0;
            var timeoutErrorCount = 0;
            var unavailableErrorCount = 0;
            var budgetExceededCount = 0;

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

                executedWinnerCount++;
                if (success && !LooksLikeStructuredError(result))
                    successfulPayloadCount++;
                if (IsTimeoutLikeResult(result))
                    timeoutErrorCount++;
                if (IsUnavailableLikeResult(result))
                    unavailableErrorCount++;
                if (IsBudgetExceededResult(result))
                    budgetExceededCount++;

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

            // All tools failed with structured errors — return deterministic
            // messages instead of letting the LLM hallucinate a response.
            if (executedWinnerCount > 0 && successfulPayloadCount == 0)
            {
                if (timeoutErrorCount > 0)
                {
                    const string timeoutMsg =
                        "I hit a timeout while running web tools, so I couldn't complete that request right now. " +
                        "Please retry in a moment or narrow the query.";
                    request.History.Add(ChatMessage.Assistant(timeoutMsg));
                    log("AGENT_TIMEOUT_FALLBACK", timeoutMsg);

                    return new AgentResponse
                    {
                        Text = timeoutMsg,
                        Success = true,
                        ToolCallsMade = request.ToolCallsMade,
                        LlmRoundTrips = roundTrips
                    };
                }

                if (unavailableErrorCount > 0)
                {
                    const string unavailableMsg =
                        "The requested tool is currently unavailable. " +
                        "Please verify MCP server connectivity and try again.";
                    request.History.Add(ChatMessage.Assistant(unavailableMsg));
                    log("AGENT_UNAVAILABLE_FALLBACK", unavailableMsg);

                    return new AgentResponse
                    {
                        Text = unavailableMsg,
                        Success = true,
                        ToolCallsMade = request.ToolCallsMade,
                        LlmRoundTrips = roundTrips
                    };
                }

                if (budgetExceededCount > 0)
                {
                    const string budgetMsg =
                        "I reached the safety budget for tool calls in this turn. " +
                        "Please retry with a narrower request or increase tool budgets in settings.";
                    request.History.Add(ChatMessage.Assistant(budgetMsg));
                    log("AGENT_BUDGET_FALLBACK", budgetMsg);

                    return new AgentResponse
                    {
                        Text = budgetMsg,
                        Success = true,
                        ToolCallsMade = request.ToolCallsMade,
                        LlmRoundTrips = roundTrips
                    };
                }
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

    private static bool LooksLikeStructuredError(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("error", out _);
        }
        catch
        {
            return payload.Contains("tool error", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsUnavailableLikeResult(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("error", out var errorEl))
            {
                return false;
            }

            if (errorEl.ValueKind == JsonValueKind.String)
                return errorEl.GetString()?.Contains("unavailable", StringComparison.OrdinalIgnoreCase) == true;

            if (errorEl.ValueKind == JsonValueKind.Object)
            {
                if (errorEl.TryGetProperty("code", out var codeEl) &&
                    codeEl.ValueKind == JsonValueKind.String &&
                    codeEl.GetString()?.Contains("unavailable", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }

                if (errorEl.TryGetProperty("message", out var msgEl) &&
                    msgEl.ValueKind == JsonValueKind.String &&
                    msgEl.GetString()?.Contains("unavailable", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return payload.Contains("unavailable", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsTimeoutLikeResult(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("error", out var errorEl))
            {
                return false;
            }

            if (errorEl.ValueKind == JsonValueKind.String)
                return errorEl.GetString()?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true;

            if (errorEl.ValueKind == JsonValueKind.Object)
            {
                if (errorEl.TryGetProperty("code", out var codeEl) &&
                    codeEl.ValueKind == JsonValueKind.String &&
                    codeEl.GetString()?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }

                if (errorEl.TryGetProperty("message", out var msgEl) &&
                    msgEl.ValueKind == JsonValueKind.String &&
                    msgEl.GetString()?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return payload.Contains("timeout", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsBudgetExceededResult(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("error", out var errorEl))
            {
                return false;
            }

            return errorEl.ValueKind == JsonValueKind.String &&
                   string.Equals(errorEl.GetString(), "tool_budget_exceeded", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return payload.Contains("tool_budget_exceeded", StringComparison.OrdinalIgnoreCase);
        }
    }
}

