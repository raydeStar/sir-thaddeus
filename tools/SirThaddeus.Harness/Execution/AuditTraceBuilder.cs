using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.Contracts;
using SirThaddeus.Harness.Tracing;

namespace SirThaddeus.Harness.Execution;

/// <summary>
/// Reconstructs a tool trace (tool calls, turns, steps) from the JSONL
/// audit format that BOTH runtimes write — v1 surfaces it via
/// <c>/api/audit?take=N</c>, v2 writes it to
/// <c>&lt;sandboxRoot&gt;/logs/audit.jsonl</c>. The shared format is
/// <c>MCP_TOOL_CALL_START</c> / <c>MCP_TOOL_CALL_END</c> entries with
/// metadata fields <c>request_id</c>, <c>tool_name_canonical</c>,
/// <c>input_summary</c>, <c>output_summary</c>, <c>error_message</c>.
///
/// <para>This is the canonical path for tool-trace reconstruction. The
/// scoring engine reads <see cref="TraceStep.Result"/> to score whether
/// the model incorporated tool output, so trace fidelity matters.</para>
/// </summary>
internal static class AuditTraceBuilder
{
    /// <summary>
    /// Reads <paramref name="auditFilePath"/> as JSONL, parses each line
    /// into an <see cref="AuditEntryDto"/>, and returns
    /// <see cref="BuildFromAuditEntries"/>'s output. Returns empty trace
    /// if the file is missing or unreadable.
    /// </summary>
    public static (
        IReadOnlyList<ToolCallRecord> ToolCalls,
        IReadOnlyList<RecordedToolTurn> ToolTurns,
        IReadOnlyList<TraceStep> Steps)
        BuildFromAuditFile(string auditFilePath, DateTimeOffset capturedSince)
    {
        if (!File.Exists(auditFilePath))
            return ([], [], []);

        var entries = new List<AuditEntryDto>();
        try
        {
            var lines = File.ReadAllLines(auditFilePath);
            foreach (var (line, index) in lines.Select((l, i) => (l, i)))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (TryParseAuditLine(line, index, out var entry) && entry.TimestampUtc >= capturedSince)
                    entries.Add(entry);
            }
        }
        catch (IOException)
        {
            return ([], [], []);
        }
        return BuildFromAuditEntries(entries);
    }

    /// <summary>
    /// Same trace-shape as the v1 client previously did inline — extracted
    /// here so v2 can call it after reading the audit JSONL file directly.
    /// </summary>
    public static (
        IReadOnlyList<ToolCallRecord> ToolCalls,
        IReadOnlyList<RecordedToolTurn> ToolTurns,
        IReadOnlyList<TraceStep> Steps)
        BuildFromAuditEntries(IReadOnlyList<AuditEntryDto> auditEntries)
    {
        var starts = new Dictionary<string, (string ToolName, string Arguments, DateTimeOffset Timestamp)>(
            StringComparer.OrdinalIgnoreCase);
        var toolCalls = new List<ToolCallRecord>();
        var toolTurns = new List<RecordedToolTurn>();
        var steps = new List<TraceStep>();
        var stepIndex = 0;
        var toolTurnIndex = 0;

        foreach (var entry in auditEntries.OrderBy(e => e.TimestampUtc))
        {
            if (string.Equals(entry.Category, "MCP_TOOL_CALL_START", StringComparison.OrdinalIgnoreCase))
            {
                var meta = ParseMetadata(entry.MetadataJson);
                var requestId = GetString(meta, "request_id");
                var toolName = GetString(meta, "tool_name_canonical") ?? "unknown";
                var arguments = GetString(meta, "input_summary") ?? "{}";
                if (!string.IsNullOrWhiteSpace(requestId))
                    starts[requestId] = (toolName, arguments, entry.TimestampUtc);

                steps.Add(new TraceStep
                {
                    StepIndex = ++stepIndex,
                    StepType = "tool_call",
                    CallId = requestId,
                    ToolName = toolName,
                    Arguments = arguments,
                    StartedAt = entry.TimestampUtc
                });
            }
            else if (string.Equals(entry.Category, "MCP_TOOL_CALL_END", StringComparison.OrdinalIgnoreCase))
            {
                var meta = ParseMetadata(entry.MetadataJson);
                var requestId = GetString(meta, "request_id");
                var errorMessage = GetString(meta, "error_message");
                var outputSummary = GetString(meta, "output_summary") ?? string.Empty;
                var success = entry.Message.Contains("(ok)", StringComparison.OrdinalIgnoreCase);

                starts.TryGetValue(requestId ?? string.Empty, out var start);
                var toolName = start.ToolName ?? GetString(meta, "tool_name_canonical") ?? "unknown";
                var arguments = start.Arguments ?? GetString(meta, "input_summary") ?? "{}";
                var resultText = success ? outputSummary : (errorMessage ?? outputSummary);

                toolCalls.Add(new ToolCallRecord
                {
                    ToolName = toolName,
                    Arguments = arguments,
                    Result = resultText,
                    Success = success
                });
                toolTurns.Add(new RecordedToolTurn
                {
                    Index = toolTurnIndex++,
                    ToolName = toolName,
                    ArgumentsJson = arguments,
                    ResultText = resultText,
                    Success = success
                });
                steps.Add(new TraceStep
                {
                    StepIndex = ++stepIndex,
                    StepType = "tool_result",
                    CallId = requestId,
                    ToolName = toolName,
                    Arguments = arguments,
                    StartedAt = start.Timestamp == default ? entry.TimestampUtc : start.Timestamp,
                    EndedAt = entry.TimestampUtc,
                    DurationMs = Math.Max(0,
                        (long)(entry.TimestampUtc - (start.Timestamp == default ? entry.TimestampUtc : start.Timestamp))
                            .TotalMilliseconds),
                    Result = success
                        ? ToolResultPayloads.BuildSuccess(resultText)
                        : ToolResultPayloads.BuildErrorJson("tool_error", resultText, false),
                    Error = success
                        ? null
                        : new TraceError
                        {
                            Code = "tool_error",
                            Message = resultText,
                            Retriable = false
                        }
                });
            }
        }
        return (toolCalls, toolTurns, steps);
    }

    /// <summary>
    /// Parses one JSONL audit line. <c>JsonLineAuditLogger</c> writes
    /// records as
    /// <c>{"event_version":"1.0","ts":"...","actor":"...","action":"...",
    /// "target":...,"result":"...","details":{...},"permission_token_id":"..."}</c>
    /// and we map these onto <see cref="AuditEntryDto"/> — the same shape
    /// the v1 audit HTTP API used to surface to the harness.
    /// </summary>
    private static bool TryParseAuditLine(string line, int index, out AuditEntryDto entry)
    {
        entry = default!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            DateTimeOffset timestamp = DateTimeOffset.MinValue;
            if (root.TryGetProperty("ts", out var ts) &&
                ts.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(ts.GetString(), out var parsed))
            {
                timestamp = parsed;
            }

            var category = TryGetString(root, "action") ?? "";
            var actor = TryGetString(root, "actor") ?? "";
            var target = TryGetString(root, "target") ?? "";
            var result = TryGetString(root, "result") ?? "";
            var correlation = TryGetString(root, "permission_token_id");

            // Build a "message" string mimicking the v1 audit API. The
            // shared <see cref="BuildFromAuditEntries"/> helper detects
            // "(ok)" in this string to mark a tool call as successful.
            var message = string.IsNullOrWhiteSpace(target)
                ? $"{actor} {category} ({result})"
                : $"{actor} {category} {target} ({result})";

            string? metadataJson = null;
            if (root.TryGetProperty("details", out var details) &&
                details.ValueKind == JsonValueKind.Object)
            {
                metadataJson = details.GetRawText();
            }

            entry = new AuditEntryDto(
                Id: $"{timestamp.ToUnixTimeMilliseconds()}-{index}",
                Category: category,
                Message: message,
                TimestampUtc: timestamp,
                CorrelationId: correlation,
                MetadataJson: metadataJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement? ParseMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement? element, string propertyName)
    {
        if (element is not JsonElement root || root.ValueKind != JsonValueKind.Object)
            return null;
        if (!root.TryGetProperty(propertyName, out var prop))
            return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => prop.GetRawText()
        };
    }

    private static string? TryGetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
