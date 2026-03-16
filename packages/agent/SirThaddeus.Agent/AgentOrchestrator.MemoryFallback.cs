using System.Text;
using System.Text.Json;
using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
    private async Task<AgentResponse?> TryRunDeterministicMemoryStoreFallbackAsync(
        RouterOutput route,
        string userMessage,
        IReadOnlyList<ToolDefinition> tools,
        List<ToolCallRecord> toolCallsMade,
        AgentResponse toolLoopResponse,
        CancellationToken cancellationToken)
    {
        if (!MemoryEnabled)
            return null;

        if (!route.Intent.Equals(Intents.MemoryWrite, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!ContainsTool(tools, MemoryStoreFactsToolName, MemoryStoreFactsToolNameAlt))
            return null;

        if (HasSuccessfulMemoryWriteToolCall(toolCallsMade))
            return null;

        if (!TryExtractExplicitMemoryStoreStatement(userMessage, out var statement))
            return null;

        if (!TryBuildDeterministicMemoryFact(statement, out var subject, out var predicate, out var obj))
            return null;

        var factsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                subject,
                predicate,
                @object = obj
            }
        });
        var argsJson = JsonSerializer.Serialize(new
        {
            factsJson,
            sourceRef = "conversation"
        });

        var storeCall = await CallToolWithAliasAsync(
            MemoryStoreFactsToolName,
            MemoryStoreFactsToolNameAlt,
            argsJson,
            cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = storeCall.ToolName,
            Arguments = argsJson,
            Result = storeCall.Result,
            Success = storeCall.Success
        });

        if (!storeCall.Success || TryReadMemoryStoreError(storeCall.Result, out _))
        {
            const string errorText =
                "I couldn't save that to memory right now. Please check memory-write permissions and try again.";
            ReplaceLastAssistantMessage(errorText);
            LogEvent("MEMORY_WRITE_FALLBACK_FAILED", storeCall.Result);
            return new AgentResponse
            {
                Text = errorText,
                Success = false,
                Error = "memory_store_failed",
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = toolLoopResponse.LlmRoundTrips
            };
        }

        var responseText = BuildMemoryStoreConfirmation(storeCall.Result);
        ReplaceLastAssistantMessage(responseText);
        LogEvent("MEMORY_WRITE_FALLBACK_APPLIED",
            $"Deterministic store used for explicit remember request. subject={subject}, predicate={predicate}");

        return new AgentResponse
        {
            Text = responseText,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = toolLoopResponse.LlmRoundTrips
        };
    }

    private static bool ContainsTool(
        IReadOnlyList<ToolDefinition> tools,
        string primaryToolName,
        string alternateToolName)
    {
        foreach (var tool in tools)
        {
            var name = tool.Function.Name;
            if (name.Equals(primaryToolName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(alternateToolName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSuccessfulMemoryWriteToolCall(IReadOnlyList<ToolCallRecord> toolCalls)
    {
        foreach (var call in toolCalls)
        {
            if (!call.Success)
                continue;

            var name = call.ToolName ?? "";
            if (!IsMemoryWriteToolName(name))
                continue;

            // MCP transport success is not enough: a tool can return a
            // structured error payload while the transport call still succeeds.
            if (TryReadStructuredToolError(call.Result, out _))
                continue;

            return true;
        }

        return false;
    }

    private static bool IsMemoryWriteToolName(string toolName)
    {
        return toolName.Equals("memory_store_facts", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("MemoryStoreFacts", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("memory_update_fact", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("MemoryUpdateFact", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("memory_delete_fact", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("MemoryDeleteFact", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractExplicitMemoryStoreStatement(string userMessage, out string statement)
    {
        statement = "";
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var normalized = NormalizeMemoryDirectiveText(userMessage);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        ReadOnlySpan<string> phrases =
        [
            "can you remember that ",
            "can you remember this ",
            "can you remember ",
            "please remember that ",
            "please remember this ",
            "please remember ",
            "remember that ",
            "remember this ",
            "remember ",
            "make a note that ",
            "make a note ",
            "note that ",
            "note this ",
            "save that ",
            "save this ",
            "store that ",
            "store this ",
            "do not forget that ",
            "do not forget ",
            "don't forget that ",
            "don't forget ",
            "keep in mind that ",
            "keep in mind "
        ];

        foreach (var phrase in phrases)
        {
            if (!normalized.StartsWith(phrase, StringComparison.Ordinal))
                continue;

            statement = normalized[phrase.Length..].Trim();
            break;
        }

        if (string.IsNullOrWhiteSpace(statement))
            return false;

        if (statement.StartsWith("that ", StringComparison.Ordinal))
            statement = statement[5..].Trim();
        else if (statement.StartsWith("this ", StringComparison.Ordinal))
            statement = statement[5..].Trim();

        statement = statement.Trim(' ', '.', '!', '?', '"', '\'');
        return !string.IsNullOrWhiteSpace(statement);
    }

    private static bool TryBuildDeterministicMemoryFact(
        string statement,
        out string subject,
        out string predicate,
        out string obj)
    {
        subject = "";
        predicate = "";
        obj = "";

        if (string.IsNullOrWhiteSpace(statement))
            return false;

        var text = CollapseWhitespace(statement.Trim(' ', '.', '!', '?', '"', '\''));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (TryTakeAfterPrefix(text, "my name is ", out var name))
        {
            subject = "user";
            predicate = "name";
            obj = NormalizeFactObject(name);
            return obj.Length > 0;
        }

        if (TryTakeAfterPrefix(text, "i am ", out var am) ||
            TryTakeAfterPrefix(text, "i'm ", out am) ||
            TryTakeAfterPrefix(text, "im ", out am))
        {
            subject = "user";
            predicate = "is";
            obj = NormalizeFactObject(am);
            return obj.Length > 0;
        }

        if (TryTakeAfterPrefix(text, "i like ", out var likes))
        {
            subject = "user";
            predicate = "likes";
            obj = NormalizeFactObject(likes);
            return obj.Length > 0;
        }

        if (TryTakeAfterPrefix(text, "i love ", out var loves))
        {
            subject = "user";
            predicate = "loves";
            obj = NormalizeFactObject(loves);
            return obj.Length > 0;
        }

        if (TryTakeAfterPrefix(text, "i prefer ", out var prefers))
        {
            subject = "user";
            predicate = "prefers";
            obj = NormalizeFactObject(prefers);
            return obj.Length > 0;
        }

        if (TryTakeAfterPrefix(text, "i work at ", out var worksAt))
        {
            subject = "user";
            predicate = "works_at";
            obj = NormalizeFactObject(worksAt);
            return obj.Length > 0;
        }

        if (TryTakeAfterPrefix(text, "i work for ", out var worksFor))
        {
            subject = "user";
            predicate = "works_for";
            obj = NormalizeFactObject(worksFor);
            return obj.Length > 0;
        }

        if (TryTakeAfterPrefix(text, "i live in ", out var livesIn))
        {
            subject = "user";
            predicate = "lives_in";
            obj = NormalizeFactObject(livesIn);
            return obj.Length > 0;
        }

        if (TryTakeAfterPrefix(text, "i live at ", out var livesAt))
        {
            subject = "user";
            predicate = "lives_at";
            obj = NormalizeFactObject(livesAt);
            return obj.Length > 0;
        }

        var isIndex = text.IndexOf(" is ", StringComparison.Ordinal);
        if (isIndex > 0 && isIndex < text.Length - 4)
        {
            subject = NormalizeFactSubject(text[..isIndex]);
            predicate = "is";
            obj = NormalizeFactObject(text[(isIndex + 4)..]);
            if (subject.Length > 0 && obj.Length > 0)
                return true;
        }

        subject = "user";
        predicate = "note";
        obj = NormalizeFactObject(text);
        return obj.Length > 0;
    }

    private static bool TryTakeAfterPrefix(string input, string prefix, out string value)
    {
        value = "";
        if (!input.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        value = input[prefix.Length..].Trim();
        return value.Length > 0;
    }

    private static string NormalizeFactSubject(string value)
    {
        var normalized = CollapseWhitespace(value).Trim(' ', '.', '!', '?', '"', '\'');
        if (normalized.Equals("i", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("me", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("myself", StringComparison.OrdinalIgnoreCase))
        {
            return "user";
        }

        return normalized.Length <= 64
            ? normalized
            : normalized[..64].Trim();
    }

    private static string NormalizeFactObject(string value)
    {
        var normalized = CollapseWhitespace(value).Trim(' ', '.', '!', '?', '"', '\'');
        return normalized.Length <= 220
            ? normalized
            : normalized[..220].Trim();
    }

    private static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var sb = new StringBuilder(value.Length);
        var lastWasSpace = true;
        foreach (var c in value)
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
                lastWasSpace = false;
                continue;
            }

            if (lastWasSpace)
                continue;

            sb.Append(' ');
            lastWasSpace = true;
        }

        return sb.ToString().Trim();
    }

    private static string NormalizeMemoryDirectiveText(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";

        var sb = new StringBuilder(input.Length);
        var lastWasSpace = true;
        foreach (var c in input)
        {
            if (char.IsLetterOrDigit(c) || c is '\'' or '-')
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
                continue;
            }

            if (lastWasSpace)
                continue;

            sb.Append(' ');
            lastWasSpace = true;
        }

        return sb.ToString().Trim();
    }

    private static bool TryReadMemoryStoreError(string payload, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (!doc.RootElement.TryGetProperty("error", out var errorEl))
                return false;

            if (errorEl.ValueKind != JsonValueKind.String)
                return false;

            var value = errorEl.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(value))
                return false;

            error = value;
            return true;
        }
        catch
        {
            return payload.Contains("error", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool TryReadStructuredToolError(string payload, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (!doc.RootElement.TryGetProperty("error", out var errorEl))
                return false;

            if (errorEl.ValueKind == JsonValueKind.String)
            {
                var str = errorEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(str))
                    return false;
                error = str;
                return true;
            }

            if (errorEl.ValueKind == JsonValueKind.Object)
            {
                if (errorEl.TryGetProperty("message", out var msgEl) &&
                    msgEl.ValueKind == JsonValueKind.String)
                {
                    var msg = msgEl.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        error = msg;
                        return true;
                    }
                }

                if (errorEl.TryGetProperty("code", out var codeEl) &&
                    codeEl.ValueKind == JsonValueKind.String)
                {
                    var code = codeEl.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        error = code;
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Ignore parse failures; caller treats non-JSON payloads as non-structured errors.
        }

        return false;
    }

    private static string BuildMemoryStoreConfirmation(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return "Got it — I'll remember that.";

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return "Got it — I'll remember that.";

            var stored = ReadInt(doc.RootElement, "stored");
            var replaced = ReadInt(doc.RootElement, "replaced");
            var skipped = ReadInt(doc.RootElement, "skipped");

            if (stored > 0 || replaced > 0)
                return "Got it — I'll remember that.";
            if (skipped > 0)
                return "You're all set — I already had that in memory.";
        }
        catch
        {
            // Fall back to a generic confirmation.
        }

        return "Got it — I'll remember that.";
    }

    private static int ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var node))
            return 0;
        return node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private void ReplaceLastAssistantMessage(string replacementText)
    {
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (!_history[i].Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                continue;

            _history[i] = ChatMessage.Assistant(replacementText);
            return;
        }

        AppendAssistantMessage(replacementText);
    }
}
