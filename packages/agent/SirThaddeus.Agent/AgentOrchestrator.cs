using System.Text;
using System.Text.Json;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent;

/// <summary>
/// Implements the agent loop: user message -> LLM -> tool calls -> LLM -> ... -> final text.
/// Holds in-memory conversation history and delegates tool execution to the MCP client.
/// </summary>
public sealed class AgentOrchestrator : IAgentOrchestrator
{
    private readonly ILlmClient _llm;
    private readonly IMcpToolClient _mcp;
    private readonly IAuditLogger _audit;
    private readonly string _systemPrompt;

    private readonly List<ChatMessage> _history = [];
    private const int MaxToolRoundTrips  = 10;  // Safety valve
    private const int DefaultWebSearchMaxResults = 5;

    // ── Web search tool names ────────────────────────────────────────
    // MCP stacks may register tools in snake_case or PascalCase.
    // Try the canonical name first, fall back to the alternate.
    private const string WebSearchToolName    = "web_search";
    private const string WebSearchToolNameAlt = "WebSearch";

    // ── Summary instruction injected after search results ────────────
    private const string WebSummaryInstruction =
        "\n\nSearch results are in the next message. " +
        "Summarize them. No URLs. Be brief.";

    // ── Token budget per intent ──────────────────────────────────────
    // Small models fill available space with filler. Tight caps force
    // them to be concise and reduce self-dialogue / instruction echoing.
    private const int MaxTokensCasual    = 256;
    private const int MaxTokensWebSummary = 512;
    private const int MaxTokensTooling   = 1024;

    // Hard ceiling on memory retrieval. If the MCP tool + SQLite +
    // optional embeddings don't finish in this window, we skip memory
    // entirely and proceed with the conversation. Non-negotiable.
    private static readonly TimeSpan MemoryRetrievalTimeout = TimeSpan.FromSeconds(4);

    // ── Onboarding prompts ────────────────────────────────────────────
    // Injected when no active profile is loaded for this session.
    //
    // Cold prompt: first turn — actively introduce yourself and ask.
    // Follow-up: subsequent turns — passively capture info if shared.
    //
    // The LLM MUST have memory tools available when these are active
    // (see the policy override in ProcessAsync). Without tools, the
    // model can't persist anything the user shares.

    private const string OnboardingColdPrompt =
        "\n\n[ONBOARDING]\n" +
        "No profile is loaded — you don't know who you're talking to yet.\n" +
        "Introduce yourself warmly (stay in character) and ask who they are.\n" +
        "If they share their name, IMMEDIATELY call memory_store_facts to save it:\n" +
        "  {\"subject\": \"user\", \"predicate\": \"name\", \"object\": \"<their name>\"}\n" +
        "Then ask 2-3 light questions to get to know them — what they work on, " +
        "a preference or two, how they like to be addressed.\n" +
        "Keep it casual and brief. If they say they'd rather not share or " +
        "want to skip, that is perfectly fine — just say something like " +
        "'No problem at all' and help them with whatever they need.\n" +
        "Do NOT ignore their original message — answer it too, " +
        "just weave the introduction in naturally.\n" +
        "[/ONBOARDING]\n";

    private const string OnboardingFollowUpPrompt =
        "\n\n[ONBOARDING]\n" +
        "You still don't know who this user is.\n" +
        "If they share personal details (name, preferences, etc.), " +
        "use memory_store_facts to save them.\n" +
        "Do NOT keep asking if they clearly want to move on — just help them.\n" +
        "[/ONBOARDING]\n";

    // ── History sliding window ───────────────────────────────────────
    // Keep the last N user+assistant turns so the context window stays
    // within a small model's effective range. The system prompt is
    // always retained as message[0].
    private const int MaxHistoryTurns = 12;

    /// <summary>
    /// The profile_id of the currently active user. Set from the
    /// Settings tab's dropdown. Passed to the MemoryRetrieve tool
    /// on every call so the MCP server knows who's talking —
    /// env vars can't cross process boundaries at runtime.
    /// </summary>
    public string? ActiveProfileId { get; set; }

    private enum ChatIntent
    {
        Casual,
        WebLookup,
        Tooling
    }

