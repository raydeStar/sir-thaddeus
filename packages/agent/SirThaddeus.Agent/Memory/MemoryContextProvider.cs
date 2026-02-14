using System.Text.Json;
using SirThaddeus.AuditLog;

namespace SirThaddeus.Agent.Memory;

/// <summary>
/// Default memory prefetch provider backed by MCP memory_retrieve.
/// </summary>
public sealed class MemoryContextProvider : IMemoryContextProvider
{
    private const string MemoryRetrieveToolName = "memory_retrieve";
    private const string MemoryRetrieveToolNameAlt = "MemoryRetrieve";

    private readonly IMcpToolClient _mcp;
    private readonly IAuditLogger _audit;
    private readonly TimeProvider _timeProvider;

    public MemoryContextProvider(
        IMcpToolClient mcp,
        IAuditLogger audit,
        TimeProvider? timeProvider = null)
    {
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<MemoryContextResult> GetContextAsync(
        MemoryContextRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.MemoryEnabled)
        {
            return new MemoryContextResult
            {
                Provenance = new MemoryContextProvenance
                {
                    Skipped = true,
                    Success = false,
                    Summary = "Memory disabled — skipped."
                }
            };
        }

        var start = _timeProvider.GetTimestamp();
        try
        {
            var argsObj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["query"] = request.UserMessage
            };
            var retrievalMode = request.IsColdGreeting ? "greet" : "default";
            if (request.IsColdGreeting)
                argsObj["mode"] = "greet";
            if (!string.IsNullOrWhiteSpace(request.ActiveProfileId))
                argsObj["activeProfileId"] = request.ActiveProfileId;

            var args = JsonSerializer.Serialize(argsObj);
            var callTask = CallToolWithAliasAsync(
                MemoryRetrieveToolName,
                MemoryRetrieveToolNameAlt,
                args,
                cancellationToken);

            var completed = await Task.WhenAny(
                callTask,
                Task.Delay(request.Timeout, cancellationToken));

            if (!ReferenceEquals(completed, callTask))
            {
                return new MemoryContextResult
                {
                    Error = "Memory retrieval timed out.",
                    Provenance = new MemoryContextProvenance
                    {
                        RetrievalMode = retrievalMode,
                        TimedOut = true,
                        Success = false,
                        DurationMs = ElapsedMs(start),
                        Summary = "Timeout — skipped."
                    }
                };
            }

            var call = await callTask;
            if (!call.Success)
            {
                return new MemoryContextResult
                {
                    Error = call.Result,
                    Provenance = new MemoryContextProvenance
                    {
                        SourceTool = call.ToolName,
                        RetrievalMode = retrievalMode,
                        Success = false,
                        DurationMs = ElapsedMs(start),
                        Summary = $"Memory retrieve error: {call.Result}"
                    }
                };
            }

            if (string.IsNullOrWhiteSpace(call.Result))
            {
                return new MemoryContextResult
                {
                    Error = "Empty response from memory retrieval tool.",
                    Provenance = new MemoryContextProvenance
                    {
                        SourceTool = call.ToolName,
                        RetrievalMode = retrievalMode,
                        Success = false,
                        DurationMs = ElapsedMs(start),
                        Summary = "Memory retrieve error: Empty response from memory retrieval tool."
                    }
                };
            }

            using var doc = JsonDocument.Parse(call.Result);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errEl) &&
                errEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(errEl.GetString()))
            {
                var error = errEl.GetString()!;
                WriteAudit("MEMORY_RETRIEVE_ERROR", error);
                return new MemoryContextResult
                {
                    Error = error,
                    Provenance = new MemoryContextProvenance
                    {
                        SourceTool = call.ToolName,
                        RetrievalMode = retrievalMode,
                        Success = false,
                        DurationMs = ElapsedMs(start),
                        Summary = $"Memory retrieve error: {error}"
                    }
                };
            }

            var onboarding = root.TryGetProperty("onboardingNeeded", out var ob) &&
                             ob.ValueKind == JsonValueKind.True;

            var packText = root.TryGetProperty("packText", out var packTextEl) &&
                           packTextEl.ValueKind == JsonValueKind.String
                ? (packTextEl.GetString() ?? "")
                : "";
            packText = SanitizePackTextForRequest(packText, request.UserMessage, request.IsColdGreeting);

            var facts = TryReadInt(root, "facts");
            var events = TryReadInt(root, "events");
            var chunks = TryReadInt(root, "chunks");
            var nuggets = TryReadInt(root, "nuggets");
            var hasProfile = root.TryGetProperty("hasProfile", out var hp) &&
                             hp.ValueKind == JsonValueKind.True;

            if (!string.IsNullOrWhiteSpace(packText))
            {
                WriteAudit("MEMORY_RETRIEVED",
                    $"Retrieved {facts} facts, {events} events, {chunks} chunks, {nuggets} nuggets" +
                    $"{(hasProfile ? " (profile loaded)" : "")} for this reply.");
            }

            var summary = string.IsNullOrWhiteSpace(packText)
                ? "No relevant memories found."
                : Truncate(packText.Replace("\n", " ", StringComparison.Ordinal).Trim(), 200);

            return new MemoryContextResult
            {
                PackText = packText,
                OnboardingNeeded = onboarding,
                Provenance = new MemoryContextProvenance
                {
                    SourceTool = call.ToolName,
                    RetrievalMode = retrievalMode,
                    Success = true,
                    DurationMs = ElapsedMs(start),
                    Facts = facts,
                    Events = events,
                    Chunks = chunks,
                    Nuggets = nuggets,
                    HasProfile = hasProfile,
                    Summary = summary
                }
            };
        }
        catch
        {
            return new MemoryContextResult
            {
                Error = "Memory retrieval failed before parse.",
                Provenance = new MemoryContextProvenance
                {
                    Success = false,
                    DurationMs = ElapsedMs(start),
                    Summary = "Memory retrieve error: Memory retrieval failed before parse."
                }
            };
        }
    }

    private async Task<ToolCallOutcome> CallToolWithAliasAsync(
        string primaryToolName,
        string alternateToolName,
        string argsJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mcp.CallToolAsync(primaryToolName, argsJson, cancellationToken);
            return new ToolCallOutcome(primaryToolName, result, true);
        }
        catch (Exception exPrimary)
        {
            if (!LooksLikeUnknownTool(exPrimary.Message, primaryToolName))
                return new ToolCallOutcome(primaryToolName, exPrimary.Message, false);

            try
            {
                var result = await _mcp.CallToolAsync(alternateToolName, argsJson, cancellationToken);
                return new ToolCallOutcome(alternateToolName, result, true);
            }
            catch (Exception exAlt)
            {
                return new ToolCallOutcome(alternateToolName, exAlt.Message, false);
            }
        }
    }

    private static bool LooksLikeUnknownTool(string? payload, string requestedTool)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        if (!payload.Contains("Unknown tool", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(requestedTool))
            return true;

        if (payload.Contains(requestedTool, StringComparison.OrdinalIgnoreCase))
            return true;

        return payload.Contains(ToPascalCaseToolAlias(requestedTool), StringComparison.OrdinalIgnoreCase);
    }

    private static string ToPascalCaseToolAlias(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return "";

        var parts = toolName
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return "";

        return string.Concat(parts.Select(part =>
            part.Length == 0
                ? ""
                : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string SanitizePackTextForRequest(
        string packText,
        string userMessage,
        bool isColdGreeting)
    {
        if (string.IsNullOrWhiteSpace(packText) || isColdGreeting)
            return packText;

        if (LooksLikePersonalMemoryContextRequest(userMessage))
            return packText;

        // Public/third-person prompts should not receive rich memory
        // context; it can leak unrelated preferences into answers.
        if (LooksLikeThirdPersonPublicTopicPrompt(userMessage))
            return "";

        var reduced = StripTaggedSection(packText, "[NUGGETS]", "[/NUGGETS]");
        reduced = StripTaggedSection(reduced, "[MEMORY CONTEXT]", "[/MEMORY CONTEXT]");
        reduced = RemoveLinesContaining(reduced, "You know this user as");
        reduced = reduced.Trim();

        if (string.IsNullOrWhiteSpace(reduced))
            return "";

        return reduced +
               "\n[MEMORY RULES]\n" +
               "Use profile context only if directly relevant. " +
               "Never mention memory retrieval, profile tags, or unrelated saved facts.\n" +
               "[/MEMORY RULES]";
    }

    private static bool LooksLikePersonalMemoryContextRequest(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var lower = $" {userMessage.Trim().ToLowerInvariant()} ";

        if (lower.Contains(" i ", StringComparison.Ordinal) ||
            lower.Contains(" i'm ", StringComparison.Ordinal) ||
            lower.Contains(" im ", StringComparison.Ordinal) ||
            lower.Contains(" i've ", StringComparison.Ordinal) ||
            lower.Contains(" i ve ", StringComparison.Ordinal) ||
            lower.Contains(" we ", StringComparison.Ordinal) ||
            lower.Contains(" our ", StringComparison.Ordinal))
        {
            return true;
        }

        if (lower.Contains(" about me ", StringComparison.Ordinal) ||
            lower.Contains(" my name ", StringComparison.Ordinal) ||
            lower.Contains(" call me ", StringComparison.Ordinal) ||
            lower.Contains(" what do you remember ", StringComparison.Ordinal) ||
            lower.Contains(" what do you know about me ", StringComparison.Ordinal) ||
            lower.Contains(" remember that i ", StringComparison.Ordinal) ||
            lower.Contains(" i prefer ", StringComparison.Ordinal) ||
            lower.Contains(" i like ", StringComparison.Ordinal))
        {
            return true;
        }

        return lower.Contains(" my ", StringComparison.Ordinal);
    }

    private static bool LooksLikeThirdPersonPublicTopicPrompt(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var lower = $" {userMessage.Trim().ToLowerInvariant()} ";
        var hasThirdPersonCue =
            lower.Contains(" he ", StringComparison.Ordinal) ||
            lower.Contains(" she ", StringComparison.Ordinal) ||
            lower.Contains(" his ", StringComparison.Ordinal) ||
            lower.Contains(" her ", StringComparison.Ordinal) ||
            lower.Contains(" their ", StringComparison.Ordinal) ||
            lower.Contains(" them ", StringComparison.Ordinal);

        if (!hasThirdPersonCue)
            return false;

        return lower.Contains(" policy", StringComparison.Ordinal) ||
               lower.Contains(" immigration", StringComparison.Ordinal) ||
               lower.Contains(" president", StringComparison.Ordinal) ||
               lower.Contains(" elected", StringComparison.Ordinal) ||
               lower.Contains(" news", StringComparison.Ordinal) ||
               lower.Contains(" latest", StringComparison.Ordinal) ||
               lower.Contains(" current", StringComparison.Ordinal);
    }

    private static string StripTaggedSection(string text, string openTag, string closeTag)
    {
        var output = text;
        while (true)
        {
            var start = output.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                break;

            var end = output.IndexOf(closeTag, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                output = output[..start];
                break;
            }

            var removeEnd = end + closeTag.Length;
            output = output[..start] + output[removeEnd..];
        }

        return output;
    }

    private static string RemoveLinesContaining(string text, string token)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var lines = text.Split('\n');
        var kept = lines
            .Where(line => !line.Contains(token, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return string.Join("\n", kept);
    }

    private static int TryReadInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var node))
            return 0;
        return node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "\u2026";

    private long ElapsedMs(long startTimestamp)
        => (long)_timeProvider.GetElapsedTime(startTimestamp, _timeProvider.GetTimestamp()).TotalMilliseconds;

    private void WriteAudit(string action, string detail)
    {
        try
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
        catch
        {
            // Best-effort telemetry.
        }
    }

    private sealed record ToolCallOutcome(string ToolName, string Result, bool Success);
}

