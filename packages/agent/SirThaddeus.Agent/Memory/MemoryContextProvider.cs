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