    public AgentOrchestrator(
        ILlmClient llm,
        IMcpToolClient mcp,
        IAuditLogger audit,
        string systemPrompt)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _systemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));

        // Seed the conversation with the system prompt
        _history.Add(ChatMessage.System(_systemPrompt));
    }

    /// <inheritdoc />
    public async Task<AgentResponse> ProcessAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return AgentResponse.FromError("Empty message.");

        // ── Add user message to history ──────────────────────────────
        _history.Add(ChatMessage.User(userMessage));
        TrimHistory();
        LogEvent("AGENT_USER_MESSAGE", userMessage);

        var toolCallsMade = new List<ToolCallRecord>();
        var roundTrips = 0;

        // ── Route: classify intent + determine requirements ──────────
        var route = await RouteMessageAsync(userMessage, cancellationToken);
        LogEvent("ROUTER_OUTPUT",
            $"intent={route.Intent}, confidence={route.Confidence:F2}, " +
            $"web={route.NeedsWeb}, screen={route.NeedsScreenRead}, " +
            $"file={route.NeedsFileAccess}, memory_w={route.NeedsMemoryWrite}, " +
            $"system={route.NeedsSystemExecute}, risk={route.RiskLevel}");

        // ── Policy: determine which tools the executor may see ───────
        var policy = PolicyGate.Evaluate(route);
        LogEvent("POLICY_DECISION",
            $"allowed=[{string.Join(", ", policy.AllowedTools)}], " +
            $"forbidden=[{string.Join(", ", policy.ForbiddenTools)}], " +
            $"permissions=[{string.Join(", ", policy.RequiredPermissions)}], " +
            $"useToolLoop={policy.UseToolLoop}");

        // Keep the old intent for the WebLookup deterministic path
        var intent = MapRouteToLegacyIntent(route);

        // ── Retrieve memory context (best-effort, hard timeout) ──
        // Called after classification but before the main LLM call.
        // The pack text is injected into the system prompt so the
        // model has relevant facts/events/chunks for its answer.
        // If retrieval takes longer than the timeout, we skip it
        // entirely rather than stalling the user's conversation.
        var memoryPackText = "";
        var onboardingNeeded = false;
        try
        {
            var memoryTask = RetrieveMemoryContextAsync(userMessage, cancellationToken);
            if (await Task.WhenAny(memoryTask, Task.Delay(MemoryRetrievalTimeout, cancellationToken)) == memoryTask)
            {
                (memoryPackText, onboardingNeeded) = await memoryTask;

                // Surface the retrieval in the activity log so the user
                // can see what was recalled (or that nothing was found).
                var summary = string.IsNullOrWhiteSpace(memoryPackText)
                    ? "No relevant memories found."
                    : memoryPackText.Replace("\n", " ").Trim();
                if (summary.Length > 200) summary = summary[..200] + "\u2026";

                toolCallsMade.Add(new ToolCallRecord
                {
                    ToolName  = "MemoryRetrieve",
                    Arguments = $"{{\"query\":\"{Truncate(userMessage, 80)}\"}}",
                    Result    = summary,
                    Success   = true
                });
            }
            else
            {
                LogEvent("MEMORY_TIMEOUT", "Memory retrieval exceeded timeout — skipped.");
                toolCallsMade.Add(new ToolCallRecord
                {
                    ToolName  = "MemoryRetrieve",
                    Arguments = $"{{\"query\":\"{Truncate(userMessage, 80)}\"}}",
                    Result    = "Timeout — skipped.",
                    Success   = false
                });
            }
        }
        catch
        {
            // Swallow — memory is best-effort
        }

        // ── Onboarding injection ──────────────────────────────────────
        // When no active profile is loaded and this is the first turn,
        // inject the "get to know you" prompt. On subsequent turns,
        // inject a lighter reminder that passively captures info.
        //
        // Also force the tool loop on so the LLM can call
        // memory_store_facts when the user shares their name.
        if (onboardingNeeded)
        {
            var isFirstTurn = _history.Count(m => m.Role == "user") <= 1;
            memoryPackText = isFirstTurn
                ? OnboardingColdPrompt
                : OnboardingFollowUpPrompt;

            // Upgrade chat-only → memory_write so the LLM has access
            // to memory tools. Without this, the model is told to call
            // memory_store_facts but has no tools available.
            //
            // IMPORTANT: only override Casual intent. Do NOT touch
            // WebLookup, Tooling, or other intents — their routing
            // takes priority over onboarding. The onboarding prompt
            // is still injected into the system context regardless,
            // so the LLM can passively capture info during any flow.
            if (intent == ChatIntent.Casual)
            {
                route = MakeRoute(Intents.MemoryWrite, confidence: 0.9,
                    needsMemoryWrite: true);
                policy = PolicyGate.Evaluate(route);
                intent = MapRouteToLegacyIntent(route);
            }

            LogEvent("ONBOARDING_INJECTED",
                isFirstTurn ? "First turn — introducing and asking who the user is."
                            : "Follow-up — passively capturing info.");
        }

        try
        {
            // ── Web lookup: LLM decides search terms via tool call ─────
            if (intent == ChatIntent.WebLookup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await ExecuteWebSearchAsync(
                    userMessage, memoryPackText,
                    toolCallsMade, roundTrips, "AGENT", cancellationToken);
            }

            // ── Inject memory context ─────────────────────────────────
            if (!string.IsNullOrWhiteSpace(memoryPackText))
                InjectMemoryIntoHistoryInPlace(_history, memoryPackText);

            // ── Chat-only: skip tool loop entirely ───────────────────
            // No tools, no function-calling grammar. The LLM just
            // responds with text. Fastest path for casual conversation.
            if (!policy.UseToolLoop)
            {
                cancellationToken.ThrowIfCancellationRequested();
                roundTrips++;

                var messages = _history.ToList();
                var response = await CallLlmWithRetrySafe(
                    messages, roundTrips, MaxTokensCasual, cancellationToken);

                var text = TruncateSelfDialogue(response.Content ?? "[No response]");
                text = StripRawTemplateTokens(text);

                // ── Fallback: if template tokens ate the whole response,
                // the user probably asked a follow-up about something
                // the model can't answer from memory alone (e.g., a
                // person or event from a previous web search). Try a
                // web search before giving up. ──────────────────────────
                if (string.IsNullOrWhiteSpace(text))
                {
                    LogEvent("CHAT_FALLBACK_TO_SEARCH",
                        "Response was all template garbage — " +
                        "falling back to web search.");

                    return await FallbackToWebSearchAsync(
                        userMessage, memoryPackText,
                        toolCallsMade, roundTrips, cancellationToken);
                }

                _history.Add(ChatMessage.Assistant(text));
                LogEvent("AGENT_RESPONSE", text);

                return new AgentResponse
                {
                    Text = text,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = roundTrips
                };
            }

            // ── Policy-filtered tool loop ────────────────────────────
            // Build the full tool set, then filter through the policy
            // gate. The executor only sees what the policy allows.
            var allTools = await BuildToolDefinitionsAsync(cancellationToken);
            var tools = PolicyGate.FilterTools(allTools, policy);

            LogEvent("AGENT_TOOLS_POLICY_FILTERED",
                $"{tools.Count} tool(s) from {allTools.Count} total: " +
                $"[{string.Join(", ", tools.Select(t => t.Function.Name))}]");

            return await RunToolLoopAsync(
                tools, toolCallsMade, roundTrips, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogEvent("AGENT_CANCELLED", "Processing was cancelled.");
            return new AgentResponse
            {
                Text = "Request was cancelled.",
                Success = false,
                Error = "Cancelled",
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }
        catch (Exception ex)
        {
            LogEvent("AGENT_ERROR", ex.Message);
            return new AgentResponse
            {
                Text          = $"Error: {ex.Message}",
                Success       = false,
                Error         = ex.Message,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Tool Loop
    //
    // Shared by both casual (memory-only tools) and tooling (all tools)
    // paths. Iterates until the LLM produces a final text answer or
    // we hit the safety cap.
    // ─────────────────────────────────────────────────────────────────

    private async Task<AgentResponse> RunToolLoopAsync(
        IReadOnlyList<ToolDefinition> tools,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            roundTrips++;

            // ── Call the LLM ─────────────────────────────────────
            LogEvent("AGENT_LLM_CALL", $"Round trip #{roundTrips}");
            LlmResponse response;
            try
            {
                response = await _llm.ChatAsync(_history, tools, cancellationToken);
            }
            catch (HttpRequestException ex) when (IsLmStudioRegexFailure(ex) && tools is { Count: > 0 })
            {
                // LM Studio sometimes fails to compile the function-calling grammar
                // ("Failed to process regex") on follow-up calls after tool results.
                // Retrying *without tools* avoids schema/grammar compilation entirely
                // and allows the model to summarize the tool outputs normally.
                LogEvent("AGENT_LLM_REGEX_RETRY", "LM Studio regex failure — retrying without tools");
                response = await _llm.ChatAsync(_history, tools: null, cancellationToken);
            }

            // ── If the model produced a final answer, we're done ─
            if (response.IsComplete || response.ToolCalls is not { Count: > 0 })
            {
                var text = TruncateSelfDialogue(response.Content ?? "[No response]");
                text = StripRawTemplateTokens(text);
                _history.Add(ChatMessage.Assistant(text));
                LogEvent("AGENT_RESPONSE", text);

                return new AgentResponse
                {
                    Text = text,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = roundTrips
                };
            }

            // ── Model wants to call tools ────────────────────────
            _history.Add(ChatMessage.AssistantToolCalls(response.ToolCalls));

            foreach (var toolCall in response.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LogEvent("AGENT_TOOL_CALL", $"{toolCall.Function.Name}({toolCall.Function.Arguments})");

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

                toolCallsMade.Add(new ToolCallRecord
                {
                    ToolName = toolCall.Function.Name,
                    Arguments = toolCall.Function.Arguments,
                    Result = result,
                    Success = success
                });

                // Feed the result back into the conversation
                _history.Add(ChatMessage.ToolResult(toolCall.Id, result));
                LogEvent("AGENT_TOOL_RESULT", $"{toolCall.Function.Name} -> {(success ? "ok" : "error")}");
            }

            // Safety: prevent infinite loops
            if (roundTrips >= MaxToolRoundTrips)
            {
                var bailMsg = "Reached maximum tool round-trips. Returning partial result.";
                _history.Add(ChatMessage.System(bailMsg));
                LogEvent("AGENT_MAX_ROUNDS", bailMsg);
                return new AgentResponse
                {
                    Text = bailMsg,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
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

    // ─────────────────────────────────────────────────────────────────
    // Web Search Execution (shared pipeline)
    //
    // Single implementation of the extract → search → summarize flow.
    // Called from the primary WebLookup intent path and from the
    // chat-only fallback. Keeps all tool-name negotiation, raw-dump
    // rewriting, and template stripping in one place.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Core web search pipeline: extracts a query from the user message,
    /// calls the <c>web_search</c> MCP tool, and summarizes the results.
    /// </summary>
    /// <param name="logPrefix">
    /// Prefix for audit log events — distinguishes primary ("AGENT")
    /// from fallback ("FALLBACK") search paths.
    /// </param>
    private async Task<AgentResponse> ExecuteWebSearchAsync(
        string userMessage,
        string memoryPackText,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        string logPrefix,
        CancellationToken cancellationToken)
    {
        // ── 1. Extract search query via structured tool call ─────────
        var (searchQuery, recency) = await ExtractSearchViaToolCallAsync(
            userMessage, memoryPackText, cancellationToken);
        LogEvent($"{logPrefix}_SEARCH_QUERY",
            $"\"{userMessage}\" → \"{searchQuery}\" (recency={recency})");

        // ── 2. Execute web_search MCP tool ───────────────────────────
        var webSearchArgs = JsonSerializer.Serialize(new
        {
            query      = searchQuery,
            maxResults = DefaultWebSearchMaxResults,
            recency
        });

        var toolName = WebSearchToolName;
        string toolResult;
        var toolOk = false;

        try
        {
            LogEvent("AGENT_TOOL_CALL", $"{toolName}({webSearchArgs})");
            toolResult = await _mcp.CallToolAsync(toolName, webSearchArgs, cancellationToken);
            toolOk = true;
        }
        catch (Exception ex)
        {
            // Back-compat: some MCP stacks register PascalCase tool names.
            try
            {
                toolName = WebSearchToolNameAlt;
                LogEvent("AGENT_TOOL_CALL", $"{toolName}({webSearchArgs})");
                toolResult = await _mcp.CallToolAsync(toolName, webSearchArgs, cancellationToken);
                toolOk = true;
            }
            catch
            {
                toolResult = $"Tool error: {ex.Message}";
            }
        }

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName  = toolName,
            Arguments = webSearchArgs,
            Result    = toolResult,
            Success   = toolOk
        });
        LogEvent("AGENT_TOOL_RESULT", $"{toolName} -> {(toolOk ? "ok" : "error")}");

        // ── 3. Summarize results ─────────────────────────────────────
        // Feed tool output as a User message (not a ToolResult).
        // Mistral's Jinja template requires tool_call_ids to be exactly
        // 9-char alphanumeric, and ToolResult roles must follow an
        // assistant message with matching tool_calls. A plain User
        // message sidesteps the whole template minefield.
        roundTrips++;

        var messagesForSummary = InjectModeIntoSystemPrompt(
            _history, memoryPackText + WebSummaryInstruction);
        messagesForSummary.Add(ChatMessage.User(
            "[Search results — reference only, do not display to user]\n" + toolResult));

        var response = await CallLlmWithRetrySafe(
            messagesForSummary, roundTrips, MaxTokensWebSummary, cancellationToken);

        var text = TruncateSelfDialogue(response.Content ?? "[No response]");

        // ── 4. Raw dump → rewrite ────────────────────────────────────
        if (LooksLikeRawDump(text))
        {
            LogEvent("AGENT_REWRITE", "Response looked like a raw dump — rewriting");
            var rewriteMessages = new List<ChatMessage>
            {
                ChatMessage.System(
                    _systemPrompt + " " +
                    "Rewrite the draft into the final answer. " +
                    "Casual tone. Bottom line first. 2-3 short paragraphs. " +
                    "No URLs. No lists of sources. No copied excerpts."),
                ChatMessage.User(text)
            };

            roundTrips++;
            var rewritten = await CallLlmWithRetrySafe(
                rewriteMessages, roundTrips, MaxTokensWebSummary, cancellationToken);
            if (!string.IsNullOrWhiteSpace(rewritten.Content))
                text = rewritten.Content!;
        }

        // ── 5. Strip template garbage ────────────────────────────────
        text = StripRawTemplateTokens(text);
        if (string.IsNullOrWhiteSpace(text))
            text = "I wasn't able to generate a clean answer for that. " +
                   "Could you try asking a different way?";

        _history.Add(ChatMessage.Assistant(text));
        LogEvent("AGENT_RESPONSE", text);

        return new AgentResponse
        {
            Text          = text,
            Success       = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Web Search Fallback
    //
    // When the chat-only path produces garbage (template tokens, empty
    // response), the user likely asked a follow-up about something the
    // model can't answer from memory alone. Rather than returning a
    // useless "something went sideways" message, we try a web search.
    // This handles the common pattern:
    //   Turn 1: "pull up the news"  → web search → great summary
    //   Turn 2: "whats with X?"     → chat-only  → garbage
    //                               → fallback   → web search for X
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the web search pipeline as a fallback when the chat-only
    /// path fails. Wraps <see cref="ExecuteWebSearchAsync"/> with
    /// error handling so a search failure doesn't crash the turn.
    /// </summary>
    private async Task<AgentResponse> FallbackToWebSearchAsync(
        string userMessage,
        string memoryPackText,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteWebSearchAsync(
                userMessage, memoryPackText,
                toolCallsMade, roundTrips, "FALLBACK", cancellationToken);
        }
        catch (Exception ex)
        {
            LogEvent("FALLBACK_SEARCH_FAIL", ex.Message);

            var fallbackMsg = "I wasn't able to generate a clean answer for that. " +
                              "Could you try asking a different way?";
            _history.Add(ChatMessage.Assistant(fallbackMsg));

            return new AgentResponse
            {
                Text          = fallbackMsg,
                Success       = false,
                Error         = ex.Message,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }
    }

    /// <summary>
    /// Calls the LLM with escalating retry for the "Failed to process regex"
    /// error that LM Studio throws when its grammar engine chokes.
    ///
    /// Strategy:
    ///   1. Try the full message list as-is.
    ///   2. On regex failure, wait briefly and retry the same call.
    ///   3. If that also fails, fall back to a minimal message set
    ///      (system prompt + last user message only) to eliminate
    ///      any message structure the template can't handle.
    /// </summary>
    private Task<LlmResponse> CallLlmWithRetrySafe(
        IReadOnlyList<ChatMessage> messages,
        int roundTrip,
        CancellationToken cancellationToken)
        => CallLlmWithRetrySafe(messages, roundTrip, maxTokens: null, cancellationToken);

    private async Task<LlmResponse> CallLlmWithRetrySafe(
        IReadOnlyList<ChatMessage> messages,
        int roundTrip,
        int? maxTokens,
        CancellationToken cancellationToken)
    {
        LogEvent("AGENT_LLM_CALL", $"Round trip #{roundTrip}" +
            (maxTokens.HasValue ? $" (max_tokens={maxTokens})" : ""));

        Task<LlmResponse> Call(IReadOnlyList<ChatMessage> msgs) =>
            maxTokens.HasValue
                ? _llm.ChatAsync(msgs, tools: null, maxTokens.Value, cancellationToken)
                : _llm.ChatAsync(msgs, tools: null, cancellationToken);

        // ── Attempt 1: full message list ─────────────────────────────
        try
        {
            return await Call(messages);
        }
        catch (HttpRequestException ex) when (IsLmStudioRegexFailure(ex))
        {
            LogEvent("AGENT_LLM_REGEX_RETRY",
                "Regex failure — retrying same call after 500 ms");
        }

        await Task.Delay(500, cancellationToken);

        // ── Attempt 2: same messages, second chance ──────────────────
        try
        {
            return await Call(messages);
        }
        catch (HttpRequestException ex) when (IsLmStudioRegexFailure(ex))
        {
            LogEvent("AGENT_LLM_REGEX_RETRY",
                "Regex failure persisted — falling back to minimal message set");
        }

        // ── Attempt 3: minimal messages (system + last user only) ────
        var minimal = new List<ChatMessage>();
        var sysMsg = messages.FirstOrDefault(m => m.Role == "system");
        var lastUser = messages.LastOrDefault(m => m.Role == "user");

        if (sysMsg is not null) minimal.Add(sysMsg);
        if (lastUser is not null) minimal.Add(lastUser);

        try
        {
            return minimal.Count > 0
                ? await Call(minimal)
                : await Call(messages);
        }
        catch (HttpRequestException ex) when (IsLmStudioRegexFailure(ex))
        {
            // All three attempts failed. Rather than crashing the
            // entire conversation, return a graceful error message
            // so the user can retry or switch models.
            LogEvent("AGENT_LLM_REGEX_EXHAUSTED",
                "All retry attempts failed — LM Studio grammar engine is " +
                "unresponsive for this model. The user should retry or " +
                "check the model configuration.");

            return new LlmResponse
            {
                IsComplete   = true,
                Content      = "I'm having trouble with the language model right now — " +
                               "it keeps rejecting my requests. Try sending your " +
                               "message again, or check if the model needs a reload " +
                               "in LM Studio.",
                FinishReason = "error"
            };
        }
    }

    /// <summary>
    /// Keeps the history within a sliding window so small models don't
    /// lose coherence as the context fills up. The system prompt
    /// (message[0]) is always preserved; older turns are evicted FIFO.
    /// </summary>
    private void TrimHistory()
    {
        // Count non-system messages
        var turnMessages = _history.Count(m => m.Role != "system");
        if (turnMessages <= MaxHistoryTurns)
            return;

        var excess = turnMessages - MaxHistoryTurns;
        var removed = 0;
        for (var i = _history.Count - 1; i >= 0 && removed < excess; i--)
        {
            // Walk backwards through the list but remove from the FRONT
            // (oldest non-system messages). Easier to just rebuild:
        }

        // Rebuild: keep system prompt + last N messages
        var sysPrompt = _history.FirstOrDefault(m => m.Role == "system");
        var recent = _history.Where(m => m.Role != "system")
                             .TakeLast(MaxHistoryTurns)
                             .ToList();

        _history.Clear();
        if (sysPrompt is not null) _history.Add(sysPrompt);
        _history.AddRange(recent);

        LogEvent("AGENT_HISTORY_TRIM",
            $"Trimmed to {_history.Count} messages ({MaxHistoryTurns} turns)");
    }

    /// <inheritdoc />
    public void ResetConversation()
    {
        _history.Clear();
        _history.Add(ChatMessage.System(_systemPrompt));
        LogEvent("AGENT_RESET", "Conversation history cleared.");
    }

    /// <inheritdoc />
    public async Task<int> GetAvailableToolCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var tools = await _mcp.ListToolsAsync(cancellationToken);
            return tools.Count;
        }
        catch
        {
            return 0;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ToolDefinition>> BuildToolDefinitionsAsync(
        CancellationToken ct)
    {
        try
        {
            var mcpTools = await _mcp.ListToolsAsync(ct);
            var definitions = mcpTools.Select(t => new ToolDefinition
            {
                Function = new FunctionDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = SanitizeSchemaForLocalLlm(t.InputSchema)
                }
            }).ToList();

            LogEvent("AGENT_TOOLS_LOADED", $"{definitions.Count} tool(s): {string.Join(", ", definitions.Select(d => d.Function.Name))}");

            // Log sanitized schemas for debugging "Failed to process regex" errors
            foreach (var def in definitions)
            {
                var schemaJson = JsonSerializer.Serialize(def.Function.Parameters, new JsonSerializerOptions { WriteIndented = false });
                LogEvent("AGENT_TOOL_SCHEMA", $"{def.Function.Name}: {schemaJson}");
            }

            return definitions;
        }
        catch (Exception ex)
        {
            // Don't silently swallow this — log it clearly
            LogEvent("AGENT_TOOLS_FAILED", $"MCP tool discovery failed: {ex.Message}");
            return [];
        }
    }

    // BuildMemoryOnlyToolsAsync removed — replaced by PolicyGate.FilterTools

    // ─────────────────────────────────────────────────────────────────
    // Schema Sanitization
    //
    // The ModelContextProtocol library (via System.Text.Json's
    // JsonSchemaExporter) generates JSON Schemas that may contain
    // constructs local LLMs can't handle — $schema, $defs, $ref,
    // anyOf/oneOf, additionalProperties, etc. LM Studio's grammar
    // engine compiles schemas into regex patterns, and these advanced
    // constructs cause "Failed to process regex" errors.
    //
    // Common offenders:
    //   - Optional params become: anyOf: [{type: "integer"}, {type: "null"}]
    //   - CancellationToken might leak into the schema
    //   - Default values, $ref chains, additionalProperties
    //
    // This sanitizer strips schemas to what LM Studio can handle:
    //   type, properties, required, description, enum, items
    // ─────────────────────────────────────────────────────────────────

    private static object SanitizeSchemaForLocalLlm(object rawSchema)
    {
        try
        {
            var json = JsonSerializer.Serialize(rawSchema);
            using var doc = JsonDocument.Parse(json);
            var clean = CleanSchemaNode(doc.RootElement);
            return clean;
        }
        catch
        {
            return new { type = "object", properties = new { } };
        }
    }

    private static Dictionary<string, object> CleanSchemaNode(JsonElement node)
    {
        var result = new Dictionary<string, object>();

        // ── Handle anyOf / oneOf (optional params, union types) ──────
        // MCP SDK generates these for C# optional/nullable parameters:
        //   "anyOf": [{"type": "integer"}, {"type": "null"}]
        // Extract the first non-null type and merge it into this node.
        if (TryResolveUnionType(node, out var resolvedType, out var resolvedNode))
        {
            result["type"] = resolvedType;

            // If the resolved node has sub-properties (e.g. a complex type),
            // recurse into it
            if (resolvedNode.HasValue && resolvedNode.Value.ValueKind == JsonValueKind.Object)
            {
                var inner = CleanSchemaNode(resolvedNode.Value);
                foreach (var kv in inner)
                {
                    if (kv.Key != "type") // already set above
                        result[kv.Key] = kv.Value;
                }
            }
        }

        foreach (var prop in node.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "type":
                    // Handle type arrays: ["string", "null"] → "string"
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        var types = prop.Value.EnumerateArray()
                            .Where(v => v.ValueKind == JsonValueKind.String)
                            .Select(v => v.GetString()!)
                            .Where(t => t != "null")
                            .ToList();
                        result["type"] = types.Count > 0 ? types[0] : "string";
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        result["type"] = prop.Value.GetString()!;
                    }
                    break;

                case "description":
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        // Strip characters that may break LM Studio's regex
                        var desc = prop.Value.GetString()!
                            .Replace("(", "")
                            .Replace(")", "")
                            .Replace("[", "")
                            .Replace("]", "")
                            .Replace("{", "")
                            .Replace("}", "");
                        result["description"] = desc;
                    }
                    break;

                case "required":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        result["required"] = prop.Value.EnumerateArray()
                            .Where(v => v.ValueKind == JsonValueKind.String)
                            .Select(v => v.GetString()!)
                            .ToArray();
                    }
                    break;

                case "enum":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        result["enum"] = prop.Value.EnumerateArray()
                            .Select(v => v.ToString())
                            .ToArray();
                    }
                    break;

                case "properties":
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        var props = new Dictionary<string, object>();
                        foreach (var p in prop.Value.EnumerateObject())
                        {
                            // Skip CancellationToken if it leaks into schema
                            if (p.Name.Equals("cancellationToken", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (p.Value.ValueKind == JsonValueKind.Object)
                                props[p.Name] = CleanSchemaNode(p.Value);
                        }
                        result["properties"] = props;
                    }
                    break;

                case "items":
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                        result["items"] = CleanSchemaNode(prop.Value);
                    break;

                // Everything else ($schema, $defs, $ref, anyOf, oneOf,
                // allOf, additionalProperties, default, const, etc.)
                // intentionally dropped.
            }
        }

        // Ensure type is always present
        if (!result.ContainsKey("type"))
            result["type"] = "object";

        return result;
    }

    /// <summary>
    /// Resolves anyOf/oneOf union types by extracting the first non-null type.
    /// MCP SDK generates these for optional C# parameters:
    ///   "anyOf": [{"type": "integer"}, {"type": "null"}]
    /// We extract "integer" as the resolved type.
    /// </summary>
    private static bool TryResolveUnionType(
        JsonElement node,
        out string resolvedType,
        out JsonElement? resolvedNode)
    {
        resolvedType = "string";
        resolvedNode = null;

        // Check for anyOf or oneOf arrays
        foreach (var keyword in new[] { "anyOf", "oneOf" })
        {
            if (!node.TryGetProperty(keyword, out var unionArray) ||
                unionArray.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var alt in unionArray.EnumerateArray())
            {
                if (alt.ValueKind != JsonValueKind.Object)
                    continue;

                if (!alt.TryGetProperty("type", out var typeEl) ||
                    typeEl.ValueKind != JsonValueKind.String)
                    continue;

                var typeName = typeEl.GetString();
                if (typeName == "null")
                    continue; // Skip the null alternative

                resolvedType = typeName!;
                resolvedNode = alt;
                return true;
            }

            // Had a union but couldn't resolve — still return true to prevent
            // the node from being treated as a plain object
            return true;
        }

        return false;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "\u2026";

    private void LogEvent(string action, string detail)
    {
        _audit.Append(new AuditEvent
        {
            Actor = "agent",
            Action = action,
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["detail"] = detail
            }
        });
    }

    /// <summary>
    /// Creates a copy of the message history with the mode instruction
    /// merged into the first System message. Avoids adding a second
    /// System message, which breaks Mistral's strict Jinja template.
    /// </summary>
    // ─────────────────────────────────────────────────────────────────
    // Memory Retrieval
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Calls the MemoryRetrieve MCP tool and returns the packText for
    /// system prompt injection plus an onboarding flag. Returns empty
    /// string on any failure — memory retrieval is best-effort and must
    /// never block the main flow.
    ///
    /// When the conversation is brand-new (system prompt + this one user
    /// message only) and the message looks like a greeting, we pass
    /// <c>mode = "greet"</c> to keep retrieval shallow (profile + 1-2
    /// nuggets, no deep fact/event/chunk digging).
    /// </summary>
    private async Task<(string PackText, bool OnboardingNeeded)> RetrieveMemoryContextAsync(
        string userMessage, CancellationToken cancellationToken)
    {
        try
        {
            // Cold-greeting detection: first user turn after reset
            // + message looks like a greeting → shallow retrieval mode.
            var isColdGreet = IsColdGreeting(userMessage);
            if (isColdGreet)
                LogEvent("COLD_GREET_DETECTED",
                    "First user message is a greeting — using shallow retrieval.");

            // Include the active profile ID so the MCP server knows
            // who's talking — env vars are static after process start.
            var profileArg = ActiveProfileId ?? "";
            var args = isColdGreet
                ? JsonSerializer.Serialize(new { query = userMessage, mode = "greet", activeProfileId = profileArg })
                : JsonSerializer.Serialize(new { query = userMessage, activeProfileId = profileArg });
            string result;

            try
            {
                result = await _mcp.CallToolAsync(
                    "MemoryRetrieve", args, cancellationToken);
            }
            catch
            {
                // Fallback: some MCP stacks may register snake_case names
                try
                {
                    result = await _mcp.CallToolAsync(
                        "memory_retrieve", args, cancellationToken);
                }
                catch
                {
                    return ("", false);
                }
            }

            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;

            // Onboarding flag: true when no profile exists at all
            var onboarding = root.TryGetProperty("onboardingNeeded", out var ob)
                          && ob.ValueKind == JsonValueKind.True;

            if (root.TryGetProperty("packText", out var packTextEl) &&
                packTextEl.ValueKind == JsonValueKind.String)
            {
                var packText = packTextEl.GetString() ?? "";

                if (!string.IsNullOrWhiteSpace(packText))
                {
                    // Log the retrieval audit event
                    var facts   = root.TryGetProperty("facts", out var f)   ? f.GetInt32() : 0;
                    var events  = root.TryGetProperty("events", out var ev) ? ev.GetInt32() : 0;
                    var chunks  = root.TryGetProperty("chunks", out var ch) ? ch.GetInt32() : 0;
                    var nuggets = root.TryGetProperty("nuggets", out var ng) ? ng.GetInt32() : 0;
                    var hasProf = root.TryGetProperty("hasProfile", out var hp)
                                 && hp.ValueKind == JsonValueKind.True;

                    LogEvent("MEMORY_RETRIEVED",
                        $"Retrieved {facts} facts, {events} events, " +
                        $"{chunks} chunks, {nuggets} nuggets" +
                        $"{(hasProf ? " (profile loaded)" : "")} for this reply.");

                    return (packText, onboarding);
                }
            }

            return ("", onboarding);
        }
        catch
        {
            // Memory retrieval is best-effort — never block the main flow
            return ("", false);
        }
    }

    /// <summary>
    /// Mutates history[0] in-place to append the memory pack text.
    /// Used for the tool loop where the same history list is reused
    /// across multiple LLM round-trips.
    /// </summary>
    private static void InjectMemoryIntoHistoryInPlace(
        List<ChatMessage> history, string memoryPackText)
    {
        if (string.IsNullOrWhiteSpace(memoryPackText))
            return;

        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Role == "system")
            {
                history[i] = ChatMessage.System(
                    (history[i].Content ?? "") + memoryPackText);
                return;
            }
        }
    }

    private static List<ChatMessage> InjectModeIntoSystemPrompt(
        List<ChatMessage> history, string modeSuffix)
    {
        var copy = new List<ChatMessage>(history.Count);
        var injected = false;

        foreach (var msg in history)
        {
            if (!injected && msg.Role == "system")
            {
                copy.Add(ChatMessage.System((msg.Content ?? "") + modeSuffix));
                injected = true;
            }
            else
            {
                copy.Add(msg);
            }
        }

        // If there was no system message (shouldn't happen), prepend one
        if (!injected)
            copy.Insert(0, ChatMessage.System(modeSuffix));

        return copy;
    }

    // ─────────────────────────────────────────────────────────────────
    // Search Query Extraction via Tool Call
    //
    // Rather than asking the LLM to follow a freeform "QUERY | RECENCY"
    // prompt (which small models routinely botch), we give it a single
    // web_search tool definition and let it fill in the structured args.
    // Models are far more reliable at producing constrained tool-call
    // arguments than parsing custom extraction formats.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Static tool definition used solely for search query extraction.
    /// Kept minimal so LM Studio's grammar engine compiles quickly.
    /// </summary>
    private static readonly IReadOnlyList<ToolDefinition> SearchExtractionTools =
    [
        new ToolDefinition
        {
            Function = new FunctionDefinition
            {
                Name = "web_search",
                Description = "Search the web for current information, news, or real-time data.",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["query"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] =
                                "Concise 2-6 keyword search query. " +
                                "Topic keywords ONLY — never include greetings, " +
                                "filler, or the assistant's name."
                        },
                        ["recency"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["enum"] = new[] { "day", "week", "month", "any" },
                            ["description"] =
                                "How recent the results should be. " +
                                "'day' = today/latest/breaking, " +
                                "'week' = this week, " +
                                "'month' = this month, " +
                                "'any' = no time constraint."
                        }
                    },
                    ["required"] = new[] { "query", "recency" }
                }
            }
        }
    ];

    private static string NormalizeQueryText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var input = text.Trim();
        var sb = new StringBuilder(input.Length);
        var lastWasSpace = false;

        foreach (var c in input)
        {
            // Keep letters/digits. Convert most punctuation to spaces so
            // tokens like "thadds!" become "thadds" for filtering.
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasSpace = false;
                continue;
            }

            // Keep a few token-internal characters.
            if (c is '\'' or '-' or '+')
            {
                sb.Append(c);
                lastWasSpace = false;
                continue;
            }

            if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static bool IsBannedSearchToken(string tokenLower)
    {
        if (string.IsNullOrWhiteSpace(tokenLower))
            return true;

        // Assistant name variants (common greetings / nicknames).
        if (tokenLower == "thaddeus" || tokenLower.StartsWith("thadd"))
            return true;

        // Greetings and casual filler.
        return tokenLower is
            "sir" or "hey" or "hi" or "hello" or "yo" or "sup" or "homie" or
            "buddy" or "pal" or
            "heck" or "hell" or
            "good" or "morning" or "afternoon" or "evening" or
            "can" or "could" or "would" or "will" or "you" or "me" or "my" or
            "please" or "plz" or "thanks" or "thank" or "danke" or "dank" or
            "look" or "up" or "search" or "find" or "pull" or "show" or "get" or
            "bring" or "grab" or "fetch" or "tell" or "give" or
            "for" or "to" or "on" or "about" or "into" or "in" or "the" or "a" or "an";
    }

    private static bool LooksLikeIdentityLookup(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        // Chit-chat false positives
        if (lower.Contains("what's up") || lower.Contains("whats up"))
            return false;

        // Meta / assistant / self questions that should NOT trigger web search.
        if (lower.Contains("who are you") || lower.Contains("what are you") ||
            lower.Contains("who am i")   || lower.Contains("what is my name") ||
            lower.Contains("what's my name") || lower.Contains("whats my name") ||
            lower.Contains("what is your name") || lower.Contains("what's your name") ||
            lower.Contains("whats your name"))
            return false;

        // Identity / definition triggers (these usually need external lookup)
        return lower.Contains("who is ") ||
               lower.Contains("who's ") ||
               lower.Contains("whos ")  ||
               lower.Contains("who was ") ||
               lower.Contains("who the heck is") ||
               lower.Contains("who the hell is") ||
               lower.Contains("what is ") ||
               lower.Contains("what's ") ||
               lower.Contains("whats ")  ||
               lower.Contains("define ") ||
               lower.Contains("meaning of ") ||
               lower.Contains("what does ");
    }

    private static string IdentityPrefix(string lower)
    {
        // Default to "who is" unless the user clearly asked "what is".
        if (string.IsNullOrWhiteSpace(lower))
            return "who is";

        return (lower.Contains("what is ") || lower.Contains("what's ") || lower.Contains("whats ") ||
                lower.Contains("define ") || lower.Contains("meaning of ") || lower.Contains("what does "))
            ? "what is"
            : "who is";
    }

    private static bool TryExtractIdentitySubject(string userMessage, out string subject)
    {
        subject = "";
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var trimmed = userMessage.Trim();
        var lower = trimmed.ToLowerInvariant();

        string[] markers =
        [
            "who the heck is ",
            "who the hell is ",
            "who is ",
            "who's ",
            "whos ",
            "who was ",
            "what is ",
            "what's ",
            "whats ",
            "define ",
            "meaning of ",
            "what does "
        ];

        foreach (var marker in markers)
        {
            var idx = lower.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
                continue;

            var start = idx + marker.Length;
            if (start >= trimmed.Length)
                continue;

            subject = trimmed[start..].Trim(
                ' ', '?', '!', '.', ',', ':', ';', '"', '\'', '(', ')', '[', ']', '{', '}');

            if (subject.Length > 0)
                return true;
        }

        return false;
    }

    private static string CleanSearchQuery(string query)
    {
        var normalized = NormalizeQueryText(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(tokens.Length);

        foreach (var token in tokens)
        {
            var lower = token.ToLowerInvariant();
            if (IsBannedSearchToken(lower))
                continue;

            kept.Add(token);
            if (kept.Count >= 6) // enforce the 2–6 keyword guideline
                break;
        }

        return string.Join(' ', kept).Trim();
    }

    private static bool WantsUsRegion(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        // Prefer explicit punctuation/casing so we don't confuse pronoun "us"
        // (e.g. "look this up for us") with the region "US".
        if (userMessage.Contains("U.S.", StringComparison.Ordinal) ||
            userMessage.Contains("U.S", StringComparison.Ordinal))
            return true;

        if (userMessage.Contains(" US ", StringComparison.Ordinal) ||
            userMessage.EndsWith(" US", StringComparison.Ordinal) ||
            userMessage.StartsWith("US ", StringComparison.Ordinal))
            return true;

        var lower = userMessage.ToLowerInvariant();
        return lower.Contains("united states") ||
               lower.Contains("usa") ||
               lower.Contains("u.s") ||
               lower.Contains("u s");
    }

    private static bool IsGenericHeadlineQuery(string queryLower)
    {
        var q = (queryLower ?? "").Trim();
        return q is
            "headline" or "headlines" or
            "news" or "latest news" or "breaking news" or
            "latest headlines" or "breaking headlines" or
            "top headlines";
    }

    /// <summary>
    /// Extracts a search query and recency by asking the LLM to fill in
    /// a <c>web_search</c> tool call. The schema constrains the output,
    /// making this far more reliable than freeform text extraction —
    /// especially with smaller local models that struggle with custom
    /// prompt formats.
    ///
    /// Falls back to deterministic filler stripping if the tool call
    /// fails or the model doesn't cooperate.
    /// </summary>
    private async Task<(string Query, string Recency)> ExtractSearchViaToolCallAsync(
        string userMessage, string memoryPackText, CancellationToken cancellationToken)
    {
        const string defaultRecency = "any";
        var lowerMsg = (userMessage ?? "").ToLowerInvariant();
        var wantsUs = WantsUsRegion(userMessage ?? "");
        var isIdentity = LooksLikeIdentityLookup(lowerMsg);
        var identityPrefix = IdentityPrefix(lowerMsg);

        try
        {
            // Focused instruction: system + search directive + user msg.
            // Memory context is included so the LLM knows who's talking
            // (e.g., "Mark wants news") but we don't need full history.
            var systemContent =
                "You are a search assistant. The user wants to look something up.\n" +
                "Call the web_search tool with an appropriate query and recency.\n\n" +
                "Rules:\n" +
                "- The query must be the TOPIC — 2 to 6 keywords.\n" +
                "- Strip ALL greetings (hey, hi, hello, good morning),\n" +
                "  assistant names (thaddeus, thadds, sir thaddeus),\n" +
                "  and filler (can you, please, pull up, for me, danke, thanks).\n" +
                "- If the user asks for 'news', 'headlines', or 'latest',\n" +
                "  set recency to 'day'.\n" +
                "- If the user requests generic headlines/news with no specific topic,\n" +
                "  use query: \"top headlines\" (or \"U.S. top headlines\" when they mention the US).\n" +
                "- ALWAYS call the tool. Never reply with text.";

            if (!string.IsNullOrWhiteSpace(memoryPackText))
                systemContent += "\n\n" + memoryPackText;

            var cleanedHint = StripConversationalFiller(userMessage ?? "");
            var messages = new List<ChatMessage>
            {
                ChatMessage.System(systemContent),
                ChatMessage.User(
                    "User message:\n" + userMessage + "\n\n" +
                    "Cleaned request (greetings removed):\n" + cleanedHint)
            };

            var response = await _llm.ChatAsync(
                messages, SearchExtractionTools, maxTokensOverride: 80, cancellationToken);

            // ── Parse the tool call arguments ─────────────────────────
            if (response.ToolCalls is { Count: > 0 })
            {
                var args = response.ToolCalls[0].Function.Arguments;
                using var doc = JsonDocument.Parse(args);
                var root = doc.RootElement;

                var query = root.TryGetProperty("query", out var q)
                    ? (q.GetString() ?? "").Trim()
                    : "";
                var recency = root.TryGetProperty("recency", out var r)
                    ? NormalizeRecency(r.GetString() ?? "")
                    : defaultRecency;

                // Clean + sanitize — remove assistant names, greetings, filler.
                // This also normalizes punctuation so "thadds!" is filtered.
                var cleanedQuery = CleanSearchQuery(query);
                var lowerCleanedQuery = cleanedQuery.ToLowerInvariant();

                // Prefer explicit recency hints from the user message.
                var recencyFromUser = DetectRecencyFallback(userMessage ?? "");
                if (recencyFromUser != "any" && recencyFromUser != recency)
                    recency = recencyFromUser;

                // If the query is generic "headlines"/"news", use a stable default.
                if (IsGenericHeadlineQuery(lowerCleanedQuery))
                    cleanedQuery = wantsUs ? "U.S. top headlines" : "top headlines";

                // Identity/definition queries: bias the query toward "who is X"
                // and do not apply a recency window.
                if (isIdentity && !string.IsNullOrWhiteSpace(cleanedQuery))
                {
                    var ql = cleanedQuery.ToLowerInvariant();
                    if (!ql.StartsWith("who is") && !ql.StartsWith("what is") &&
                        !ql.StartsWith("who's")  && !ql.StartsWith("whos") &&
                        !ql.StartsWith("what's") && !ql.StartsWith("whats"))
                    {
                        cleanedQuery = $"{identityPrefix} {cleanedQuery}".Trim();
                    }

                    recency = "any";
                }

                // Still empty? Fall through to deterministic cleanup below.
                if (!string.IsNullOrWhiteSpace(cleanedQuery) &&
                    cleanedQuery.Length >= 2 && cleanedQuery.Length <= 120)
                {
                    LogEvent("AGENT_QUERY_EXTRACT",
                        $"Tool call: query=\"{cleanedQuery}\", recency={recency}");
                    return (cleanedQuery, recency);
                }

                LogEvent("AGENT_QUERY_EXTRACT",
                    $"Tool call returned junk query \"{query}\" — using deterministic cleanup");
            }
            else
            {
                // Model responded with text instead of a tool call.
                // Some small models do this even when told to call a tool.
                LogEvent("AGENT_QUERY_EXTRACT",
                    "LLM did not produce a tool call — using filler strip");
            }
        }
        catch (Exception ex)
        {
            LogEvent("AGENT_QUERY_EXTRACT_FAIL",
                $"Tool-call extraction failed: {ex.Message}");
        }

        // ── Deterministic fallback: strip greetings + filler ──────────
        var fallbackQuery = CleanSearchQuery(StripConversationalFiller(userMessage ?? ""));
        var fallbackRecency = DetectRecencyFallback(userMessage ?? "");

        // Identity/definition fallback: build "who is X" / "what is X"
        // even if the model refused to call the tool.
        if (isIdentity)
        {
            if (TryExtractIdentitySubject(userMessage ?? "", out var subject))
            {
                var cleanSubject = CleanSearchQuery(subject);
                if (!string.IsNullOrWhiteSpace(cleanSubject))
                    fallbackQuery = $"{identityPrefix} {cleanSubject}".Trim();
            }
            else if (!string.IsNullOrWhiteSpace(fallbackQuery))
            {
                fallbackQuery = $"{identityPrefix} {fallbackQuery}".Trim();
            }

            fallbackRecency = "any";
        }

        if (string.IsNullOrWhiteSpace(fallbackQuery))
        {
            // If the user asked for headlines/news but gave no topic,
            // pick a stable, useful default.
            if (lowerMsg.Contains("headline") || lowerMsg.Contains("headlines") ||
                lowerMsg.Contains("news") || lowerMsg.Contains("latest") || lowerMsg.Contains("breaking"))
            {
                fallbackQuery = wantsUs ? "U.S. top headlines" : "top headlines";
            }
        }

        // If the fallback query is generic, normalize it as well.
        if (IsGenericHeadlineQuery(fallbackQuery.ToLowerInvariant()))
            fallbackQuery = wantsUs ? "U.S. top headlines" : "top headlines";

        return (fallbackQuery, fallbackRecency);
    }

    /// <summary>
    /// Quick keyword-based recency detection used when the LLM call
    /// is skipped or fails. Keeps things working even without the model.
    /// </summary>
    private static string DetectRecencyFallback(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("today") || lower.Contains("this morning") ||
            lower.Contains("right now") || lower.Contains("just happened"))
            return "day";
        if (lower.Contains("this week") || lower.Contains("past week") ||
            lower.Contains("last week") || lower.Contains("last few days"))
            return "week";
        if (lower.Contains("this month") || lower.Contains("past month") ||
            lower.Contains("recently"))
            return "month";
        if (lower.Contains("breaking") || lower.Contains("headline") || lower.Contains("headlines") ||
            lower.Contains("top stories") || lower.Contains("news") ||
            (lower.Contains("latest") &&
             (lower.Contains("news") || lower.Contains("headline") || lower.Contains("headlines") ||
              lower.Contains("update") || lower.Contains("updates") || lower.Contains("happening"))))
            return "day";
        return "any";
    }

    /// <summary>
    /// Strips conversational filler from a user message to produce a
    /// cleaner search query when the LLM extraction fails. Removes
    /// leading phrases like "can you check", "I want to look up", etc.
    /// and trailing noise like "for me", "please".
    ///
    /// Example: "Can you check up news on the US stock market this week?
    /// its been a crazy week, what happened?"
    ///   → "news on the US stock market this week"
    /// </summary>
    private static string StripConversationalFiller(string input)
    {
        var text = input.Trim(' ', '?', '!', '.', ',');
        var lower = text.ToLowerInvariant();

        // ── Strip leading greetings / salutations ─────────────────────
        // Users often open with "hey sir thaddeus!" or "hello!" before
        // stating their actual request. Peel those off first.
        string[] greetingPrefixes =
        [
            "hey sir thaddeus",   "hi sir thaddeus",
            "hello sir thaddeus", "yo sir thaddeus",
            "hey thaddeus",       "hi thaddeus",
            "hello thaddeus",     "yo thaddeus",
            "good morning",       "good afternoon",
            "good evening",       "hey there",
            "hi there",           "hello there",
            "hey",                "hi",
            "hello",              "yo",
        ];

        foreach (var greet in greetingPrefixes)
        {
            if (lower.StartsWith(greet))
            {
                text  = text[greet.Length..].TrimStart(' ', ',', '!', '.', '-');
                lower = text.ToLowerInvariant();
                break;
            }
        }

        // ── Strip assistant name prefix variants ──────────────────────
        // After removing "hey/hi/hello", users often have a name token
        // next ("thadds!") which is pure salutation, not search topic.
        string[] assistantNamePrefixes =
        [
            "sir thaddeus",
            "thaddeus",
            "thadds",
            "thaddy",
            "thadd"
        ];

        foreach (var name in assistantNamePrefixes)
        {
            if (lower.StartsWith(name))
            {
                text  = text[name.Length..].TrimStart(' ', ',', '!', '.', '?', '-', ':');
                lower = text.ToLowerInvariant();
                break;
            }
        }

        // ── Strip "how are you" / chit-chat follow-ups ────────────────
        string[] chitChat =
        [
            "how the heck are you today",
            "how the heck are you",
            "how are you doing today",
            "how are you doing",
            "how are you today",
            "how are you",
            "how's it going",
            "hows it going",
            "what's up",
            "whats up",
        ];

        foreach (var cc in chitChat)
        {
            if (lower.StartsWith(cc))
            {
                text  = text[cc.Length..].TrimStart(' ', ',', '!', '.', '?', '-');
                lower = text.ToLowerInvariant();
                break;
            }
        }

        // ── Leading filler phrases (order matters: longest first) ────
        string[] leadPhrases =
        [
            "actually, can you check up",
            "actually can you check up",
            "actually, can you check",
            "actually can you check",
            "can you check up the news on",
            "can you check up news on",
            "can you check up on",
            "can you check the news today",
            "can you look up the news today",
            "can you search for",   "can you search up",
            "can you look up",      "can you look into",
            "can you find out",     "can you find me",
            "can you pull up",      "can you check on",
            "can you check up",     "can you check",
            "can you get me",
            "could you search for", "could you look up",
            "could you find",       "could you check",
            "please search for",    "please look up",
            "please find",          "please check",
            "i want to look up information on how",
            "i want to look up information on",
            "i want to look up information about",
            "i want to look up info on",
            "i want to look up",    "i want to search for",
            "i want to find out about",
            "i want to find out",   "i want to find",
            "i want to know about", "i want to know",
            "i want to see whats happening with",
            "i want to see what's happening with",
            "i want to see",
            "i need to look up",    "i need to find",
            "i'd like to know about", "i'd like to know",
            "tell me about",        "tell me what",
            "show me",              "get me",
            "look up",              "search for",
            "search up",            "pull up the news on",
            "pull up the news about",
            "pull up news on",      "pull up news about",
            "pull up the news",     "pull up news",
            "pull up",              "find out about",
            "find out",             "check on",
            "check the",            "what's going on with",
            "whats going on with",  "what is going on with",
            "what happened with",   "what happened to",
            "what's happening with", "whats happening with",
            "how has",              "how is",
        ];

        foreach (var phrase in leadPhrases)
        {
            if (lower.StartsWith(phrase))
            {
                text  = text[phrase.Length..].TrimStart(' ', ',', ':', '-');
                lower = text.ToLowerInvariant();
                break; // Only strip one leading phrase
            }
        }

        // ── Trailing filler ─────────────────────────────────────────
        string[] trailPhrases =
        [
            "for me please", "for me", "please", "right now",
            "if you can", "if possible", "when you get a chance"
        ];

        foreach (var phrase in trailPhrases)
        {
            if (lower.EndsWith(phrase))
            {
                text  = text[..^phrase.Length].TrimEnd(' ', ',', '.');
                lower = text.ToLowerInvariant();
                break;
            }
        }

        // ── Sentence splitting: if multiple sentences remain,
        // keep the one with the most topic signal (proper nouns,
        // domain-specific words) rather than emotional commentary ─────
        var sentences = text.Split(['.', '?', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 2)
            .ToArray();

        if (sentences.Length > 1)
        {
            // Words that are pure emotional/conversational filler —
            // they tell us nothing about what to search for.
            string[] fillerWords = ["i", "you", "can", "could", "want",
                "to", "the", "a", "an", "do", "does", "is", "are",
                "please", "check", "look", "find", "search", "up",
                "me", "my", "on", "about", "information", "info",
                "it", "its", "it's", "been", "what", "how", "so",
                "just", "really", "actually", "basically", "totally"];

            // Words that signal "this sentence has a real topic."
            // Sentences with uppercase words (proper nouns) or domain
            // keywords score higher.
            string[] topicSignals = ["news", "market", "stock", "crypto",
                "price", "weather", "score", "game", "election",
                "update", "latest", "recent", "happening",
                "headlines", "breaking", "sports", "politics",
                "tech", "technology", "science", "war", "economy",
                "finance", "results", "recap", "forecast"];

            var best = sentences
                .OrderByDescending(s =>
                {
                    var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var lowerWords = words.Select(w => w.ToLowerInvariant()).ToArray();

                    // Count uppercase-start words (likely proper nouns)
                    var properNouns = words.Count(w =>
                        w.Length > 1 && char.IsUpper(w[0]));

                    // Count topic signal words
                    var topicHits = lowerWords.Count(w =>
                        topicSignals.Contains(w));

                    // Penalize high filler ratio
                    var fillerRatio = words.Length > 0
                        ? (double)lowerWords.Count(w => fillerWords.Contains(w)) / words.Length
                        : 1.0;

                    // Score: more proper nouns & topic words = better,
                    // high filler ratio = worse
                    return (properNouns * 3) + (topicHits * 2) - (fillerRatio * 5);
                })
                .ThenByDescending(s => s.Length)
                .First();

            text = best;
        }

        // Final trim
        text = text.Trim(' ', '?', '!', '.', ',');

        // If we stripped everything, fall back to the original
        return text.Length >= 3 ? text : input.Trim(' ', '?', '!', '.', ',');
    }

    /// <summary>
    /// Normalizes an LLM-returned recency string to a known value.
    /// </summary>
    private static string NormalizeRecency(string raw)
    {
        var r = (raw ?? "any").Trim().ToLowerInvariant();
        return r switch
        {
            "day" or "today" or "24h"   => "day",
            "week" or "7d"              => "week",
            "month" or "30d"            => "month",
            _                           => "any"
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Intent Router
    //
    // Produces a RouterOutput for the policy gate. Uses the same
    // hybrid approach as before (fast-path → heuristics → LLM → fallback)
    // but now outputs structured intent + capability flags instead of
    // a coarse enum.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Routes the user message to a structured <see cref="RouterOutput"/>.
    /// This is the entry point for the new pipeline. Internally uses
    /// <see cref="ClassifyIntentAsync"/> then refines the Tooling
    /// intent into sub-intents via heuristics.
    /// </summary>
    private async Task<RouterOutput> RouteMessageAsync(
        string userMessage, CancellationToken cancellationToken)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();

        // ── Fast-path overrides ──────────────────────────────────────
        if (lower.StartsWith("/browse ") || lower.StartsWith("browse:"))
            return MakeRoute(Intents.BrowseOnce, confidence: 1.0,
                needsWeb: true, needsBrowser: true);

        // Screen requests are unambiguously tool-driven, but smaller local
        // models sometimes misclassify them as "chat". Route these
        // deterministically so the policy gate can expose screen tools.
        if (LooksLikeScreenRequest(lower))
            return MakeRoute(Intents.ScreenObserve, confidence: 0.95,
                needsScreen: true);

        // ── Classify via existing LLM + heuristic pipeline ───────────
        var intent = await ClassifyIntentAsync(userMessage ?? string.Empty, cancellationToken);

        // ── Map coarse intent to fine-grained RouterOutput ───────────
        return intent switch
        {
            ChatIntent.Casual    => MakeRoute(Intents.ChatOnly, confidence: 0.8),
            ChatIntent.WebLookup => MakeRoute(Intents.LookupSearch, confidence: 0.9,
                needsWeb: true, needsSearch: true),
            ChatIntent.Tooling   => RefineToolingIntent(lower),
            _                    => MakeRoute(Intents.GeneralTool, confidence: 0.3)
        };
    }

    /// <summary>
    /// Breaks the coarse "Tooling" classification into specific
    /// sub-intents based on keyword heuristics. If no specific
    /// sub-intent matches, falls back to GeneralTool (all tools).
    /// </summary>
    private static RouterOutput RefineToolingIntent(string lower)
    {
        // Order matters — check the most specific patterns first

        if (LooksLikeMemoryWriteRequest(lower))
            return MakeRoute(Intents.MemoryWrite, confidence: 0.9,
                needsMemoryWrite: true);

        if (LooksLikeScreenRequest(lower))
            return MakeRoute(Intents.ScreenObserve, confidence: 0.85,
                needsScreen: true);

        if (LooksLikeFileRequest(lower))
            return MakeRoute(Intents.FileTask, confidence: 0.85,
                needsFile: true);

        if (LooksLikeSystemCommand(lower))
            return MakeRoute(Intents.SystemTask, confidence: 0.8,
                needsSystem: true, risk: "medium");

        if (LooksLikeBrowseRequest(lower))
            return MakeRoute(Intents.BrowseOnce, confidence: 0.85,
                needsWeb: true, needsBrowser: true);

        // Can't narrow it down — give all tools
        return MakeRoute(Intents.GeneralTool, confidence: 0.4);
    }

    /// <summary>
    /// Maps a <see cref="RouterOutput"/> back to the legacy
    /// <see cref="ChatIntent"/> enum for code that still uses it
    /// (WebLookup deterministic path).
    /// </summary>
    private static ChatIntent MapRouteToLegacyIntent(RouterOutput route)
    {
        return route.Intent switch
        {
            Intents.ChatOnly      => ChatIntent.Casual,
            Intents.MemoryRead    => ChatIntent.Casual,
            Intents.LookupSearch  => ChatIntent.WebLookup,
            _                     => ChatIntent.Tooling
        };
    }

    // ── RouterOutput factory ─────────────────────────────────────────

    private static RouterOutput MakeRoute(
        string intent,
        double confidence = 0.5,
        bool needsWeb = false,
        bool needsBrowser = false,
        bool needsSearch = false,
        bool needsMemoryRead = false,
        bool needsMemoryWrite = false,
        bool needsFile = false,
        bool needsScreen = false,
        bool needsSystem = false,
        string risk = "low")
    {
        return new RouterOutput
        {
            Intent                 = intent,
            Confidence             = confidence,
            NeedsWeb               = needsWeb,
            NeedsBrowserAutomation = needsBrowser,
            NeedsSearch            = needsSearch,
            NeedsMemoryRead        = needsMemoryRead,
            NeedsMemoryWrite       = needsMemoryWrite,
            NeedsFileAccess        = needsFile,
            NeedsScreenRead        = needsScreen,
            NeedsSystemExecute     = needsSystem,
            RiskLevel              = risk
        };
    }

    // ── Sub-intent heuristics ────────────────────────────────────────

    private static bool LooksLikeScreenRequest(string lower)
    {
        ReadOnlySpan<string> patterns =
        [
            "what's on my screen",   "whats on my screen",
            "what can you see",      "what do you see",
            "look at my screen",     "look at the screen",
            "take a screenshot",     "screenshot",
            "capture the screen",    "capture my screen",
            "screen capture",        "what's happening on screen",
            "show me my screen",     "read my screen",
            "what's on the screen",  "whats on the screen",
            "active window",
            // Users referring to their IDE / editor by name
            "look at my cursor",     "look at cursor",
            "what's in my editor",   "whats in my editor",
            "look at my editor",     "look at my ide",
            "look at my code",       "look at this code",
            "see my code",           "see what i'm working on",
            "see what im working on"
        ];

        foreach (var p in patterns)
            if (lower.Contains(p)) return true;

        return false;
    }

    private static bool LooksLikeFileRequest(string lower)
    {
        ReadOnlySpan<string> patterns =
        [
            "read the file",   "read this file",    "read file",
            "open the file",   "open this file",    "open file",
            "list files",      "list the files",    "show files",
            "what's in the file", "whats in the file",
            "file contents",   "show me the file",
            "directory listing", "folder contents",
            "list directory",  "ls "
        ];

        foreach (var p in patterns)
            if (lower.Contains(p)) return true;

        return false;
    }

    private static bool LooksLikeSystemCommand(string lower)
    {
        ReadOnlySpan<string> patterns =
        [
            "run the command",     "run this command",
            "run command",         "execute command",
            "execute the command", "execute this",
            "open this program",   "launch ",
            "run this program",    "start the ",
            "system command",      "shell command",
            "terminal command"
        ];

        foreach (var p in patterns)
            if (lower.Contains(p)) return true;

        return false;
    }

    private static bool LooksLikeBrowseRequest(string lower)
    {
        ReadOnlySpan<string> patterns =
        [
            "go to this url",      "go to this website",
            "go to this page",     "go to this site",
            "navigate to",         "open this url",
            "open this website",   "open this page",
            "open this link",      "visit this",
            "browse to",           "fetch this url",
            "fetch this page"
        ];

        foreach (var p in patterns)
            if (lower.Contains(p)) return true;

        // URL-like patterns (starts with http or contains .com/.org etc.)
        if (lower.Contains("http://") || lower.Contains("https://"))
            return true;

        return false;
    }

    /// <summary>
    /// Detects a "cold greeting" — the very first user message after a
    /// conversation reset, and it looks like a simple hello/hi/hey.
    /// When true, memory retrieval uses <c>mode = "greet"</c> for
    /// shallow context (profile + 1-2 nuggets, no deep digging).
    /// </summary>
    private bool IsColdGreeting(string userMessage)
    {
        // Cold-start: history should contain only the system prompt +
        // the current user message (which hasn't been added yet at this
        // point, or has just been added).  Accept 1 (system only) or
        // 2 (system + this user message) entries.
        var userTurns = _history.Count(m => m.Role == "user");
        if (userTurns > 1)
            return false;

        return LooksLikeGreeting(userMessage.ToLowerInvariant().Trim());
    }

    private static bool LooksLikeGreeting(string lower)
    {
        // Short greetings — whole-message match for very short strings
        // to avoid false positives ("hey, can you search for…").
        ReadOnlySpan<string> exact =
        [
            "hi",  "hey", "hello", "yo", "sup",
            "hi!", "hey!", "hello!", "yo!", "sup!",
            "good morning", "good afternoon", "good evening",
            "gm", "morning", "howdy", "hiya", "greetings",
            "what's up", "whats up", "what's good", "whats good"
        ];

        foreach (var g in exact)
            if (lower == g) return true;

        // Slightly longer — must START with a greeting word and be
        // under 40 chars to qualify (rules out "hey, can you do X").
        if (lower.Length > 40)
            return false;

        ReadOnlySpan<string> prefixes =
        [
            "hi ", "hey ", "hello ", "yo ", "sup ",
            "good morning", "good afternoon", "good evening",
            "howdy ", "hiya ", "greetings"
        ];

        foreach (var p in prefixes)
            if (lower.StartsWith(p)) return true;

        return false;
    }

    /// <summary>
    /// Uses the LLM to classify the user's intent. One fast call with a
    /// tight token cap. Explicit prefixes (/search, /chat) skip the LLM
    /// entirely for power users and testing.
    ///
    /// The LLM sees only the user message and a short classification
    /// prompt — no tools, no history, no persona. This keeps it fast
    /// and prevents the model from trying to "answer" the question
    /// during classification.
    /// </summary>
    private async Task<ChatIntent> ClassifyIntentAsync(
        string userMessage, CancellationToken cancellationToken)
    {
        var msg = (userMessage ?? "").Trim();
        if (msg.Length == 0)
            return ChatIntent.Casual;

        var lower = msg.ToLowerInvariant();

        // ── Fast-path: explicit overrides (no LLM needed) ────────────
        if (lower.StartsWith("/search ") || lower.StartsWith("search:"))
            return ChatIntent.WebLookup;

        if (lower.StartsWith("/chat ") || lower.StartsWith("chat:"))
            return ChatIntent.Casual;

        // ── Memory-write detection ────────────────────────────────────
        // "Remember that ...", "note that ...", "save this ..." must
        // route to Tooling so the LLM can call MemoryStoreFacts.
        // Without this, the classifier says "chat" and the model
        // hallucinates that it saved something.
        if (LooksLikeMemoryWriteRequest(lower))
            return ChatIntent.Tooling;

        // ── Web-search detection ─────────────────────────────────────
        // Catches obvious search-intent phrasing that small models
        // sometimes misclassify as "chat", causing them to hallucinate
        // raw function-call tokens instead of going through the
        // deterministic web_search pipeline.
        if (LooksLikeWebSearchRequest(lower))
            return ChatIntent.WebLookup;

        // ── LLM classification ───────────────────────────────────────
        const int classifyMaxTokens = 10;

        try
        {
            var messages = new List<ChatMessage>
            {
                ChatMessage.System(
                    "Classify the user message into exactly ONE category. " +
                    "Reply with a single word — nothing else.\n\n" +
                    "chat   = greetings, small talk, opinions, casual conversation\n" +
                    "search = needs current info, news, prices, weather, facts, events, looking something up\n" +
                    "tool   = wants you to interact with their computer (screenshot, read file, run command, " +
                    "remember/save/store/update/correct information, changed their mind about something, take a note)\n\n" +
                    "Reply with: chat, search, or tool"),
                ChatMessage.User(msg)
            };

            var response = await _llm.ChatAsync(
                messages, tools: null, classifyMaxTokens, cancellationToken);

            var raw = (response.Content ?? "").Trim().ToLowerInvariant();

            // Parse — accept the first recognizable token
            if (raw.Contains("search"))
                return ChatIntent.WebLookup;
            if (raw.Contains("tool"))
                return ChatIntent.Tooling;
            if (raw.Contains("chat"))
                return ChatIntent.Casual;

            // Model returned something unexpected — use heuristics as
            // a second opinion before defaulting to Casual. Defaulting
            // to Casual for a search-intent message causes the model to
            // hallucinate raw function-call tokens.
            var fallback = InferFallbackIntent(lower);
            LogEvent("AGENT_CLASSIFY_UNCLEAR",
                $"LLM returned \"{raw}\" — falling back to {fallback}");
            return fallback;
        }
        catch (Exception ex)
        {
            // Classification call failed — same heuristic fallback.
            var fallback = InferFallbackIntent(lower);
            LogEvent("AGENT_CLASSIFY_FAIL",
                $"Intent classification failed: {ex.Message} — falling back to {fallback}");
            return fallback;
        }
    }

    /// <summary>
    /// When the LLM classifier fails or returns garbage, use the keyword
    /// heuristics as a fallback instead of blindly defaulting to Casual.
    /// This prevents search-intent messages from being routed to the
    /// casual path where the model hallucinates template tokens.
    /// </summary>
    private static ChatIntent InferFallbackIntent(string lower)
    {
        if (LooksLikeWebSearchRequest(lower))
            return ChatIntent.WebLookup;
        if (LooksLikeMemoryWriteRequest(lower))
            return ChatIntent.Tooling;
        return ChatIntent.Casual;
    }

    /// <summary>
    /// Detects and truncates self-dialogue — where the model generates
    /// fake user messages and then answers them, role-playing both sides
    /// of a conversation. Common with small local models that don't
    /// reliably stop at the end of their turn.
    ///
    /// Heuristic: scan paragraph boundaries. If a paragraph looks like
    /// something a user would say TO an assistant (short question,
    /// request, first-person statement introducing a new topic), cut
    /// everything from that point onward.
    /// </summary>
    private static string TruncateSelfDialogue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Split on double-newline (paragraph boundaries)
        var paragraphs = text.Split(
            ["\n\n", "\r\n\r\n"], StringSplitOptions.None);

        if (paragraphs.Length <= 2)
            return text; // Too short to be self-dialogue

        // The first paragraph is (almost always) the real response.
        // Scan from paragraph index 1 onward for leaked instructions
        // or fake user turns. Instruction leaks can appear as early as
        // paragraph 2, so we start checking there.
        var keepCount = paragraphs.Length;
        for (var i = 1; i < paragraphs.Length; i++)
        {
            var trimmed = paragraphs[i].Trim();
            if (LooksLikeInstructionLeak(trimmed) ||
                LooksLikePhantomToolRun(trimmed) ||
                LooksLikeUserTurn(trimmed))
            {
                keepCount = i;
                break;
            }
        }

        if (keepCount == paragraphs.Length)
            return text; // Nothing suspicious found

        return string.Join("\n\n", paragraphs.Take(keepCount)).Trim();
    }

    /// <summary>
    /// Detects "phantom tool usage" where the model prints lines like
    /// "Run: weather" (or similar) even though the runtime didn't call a tool.
    /// This is distinct from user instructions like "Run: dotnet build"
    /// (which contain whitespace after Run:).
    /// </summary>
    private static bool LooksLikePhantomToolRun(string paragraph)
    {
        if (string.IsNullOrWhiteSpace(paragraph))
            return false;

        var lines = paragraph.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            // Example hallucination:
            //   Run: weather
            //   Tool: web_search
            if (line.StartsWith("run:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("tool:", StringComparison.OrdinalIgnoreCase))
            {
                var idx = line.IndexOf(':');
                var target = idx >= 0 ? line[(idx + 1)..].Trim() : "";
                if (target.Length == 0)
                    continue;

                // If there's whitespace, it's likely a user command ("dotnet build"),
                // not a tool identifier.
                if (target.Any(char.IsWhiteSpace))
                    continue;

                // Don't treat common CLI commands as "phantom tools"
                if (IsCommonCliCommand(target))
                    continue;

                // Short single-token target is highly likely to be a fake tool call.
                if (target.Length <= 24)
                    return true;
            }

            // Other common telltales
            if (line.StartsWith("calling tool", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("executing tool", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("tool call", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsCommonCliCommand(string token)
    {
        // Keep this short and pragmatic — just enough to avoid false positives.
        // (We only use this to decide whether "Run: <token>" looks like a tool.)
        return token.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("git", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("pnpm", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("yarn", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("node", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("python", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("py", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("pip", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("msbuild", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("curl", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("wget", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("ping", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("ipconfig", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("whoami", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if the paragraph looks like leaked system prompt
    /// instructions or internal reasoning rather than actual dialogue.
    /// Small models are prone to regurgitating their instructions verbatim.
    /// </summary>
    private static bool LooksLikeInstructionLeak(string paragraph)
    {
        if (string.IsNullOrWhiteSpace(paragraph))
            return false;

        var lower = paragraph.ToLowerInvariant();

        // Meta-instructions about how to behave
        var instructionPatterns = new[]
        {
            "if the user asks",   "if the user wants",
            "when the user",      "always respond with",
            "make sure to",       "remember to",
            "you should always",  "your goal is to",
            "your task is to",    "your role is to",
            "as an ai",           "as a language model",
            "as an assistant",    "the assistant should",
            "the model should",   "respond with a",
            "show presence and",  "maintain a",
            "here are some tips", "here is how"
        };

        return instructionPatterns.Any(p => lower.Contains(p));
    }

    /// <summary>
    /// Returns true if the text resembles a user speaking to the assistant
    /// rather than the assistant's own response.
    /// </summary>
    private static bool LooksLikeUserTurn(string paragraph)
    {
        if (string.IsNullOrWhiteSpace(paragraph) || paragraph.Length > 300)
            return false;

        var lower = paragraph.ToLowerInvariant();

        // First-person requests / questions directed at the assistant
        var userPatterns = new[]
        {
            "can you ",   "could you ",  "would you ",
            "i need ",    "i want ",     "i'm trying ",  "i've been ",
            "i have a ",  "i'd like ",
            "any tips",   "any advice",  "any recommendations",
            "help me ",   "show me ",    "tell me ",
            "check my ",  "pull up ",    "look up ",
            "let's ",     "what about "
        };

        if (userPatterns.Any(p => lower.Contains(p)))
            return true;

        // Short interrogative paragraphs in later turns are often the model
        // role-playing the user (e.g., "What's the temperature outside?").
        // We require an interrogative starter to avoid clipping friendly
        // assistant questions like "You doing alright?".
        var trimmed = paragraph.Trim();
        if (trimmed.EndsWith('?') && trimmed.Length <= 140)
        {
            if (lower.StartsWith("what") ||
                lower.StartsWith("why") ||
                lower.StartsWith("how") ||
                lower.StartsWith("who") ||
                lower.StartsWith("where") ||
                lower.StartsWith("when"))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Detects messages where the user is asking the assistant to store,
    /// update, or correct a memory. Covers three categories:
    ///   1. Direct storage: "remember that ...", "note that ..."
    ///   2. Corrections:    "I changed my mind ...", "actually I ..."
    ///   3. Revocations:    "I no longer ...", "forget that ..."
    ///
    /// These must route to Tooling so the memory tools are available —
    /// otherwise the LLM hallucinates a save or template-dumps.
    /// </summary>
    private static bool LooksLikeMemoryWriteRequest(string lower)
    {
        // ── Direct storage phrases ───────────────────────────────────
        ReadOnlySpan<string> storagePhrases =
        [
            "remember that", "remember this", "remember i",
            "remember my", "remember me",
            "please remember", "can you remember",
            "note that", "note this", "make a note",
            "save that", "save this",
            "don't forget", "do not forget",
            "keep in mind", "store that", "store this"
        ];

        foreach (var phrase in storagePhrases)
        {
            if (lower.Contains(phrase))
                return true;
        }

        // ── Correction / mind-change phrases ─────────────────────────
        // "I changed my mind", "actually I like ...", "correction: ..."
        ReadOnlySpan<string> correctionPhrases =
        [
            "changed my mind",  "change my mind",
            "i actually",       "actually i",     "actually, i",
            "i decided",        "i've decided",
            "correction:",      "correct that",
            "update my",        "update that",
            "no wait",          "on second thought",
            "scratch that",     "take that back",
            "i was wrong",      "i meant"
        ];

        foreach (var phrase in correctionPhrases)
        {
            if (lower.Contains(phrase))
                return true;
        }

        // ── Revocation phrases ───────────────────────────────────────
        // "I no longer like ...", "forget that I ...", "remove the fact"
        ReadOnlySpan<string> revocationPhrases =
        [
            "i no longer",      "i don't like",    "i don't want",
            "i dont like",      "i dont want",
            "forget that",      "forget i",
            "remove that",      "delete that",
            "i stopped",        "i quit"
        ];

        foreach (var phrase in revocationPhrases)
        {
            if (lower.Contains(phrase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Detects messages that clearly need a web search: news requests,
    /// price checks, "look up", "search for", current-event questions.
    /// Acts as a safety net when the LLM classifier fumbles — small
    /// models sometimes say "chat" for these and then hallucinate raw
    /// function-call tokens instead of answering.
    /// </summary>
    /// <summary>
    /// Detects messages that clearly need a web search. Uses a layered
    /// approach: strong phrase matches first, then topic + context combos.
    ///
    /// Design principle: if someone mentions a real-time topic (news,
    /// prices, weather, crypto) AND is asking/requesting in any way,
    /// it's a search. We intentionally cast a wide net here — a false
    /// positive just means we do a web search that wasn't needed
    /// (harmless), while a false negative means the model hallucinates
    /// raw template tokens (harmful).
    /// </summary>
    private static bool LooksLikeWebSearchRequest(string lower)
    {
        // Identity/definition lookups ("who is X", "what is Y") are
        // almost always external lookups.
        if (LooksLikeIdentityLookup(lower))
            return true;

        // ── Strong phrase patterns ───────────────────────────────────
        // If any of these appear anywhere in the message, it's search.
        ReadOnlySpan<string> phrases =
        [
            "search for",   "search up",    "look up",     "look into",
            "google ",      "find me ",     "find out ",
            "news on ",     "news about ",  "news for ",
            "price of ",    "price for ",
            "updates on ",  "update on ",   "updates about ",
            "what's the price", "whats the price",
            "how much is",  "how much does"
        ];

        foreach (var phrase in phrases)
        {
            if (lower.Contains(phrase))
                return true;
        }

        // ── Topic keywords — real-time subjects that almost always
        // need external data ──────────────────────────────────────────
        bool hasTopic =
            lower.Contains("news")     || lower.Contains("headline")  ||
            lower.Contains("price")    || lower.Contains("stock")     ||
            lower.Contains("market")   || lower.Contains("weather")   ||
            lower.Contains("forecast") || lower.Contains("score")     ||
            lower.Contains("crypto")   || lower.Contains("bitcoin")   ||
            lower.Contains("dogecoin") || lower.Contains("ethereum")  ||
            lower.Contains("solana")   || lower.Contains("forex");

        if (!hasTopic)
            return false;

        // ── If we have a topic keyword, the threshold is low. ────────
        // Any of these contextual signals qualifies:

        // Question mark — user is asking about the topic
        if (lower.Contains('?'))
            return true;

        // Request framing — "can you", "could you", "would you", etc.
        if (lower.Contains("can you")   || lower.Contains("could you") ||
            lower.Contains("would you") || lower.Contains("will you")  ||
            lower.Contains("please"))
            return true;

        // Action verbs (broad — if it's paired with a topic, it's search)
        if (lower.Contains("pull")   || lower.Contains("look")   ||
            lower.Contains("check")  || lower.Contains("find")   ||
            lower.Contains("show")   || lower.Contains("get")    ||
            lower.Contains("bring")  || lower.Contains("grab")   ||
            lower.Contains("fetch")  || lower.Contains("tell")   ||
            lower.Contains("give")   || lower.Contains("update"))
            return true;

        // Question words
        if (lower.Contains("what")  || lower.Contains("how")    ||
            lower.Contains("where") || lower.Contains("when")   ||
            lower.Contains("who")   || lower.Contains("why"))
            return true;

        // Temporal markers
        if (lower.Contains("today")     || lower.Contains("tonight")    ||
            lower.Contains("yesterday") || lower.Contains("last week")  ||
            lower.Contains("this week") || lower.Contains("past week")  ||
            lower.Contains("last month") || lower.Contains("this month") ||
            lower.Contains("right now") || lower.Contains("currently")  ||
            lower.Contains("latest")    || lower.Contains("recent")     ||
            lower.Contains("lately"))
            return true;

        return false;
    }

    private static bool LooksLikeRawDump(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // If it contains URLs, it's almost always a dump (the UI has cards).
        if (text.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("www.", StringComparison.OrdinalIgnoreCase))
            return true;

        // Table-like or source-list formatting.
        var lines = text.Split('\n');
        var veryShortLines = lines.Count(l => l.Trim().Length is > 0 and < 12);
        if (veryShortLines >= 8)
            return true;

        if (text.Contains("search results", StringComparison.OrdinalIgnoreCase) &&
            lines.Length > 20)
            return true;

        return false;
    }

    /// <summary>
    /// Strips raw chat-template tokens that small models sometimes emit
    /// when they try to make function calls outside the API's tool-call
    /// mechanism. These appear as literal text like:
    ///   &lt;|start|&gt;assistant&lt;|channel|&gt;commentary to=functions.fetch_url...
    ///
    /// If the entire response is template garbage, returns a graceful
    /// fallback message. If only part is contaminated, strips the
    /// contaminated portion and keeps the rest.
    /// </summary>
    private static string StripRawTemplateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Detect template token patterns from common model formats
        // (Mistral, Llama, ChatML, etc.)
        bool hasTemplateTokens =
            text.Contains("<|start|>")   || text.Contains("<|end|>")    ||
            text.Contains("<|channel|>") || text.Contains("<|message|>") ||
            text.Contains("<|im_start|>") || text.Contains("<|im_end|>") ||
            text.Contains("to=functions.") || text.Contains("[INST]")   ||
            text.Contains("[/INST]")      || text.Contains("<s>");

        if (!hasTemplateTokens)
            return text;

        // Try to salvage any human-readable content before the template
        // tokens. Split on the first template marker and keep the prefix.
        string[] markers =
        [
            "<|start|>", "<|channel|>", "<|message|>", "<|im_start|>",
            "<|im_end|>", "<|end|>", "to=functions.", "[INST]", "[/INST]", "<s>"
        ];

        var cleanText = text;
        foreach (var marker in markers)
        {
            var idx = cleanText.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
                cleanText = cleanText[..idx];
        }

        cleanText = cleanText.Trim();

        // If nothing useful survives the strip, return empty string
        // so callers can detect the failure and try an alternate path
        // (e.g., falling back to web search for a follow-up question).
        return cleanText.Length >= 10 ? cleanText : "";
    }
}
