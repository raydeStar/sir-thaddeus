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

    // ── History sliding window ───────────────────────────────────────
    // Keep the last N user+assistant turns so the context window stays
    // within a small model's effective range. The system prompt is
    // always retained as message[0].
    private const int MaxHistoryTurns = 12;

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
        var intent = await ClassifyIntentAsync(userMessage, cancellationToken);
        LogEvent("AGENT_INTENT", intent.ToString());

        // ── Retrieve memory context (best-effort, hard timeout) ──
        // Called after classification but before the main LLM call.
        // The pack text is injected into the system prompt so the
        // model has relevant facts/events/chunks for its answer.
        // If retrieval takes longer than the timeout, we skip it
        // entirely rather than stalling the user's conversation.
        var memoryPackText = "";
        try
        {
            var memoryTask = RetrieveMemoryContextAsync(userMessage, cancellationToken);
            if (await Task.WhenAny(memoryTask, Task.Delay(MemoryRetrievalTimeout, cancellationToken)) == memoryTask)
            {
                memoryPackText = await memoryTask;

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

        try
        {
            // ── Casual chat: no tools ────────────────────────────────
            // The base system prompt already has the full persona. We
            // just send _history as-is (System + User) with no tool
            // definitions, keeping the message structure as simple as
            // possible to avoid LM Studio grammar/template issues.
            if (intent == ChatIntent.Casual)
            {
                cancellationToken.ThrowIfCancellationRequested();
                roundTrips++;

                var messages = InjectMemoryIntoHistory(_history, memoryPackText);
                var response = await CallLlmWithRetrySafe(
                    messages, roundTrips, MaxTokensCasual, cancellationToken);

                var text = TruncateSelfDialogue(response.Content ?? "[No response]");
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

            // ── Web lookup: run web_search deterministically ──────────
            if (intent == ChatIntent.WebLookup)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (searchQuery, recency) = await ExtractSearchQueryAsync(userMessage, cancellationToken);
                LogEvent("AGENT_SEARCH_QUERY",
                    $"\"{userMessage}\" → \"{searchQuery}\" (recency={recency})");

                var webSearchArgs = JsonSerializer.Serialize(new
                {
                    query      = searchQuery,
                    maxResults = DefaultWebSearchMaxResults,
                    recency
                });

                var toolName = "web_search";
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
                        toolName = "WebSearch";
                        LogEvent("AGENT_TOOL_CALL", $"{toolName}({webSearchArgs})");
                        toolResult = await _mcp.CallToolAsync(toolName, webSearchArgs, cancellationToken);
                        toolOk = true;
                    }
                    catch
                    {
                        toolResult = $"Tool error: {ex.Message}";
                        toolOk = false;
                    }
                }

                toolCallsMade.Add(new ToolCallRecord
                {
                    ToolName = toolName,
                    Arguments = webSearchArgs,
                    Result = toolResult,
                    Success = toolOk
                });
                LogEvent("AGENT_TOOL_RESULT", $"{toolName} -> {(toolOk ? "ok" : "error")}");

                // Summarize: feed tool output as a User message (not a ToolResult).
                // Mistral's Jinja template requires tool_call_ids to be exactly
                // 9-char alphanumeric, and ToolResult roles must follow an
                // assistant message with matching tool_calls. Easiest to avoid
                // the whole template minefield by using a plain User message.
                roundTrips++;

                // Short mode tag — the tool output already has instructions.
                // Doubling up causes small models to echo them.
                const string webMode =
                    "\n\nSearch results are in the next message. " +
                    "Summarize them. No URLs. Be brief.";

                var messagesForSummary = InjectModeIntoSystemPrompt(_history,
                    memoryPackText + webMode);
                messagesForSummary.Add(ChatMessage.User(
                    "[Search results — reference only, do not display to user]\n" + toolResult));

                var response = await CallLlmWithRetrySafe(
                    messagesForSummary, roundTrips, MaxTokensWebSummary, cancellationToken);

                var text = TruncateSelfDialogue(response.Content ?? "[No response]");

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

            // ── Tooling mode: keep the existing tool loop ─────────────
            // Inject memory into the system prompt for the tool loop.
            // We mutate _history[0] temporarily; the pack persists for
            // the duration of this tool loop.
            if (!string.IsNullOrWhiteSpace(memoryPackText))
                InjectMemoryIntoHistoryInPlace(_history, memoryPackText);

            var tools = await BuildToolDefinitionsAsync(cancellationToken);

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
            return AgentResponse.FromError($"Agent error: {ex.Message}");
        }
    }

    private static bool IsLmStudioRegexFailure(HttpRequestException ex)
    {
        var msg = ex.Message ?? "";
        return msg.Contains("Failed to process regex", StringComparison.OrdinalIgnoreCase);
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

        if (minimal.Count == 0)
            return await Call(messages);

        return await Call(minimal);
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
    /// system prompt injection. Returns empty string on any failure —
    /// memory retrieval is best-effort and must never block the main flow.
    /// </summary>
    private async Task<string> RetrieveMemoryContextAsync(
        string userMessage, CancellationToken cancellationToken)
    {
        try
        {
            var args = JsonSerializer.Serialize(new { query = userMessage });
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
                    return "";
                }
            }

            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;

            if (root.TryGetProperty("packText", out var packTextEl) &&
                packTextEl.ValueKind == JsonValueKind.String)
            {
                var packText = packTextEl.GetString() ?? "";

                if (!string.IsNullOrWhiteSpace(packText))
                {
                    // Log the retrieval audit event
                    var facts  = root.TryGetProperty("facts", out var f)  ? f.GetInt32() : 0;
                    var events = root.TryGetProperty("events", out var ev) ? ev.GetInt32() : 0;
                    var chunks = root.TryGetProperty("chunks", out var ch) ? ch.GetInt32() : 0;

                    LogEvent("MEMORY_RETRIEVED",
                        $"Retrieved {facts} facts, {events} events, " +
                        $"{chunks} context chunks for this reply.");

                    return packText;
                }
            }

            return "";
        }
        catch
        {
            // Memory retrieval is best-effort — never block the main flow
            return "";
        }
    }

    /// <summary>
    /// Returns a copy of history with the memory pack text appended to
    /// the system message. Used for casual / web paths where we don't
    /// want to mutate _history.
    /// </summary>
    private static List<ChatMessage> InjectMemoryIntoHistory(
        List<ChatMessage> history, string memoryPackText)
    {
        if (string.IsNullOrWhiteSpace(memoryPackText))
            return history;

        return InjectModeIntoSystemPrompt(history, memoryPackText);
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

    /// <summary>
    /// Uses the LLM to extract a clean search query AND a recency window
    /// from the user's conversational message. One fast call, two outputs.
    ///
    /// "what happened with the stock market today?"
    ///   → query: "stock market", recency: "day"
    ///
    /// "tell me about the Taylor Swift headlines"
    ///   → query: "Taylor Swift headlines", recency: "any"
    ///
    /// Falls back to the raw message (trimmed) + "any" if the LLM fails.
    /// </summary>
    private async Task<(string Query, string Recency)> ExtractSearchQueryAsync(
        string userMessage, CancellationToken cancellationToken)
    {
        const int queryMaxTokens = 30;
        const string defaultRecency = "any";

        // Strip explicit /search prefix before handing to LLM
        var input = userMessage.Trim();
        foreach (var prefix in new[] { "/search ", "search:" })
        {
            if (input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                input = input[prefix.Length..].Trim();
                break;
            }
        }

        // If it's already short and looks clean, skip the LLM call
        // but still detect obvious recency hints
        if (input.Length <= 40 && !input.Contains('?'))
            return (input, DetectRecencyFallback(input));

        try
        {
            var messages = new List<ChatMessage>
            {
                ChatMessage.System(
                    "Extract the search query AND a time range from the user's message.\n" +
                    "Reply in exactly this format on one line:\n" +
                    "QUERY | RECENCY\n\n" +
                    "QUERY  = the core search keywords (no quotes, no punctuation)\n" +
                    "RECENCY = day, week, month, or any\n\n" +
                    "Examples:\n" +
                    "\"whats going on in the stock market today?\" → stock market | day\n" +
                    "\"Taylor Swift news this week\" → Taylor Swift news | week\n" +
                    "\"tell me about quantum computing\" → quantum computing | any"),
                ChatMessage.User(input)
            };

            var response = await _llm.ChatAsync(
                messages, tools: null, queryMaxTokens, cancellationToken);

            var raw = (response.Content ?? "").Trim(' ', '"', '\'', '.', '\n', '\r');

            // Parse the "query | recency" format
            var parts = raw.Split('|', 2, StringSplitOptions.TrimEntries);
            var query   = parts.Length > 0 ? parts[0].Trim(' ', '"', '\'', '.') : "";
            var recency = parts.Length > 1 ? NormalizeRecency(parts[1]) : defaultRecency;

            // Sanity check: LLM should return something short and useful
            if (query.Length >= 2 && query.Length <= 120)
            {
                LogEvent("AGENT_QUERY_EXTRACT",
                    $"LLM extracted: \"{query}\" (recency={recency})");
                return (query, recency);
            }
        }
        catch (Exception ex)
        {
            LogEvent("AGENT_QUERY_EXTRACT_FAIL",
                $"LLM query extraction failed, using raw message: {ex.Message}");
        }

        // Fallback: trim punctuation and detect recency from keywords
        var fallbackQuery = input.Trim(' ', '?', '!', '.', ',');
        return (fallbackQuery, DetectRecencyFallback(fallbackQuery));
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
            lower.Contains("last few days"))
            return "week";
        if (lower.Contains("this month") || lower.Contains("past month") ||
            lower.Contains("recently"))
            return "month";
        return "any";
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
                    "remember/save/store information, take a note)\n\n" +
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

            // If the model returned something unexpected, default to casual
            LogEvent("AGENT_CLASSIFY_UNCLEAR",
                $"LLM returned \"{raw}\" — defaulting to Casual");
            return ChatIntent.Casual;
        }
        catch (Exception ex)
        {
            // If the classification call fails, fall back to casual.
            // Better to have a friendly non-answer than a crashed request.
            LogEvent("AGENT_CLASSIFY_FAIL",
                $"Intent classification failed: {ex.Message} — defaulting to Casual");
            return ChatIntent.Casual;
        }
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
    /// Detects messages where the user explicitly asks the assistant to
    /// persist information: "remember that ...", "note that ...", etc.
    /// These must route to Tooling so the MemoryStoreFacts tool is
    /// available — otherwise the LLM just hallucinates a save.
    /// </summary>
    private static bool LooksLikeMemoryWriteRequest(string lower)
    {
        // Prefix patterns — user typically starts with these
        ReadOnlySpan<string> prefixes =
        [
            "remember that", "remember this", "remember i",
            "remember my", "remember me",
            "please remember", "can you remember",
            "note that", "note this", "make a note",
            "save that", "save this",
            "don't forget", "do not forget",
            "keep in mind", "store that", "store this"
        ];

        foreach (var prefix in prefixes)
        {
            if (lower.Contains(prefix))
                return true;
        }

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
}
