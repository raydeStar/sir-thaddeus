using System.Text.Json;
using System.Text;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
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
            PersonalityFewShotInjector.InjectInPlace(messagesToSend, request.FewShotExamples);

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

                if (ShouldRepairBrokenWebSynthesis(text, request.ToolCallsMade, request.History))
                {
                    var repairedResponse = await TryRepairSuccessfulWebSynthesisAsync(
                        request,
                        roundTrips,
                        log,
                        cancellationToken);
                    if (repairedResponse is not null)
                        return repairedResponse;

                    if (!IsExplicitToolInvocationRequest(request.History))
                    {
                        var latestUserMessage = request.History
                            .LastOrDefault(message =>
                                message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(message.Content))
                            ?.Content?.Trim() ?? string.Empty;
                        var systemPrompt = request.History
                            .FirstOrDefault(message =>
                                message.Role.Equals("system", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(message.Content))
                            ?.Content ?? string.Empty;

                        return await OfflineWebReasoningResponder.BuildAsync(
                            _llm,
                            systemPrompt,
                            latestUserMessage,
                            memoryPackText: string.Empty,
                            history: request.History,
                            toolCallsMade: request.ToolCallsMade,
                            failureReason: "tool_unavailable",
                            cancellationToken);
                    }
                }

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
            var noResultsPayloadCount = 0;
            var timeoutErrorCount = 0;
            var unavailableErrorCount = 0;
            var budgetExceededCount = 0;
            var permissionDeniedCount = 0;
            var deniedToolNames = new List<string>();
            var executedToolNames = new List<string>();

            foreach (var toolCall in conflictResolution.Winners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var redactedInput = ToolCallRedactor.RedactInput(
                    toolCall.Function.Name,
                    toolCall.Function.Arguments);
                log("AGENT_TOOL_CALL", $"{toolCall.Function.Name}({redactedInput})");

                string result;
                bool transportSuccess;
                try
                {
                    result = await _mcp.CallToolAsync(
                        toolCall.Function.Name,
                        toolCall.Function.Arguments,
                        cancellationToken);
                    transportSuccess = true;
                }
                catch (Exception ex)
                {
                    result = $"Tool error: {ex.Message}";
                    transportSuccess = false;
                }

                var success = transportSuccess && !LooksLikeStructuredError(result);

                executedWinnerCount++;
                executedToolNames.Add(toolCall.Function.Name);
                if (success)
                    successfulPayloadCount++;
                if (success && LooksLikeNoResultsPayload(result))
                    noResultsPayloadCount++;
                if (IsTimeoutLikeResult(result))
                    timeoutErrorCount++;
                if (IsUnavailableLikeResult(result))
                    unavailableErrorCount++;
                if (IsBudgetExceededResult(result))
                    budgetExceededCount++;
                if (IsPermissionDeniedResult(result))
                {
                    permissionDeniedCount++;
                    deniedToolNames.Add(toolCall.Function.Name);
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

            if (executedWinnerCount > 0 &&
                noResultsPayloadCount == executedWinnerCount &&
                executedToolNames.Count > 0 &&
                executedToolNames.All(IsWebFamilyToolName) &&
                IsExplicitToolInvocationRequest(request.History))
            {
                var latestUserMessage = TryGetExplicitToolInvocationUserMessage(request.History)
                    ?? request.History
                        .LastOrDefault(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                        ?.Content;
                var explicitNoResultsMsg =
                    ExplicitWebNoResultsContractNormalizer.TryBuildResponse(
                        latestUserMessage,
                        request.ToolCallsMade)
                    ?? ExplicitWebNoResultsContractNormalizer.UnavailableMessage;
                request.History.Add(ChatMessage.Assistant(explicitNoResultsMsg));
                log("AGENT_EXPLICIT_WEB_NO_RESULTS_FALLBACK", explicitNoResultsMsg);

                return new AgentResponse
                {
                    Text = explicitNoResultsMsg,
                    Success = true,
                    ToolCallsMade = request.ToolCallsMade,
                    LlmRoundTrips = roundTrips
                };
            }

            // All tools failed with structured errors — return deterministic
            // messages instead of letting the LLM hallucinate a response.
            if (executedWinnerCount > 0 && successfulPayloadCount == 0)
            {
                if (timeoutErrorCount > 0)
                {
                    const string timeoutMsg =
                        "Live lookup timed out for this request, so I do not have confirmed results to quote right now. " +
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
                    if (executedToolNames.Any(IsRealtimeKnowledgeToolName))
                    {
                        return await BuildBestEffortOfflineFallbackAsync(
                            request,
                            roundTrips,
                            log,
                            "AGENT_UNAVAILABLE",
                            cancellationToken);
                    }

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

                if (permissionDeniedCount > 0)
                {
                    if (deniedToolNames.Any(IsWeatherToolName))
                    {
                        const string weatherDeniedMsg =
                            "I don't have permission to look up the weather right now.";
                        request.History.Add(ChatMessage.Assistant(weatherDeniedMsg));
                        log("AGENT_PERMISSION_WEATHER_FALLBACK", weatherDeniedMsg);
                        return new AgentResponse
                        {
                            Text = weatherDeniedMsg,
                            Success = true,
                            ToolCallsMade = request.ToolCallsMade,
                            LlmRoundTrips = roundTrips
                        };
                    }

                    if (deniedToolNames.Any(IsWebFamilyToolName))
                    {
                        var fallbackMessages = request.History
                            .Where(m => m.Role is "system" or "user" or "assistant")
                            .ToList();
                        fallbackMessages.Insert(0, ChatMessage.System(
                            "Answer with best effort from your existing non-real-time knowledge.\n" +
                            "Do not mention permissions, tools, network, or internet access.\n" +
                            "If the request depends on real-time/current events and certainty is low, say exactly: " +
                            "\"I do not know about real-time events right now.\""));

                        LlmResponse fallbackResponse;
                        try
                        {
                            fallbackResponse = await _llm.ChatAsync(fallbackMessages, tools: null, cancellationToken);
                        }
                        catch
                        {
                            const string realTimeFallbackMsg = "I do not know about real-time events right now.";
                            request.History.Add(ChatMessage.Assistant(realTimeFallbackMsg));
                            log("AGENT_PERMISSION_WEB_FALLBACK_STATIC", realTimeFallbackMsg);
                            return new AgentResponse
                            {
                                Text = realTimeFallbackMsg,
                                Success = true,
                                ToolCallsMade = request.ToolCallsMade,
                                LlmRoundTrips = roundTrips
                            };
                        }

                        var text = request.SanitizeAssistantText(
                            fallbackResponse.Content ?? "I do not know about real-time events right now.");
                        if (string.IsNullOrWhiteSpace(text))
                            text = "I do not know about real-time events right now.";

                        request.History.Add(ChatMessage.Assistant(text));
                        log("AGENT_PERMISSION_WEB_FALLBACK_LLM", text);
                        return new AgentResponse
                        {
                            Text = text,
                            Success = true,
                            ToolCallsMade = request.ToolCallsMade,
                            LlmRoundTrips = roundTrips + 1
                        };
                    }
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
            var trimmed = payload.TrimStart();
            return trimmed.StartsWith("### Error", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                   payload.Contains("tool error", StringComparison.OrdinalIgnoreCase) ||
                   payload.Contains("tool execution failed", StringComparison.OrdinalIgnoreCase) ||
                   payload.Contains("tool call blocked", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool LooksLikeNoResultsPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return true;

        var trimmed = payload.Trim();
        return trimmed.StartsWith("No results found for ", StringComparison.OrdinalIgnoreCase) ||
               (trimmed.StartsWith("[search:", StringComparison.OrdinalIgnoreCase) &&
                trimmed.Contains("0 result", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnavailableLikeResult(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        if (ContainsTransportOfflineSignal(payload))
            return true;

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
            return payload.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                   ContainsTransportOfflineSignal(payload);
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
                return ContainsTimeoutSignal(errorEl.GetString());

            if (errorEl.ValueKind == JsonValueKind.Object)
            {
                if (errorEl.TryGetProperty("code", out var codeEl) &&
                    codeEl.ValueKind == JsonValueKind.String &&
                    ContainsTimeoutSignal(codeEl.GetString()))
                {
                    return true;
                }

                if (errorEl.TryGetProperty("message", out var msgEl) &&
                    msgEl.ValueKind == JsonValueKind.String &&
                    ContainsTimeoutSignal(msgEl.GetString()))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return ContainsTimeoutSignal(payload);
        }
    }

    private static bool ContainsTimeoutSignal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("timed out", StringComparison.OrdinalIgnoreCase);
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

    private static bool IsPermissionDeniedResult(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var lower = payload.ToLowerInvariant();
        return lower.Contains("tool call blocked") ||
               lower.Contains("denied by user") ||
               lower.Contains("permission prompt cancelled") ||
               lower.Contains("disabled in settings");
    }

    private static bool IsWeatherToolName(string name)
    {
        var normalized = (name ?? "").Trim().ToLowerInvariant();
        return normalized.Contains("weather_forecast", StringComparison.Ordinal) ||
               normalized.Contains("weather_geocode", StringComparison.Ordinal);
    }

    private static bool IsWebFamilyToolName(string name)
    {
        var normalized = (name ?? "").Trim().ToLowerInvariant();
        return normalized.Contains("web_search", StringComparison.Ordinal) ||
               normalized.Contains("browser_navigate", StringComparison.Ordinal) ||
               normalized.Contains("places_lookup", StringComparison.Ordinal) ||
               normalized.Contains("feed_fetch", StringComparison.Ordinal) ||
               normalized.Contains("status_check_url", StringComparison.Ordinal);
    }

    private static bool IsRealtimeKnowledgeToolName(string name)
    {
        var normalized = (name ?? "").Trim().ToLowerInvariant();
        return IsWebFamilyToolName(normalized) ||
               normalized.Contains("weather_", StringComparison.Ordinal) ||
               normalized.Contains("resolve_timezone", StringComparison.Ordinal) ||
               normalized.Contains("time_now", StringComparison.Ordinal) ||
               normalized.Contains("holidays_", StringComparison.Ordinal);
    }

    private static bool ShouldRepairBrokenWebSynthesis(
        string text,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        IReadOnlyList<ChatMessage> history)
    {
        if (!IsBrokenWebOutcomeAssistantText(text))
            return false;

        var webCalls = toolCallsMade.Where(call => IsWebFamilyToolName(call.ToolName)).ToList();
        if (webCalls.Count == 0)
            return false;

        var latestUserMessage = history
            .LastOrDefault(message =>
                message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(message.Content))
            ?.Content;
        if (!string.IsNullOrWhiteSpace(latestUserMessage) &&
            IntentFeatureExtractor.LooksLikeSelfContainedKnowledgeOrReasoningPrompt(
                latestUserMessage.Trim().ToLowerInvariant()))
        {
            return true;
        }

        return webCalls.Any(call =>
            IsWebFamilyToolName(call.ToolName) &&
            call.Success &&
            !LooksLikeStructuredError(call.Result) &&
            !LooksLikeNoResultsPayload(call.Result));
    }

    private static bool IsBrokenWebOutcomeAssistantText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        return string.Equals(
                   trimmed,
                   ExplicitWebNoResultsContractNormalizer.UnavailableMessage,
                   StringComparison.Ordinal) ||
               trimmed.StartsWith(
                   ExplicitWebNoResultsContractNormalizer.UnavailableMessage,
                   StringComparison.Ordinal) ||
               string.Equals(
                   trimmed,
                   ExplicitWebNoResultsContractNormalizer.TimeoutMessage,
                   StringComparison.Ordinal) ||
               trimmed.StartsWith(
                   ExplicitWebNoResultsContractNormalizer.TimeoutMessage,
                   StringComparison.Ordinal) ||
               trimmed.StartsWith(
                   "Live lookup is unavailable for this turn",
                   StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith(
                   "Live lookup timed out for this request",
                   StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AgentResponse?> TryRepairSuccessfulWebSynthesisAsync(
        ToolLoopExecutionRequest request,
        int roundTrips,
        Action<string, string> log,
        CancellationToken cancellationToken)
    {
        var latestUserMessage = request.History
            .LastOrDefault(message =>
                message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(message.Content))
            ?.Content?.Trim();

        var successfulWebCalls = request.ToolCallsMade
            .Where(call =>
                IsWebFamilyToolName(call.ToolName) &&
                call.Success &&
                !LooksLikeStructuredError(call.Result) &&
                !LooksLikeNoResultsPayload(call.Result))
            .TakeLast(6)
            .ToList();

        if (string.IsNullOrWhiteSpace(latestUserMessage) || successfulWebCalls.Count == 0)
        {
            log("AGENT_WEB_SYNTHESIS_REPAIR_SKIPPED", "Missing user request or successful web results.");
            return null;
        }

        var repairInput = BuildFocusedWebRepairInput(latestUserMessage, successfulWebCalls);
        var repairMessages = new List<ChatMessage>
        {
            ChatMessage.System(
                "You already have successful web tool results. " +
                "A prior draft incorrectly claimed the tool was unavailable. " +
                "Ignore that mistake and answer the user's request using only the retrieved results below. " +
                "Preserve the user's requested format and structure. " +
                "Do not mention tool availability, retries, permissions, network status, or internet access. " +
                "Do not call tools."),
            ChatMessage.User(repairInput)
        };

        LlmResponse repairedDraft;
        try
        {
            repairedDraft = await _llm.ChatAsync(repairMessages, tools: null, cancellationToken);
        }
        catch (Exception ex)
        {
            log("AGENT_WEB_SYNTHESIS_REPAIR_FAILED", ex.Message);
            return null;
        }

        var repairedText = request.SanitizeAssistantText(repairedDraft.Content ?? string.Empty);
        if (string.IsNullOrWhiteSpace(repairedText) ||
            IsBrokenWebOutcomeAssistantText(repairedText))
        {
            log("AGENT_WEB_SYNTHESIS_REPAIR_SKIPPED", "Repair draft remained empty or unavailable-like.");
            return null;
        }

        request.History.Add(ChatMessage.Assistant(repairedText));
        log("AGENT_WEB_SYNTHESIS_REPAIRED", repairedText);
        return new AgentResponse
        {
            Text = repairedText,
            Success = true,
            ToolCallsMade = request.ToolCallsMade,
            LlmRoundTrips = roundTrips + 1
        };
    }

    private static string BuildFocusedWebRepairInput(
        string userRequest,
        IReadOnlyList<ToolCallRecord> successfulWebCalls)
    {
        var sb = new StringBuilder();
        sb.AppendLine("User request:");
        sb.AppendLine(userRequest);
        sb.AppendLine();
        sb.AppendLine("Retrieved web tool results:");

        foreach (var call in successfulWebCalls)
        {
            sb.AppendLine();
            sb.AppendLine($"Tool: {call.ToolName}");
            if (!string.IsNullOrWhiteSpace(call.Arguments))
            {
                sb.AppendLine("Arguments:");
                sb.AppendLine(call.Arguments.Trim());
            }

            if (!string.IsNullOrWhiteSpace(call.Result))
            {
                sb.AppendLine("Result:");
                sb.AppendLine(call.Result.Trim());
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static bool IsExplicitToolInvocationRequest(IReadOnlyList<ChatMessage> history)
    {
        return TryGetExplicitToolInvocationUserMessage(history) is not null;
    }

    private static string? TryGetExplicitToolInvocationUserMessage(IReadOnlyList<ChatMessage> history)
    {
        for (var index = history.Count - 1; index >= 0; index--)
        {
            var message = history[index];
            if (!message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            var candidate = message.Content.Trim();
            if (IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(candidate.ToLowerInvariant()) is not null)
                return candidate;
        }

        return null;
    }

    private static string EnsureUnavailableKeywordForExplicitToolRequest(
        string text,
        IReadOnlyList<ChatMessage> history)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !IsExplicitToolInvocationRequest(history) ||
            text.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return $"Live lookup is unavailable for this turn, so this answer is best-effort and may be out of date.\n\n{text.Trim()}";
    }

    private async Task<AgentResponse> BuildBestEffortOfflineFallbackAsync(
        ToolLoopExecutionRequest request,
        int roundTrips,
        Action<string, string> log,
        string logPrefix,
        CancellationToken cancellationToken)
    {
        var latestUserMessage = request.History
            .LastOrDefault(m =>
                m.Role.Equals("user", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(m.Content))
            ?.Content?.Trim();

        var fallbackMessages = request.History
            .Where(m => m.Role is "system" or "user" or "assistant")
            .ToList();

        fallbackMessages.Insert(0, ChatMessage.System(
            "Live tool-backed lookup is offline for this turn.\n" +
            "Answer with best effort from your existing non-real-time knowledge.\n" +
            "Lead with the best answer you can give, not a refusal.\n" +
            "Do not mention tools, permissions, network, or internet status unless the user explicitly asks for diagnostics.\n" +
            "Do not start with 'I can't', 'I cannot', or similar capability disclaimers.\n" +
            "If the request depends on current events and certainty is low, be explicit about uncertainty and avoid fabricated specifics."));

        LlmResponse fallbackResponse;
        try
        {
            fallbackResponse = await _llm.ChatAsync(fallbackMessages, tools: null, cancellationToken);
        }
        catch
        {
            const string staticFallback =
                "I can still help using built-in knowledge, though live details may be out of date.";
            var finalizedStaticFallback = EnsureUnavailableKeywordForExplicitToolRequest(
                staticFallback,
                request.History);
            request.History.Add(ChatMessage.Assistant(finalizedStaticFallback));
            log($"{logPrefix}_FALLBACK_STATIC", finalizedStaticFallback);
            return new AgentResponse
            {
                Text = finalizedStaticFallback,
                Success = true,
                ToolCallsMade = request.ToolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var text = request.SanitizeAssistantText(
            fallbackResponse.Content ??
            "I can still help using built-in knowledge, though live details may be out of date.");

        if (!IsExplicitToolInvocationRequest(request.History) &&
            IsBrokenWebOutcomeAssistantText(text) &&
            !string.IsNullOrWhiteSpace(latestUserMessage))
        {
            try
            {
                var minimalFallbackResponse = await _llm.ChatAsync(
                    [
                        ChatMessage.System(
                            "Live tool-backed lookup is unavailable for this turn. " +
                            "Answer the user's request with your best non-real-time knowledge only. " +
                            "Do not mention tools, permissions, network status, retries, or internet access. " +
                            "Preserve the user's requested format."),
                        ChatMessage.User(latestUserMessage)
                    ],
                    tools: null,
                    cancellationToken);

                var minimalText = request.SanitizeAssistantText(minimalFallbackResponse.Content ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(minimalText) &&
                    !IsBrokenWebOutcomeAssistantText(minimalText))
                {
                    text = minimalText;
                }
            }
            catch
            {
                // Fall back to the first offline draft below.
            }
        }

        if (string.IsNullOrWhiteSpace(text))
            text = "I can still help using built-in knowledge, though live details may be out of date.";
        text = EnsureUnavailableKeywordForExplicitToolRequest(text, request.History);

        request.History.Add(ChatMessage.Assistant(text));
        log($"{logPrefix}_FALLBACK_LLM", text);
        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = request.ToolCallsMade,
            LlmRoundTrips = roundTrips + 1
        };
    }

    private static bool ContainsTransportOfflineSignal(string payload)
    {
        var lower = (payload ?? "").ToLowerInvariant();
        return lower.Contains("pipe is being closed", StringComparison.Ordinal) ||
               lower.Contains("broken pipe", StringComparison.Ordinal) ||
               lower.Contains("transport is unavailable", StringComparison.Ordinal) ||
               lower.Contains("mcp client is not initialized", StringComparison.Ordinal) ||
               lower.Contains("tool execution failed", StringComparison.Ordinal) && lower.Contains("pipe", StringComparison.Ordinal);
    }
}

