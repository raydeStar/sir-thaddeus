using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Contracts;
using SirThaddeus.McpShared;

/// <summary>
/// Aggregates audit events into the structured activity summary
/// consumed by the trust-ledger drawer. Stateless — rebuilds
/// the snapshot from audit tail on every call.
/// </summary>
internal sealed class ActivitySummaryAggregator
{
    private readonly IAuditLogger _audit;
    private readonly Func<AppSettings> _getSettings;
    private readonly ApiPermissionGate? _permissionGate;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public ActivitySummaryAggregator(
        IAuditLogger audit,
        Func<AppSettings> getSettings,
        ApiPermissionGate? permissionGate)
    {
        _audit = audit;
        _getSettings = getSettings;
        _permissionGate = permissionGate;
    }

    public async Task<ActivitySummaryResponse> BuildSummaryAsync(
        string? sessionId,
        CancellationToken ct)
    {
        var events = await ReadToolCallEventsAsync(ct);

        if (!string.IsNullOrEmpty(sessionId))
        {
            events = events.Where(e =>
                string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
                .ToList();
        }

        var session = BuildSessionSummary(sessionId ?? "", events);
        var categories = BuildCategorySummaries(events);
        var connections = BuildConnectionSummaries(events);

        return new ActivitySummaryResponse(session, categories, connections);
    }

    // ── Audit reading ────────────────────────────────────────────────

    private async Task<List<ParsedToolCall>> ReadToolCallEventsAsync(CancellationToken ct)
    {
        if (_audit is not JsonLineAuditLogger logger)
            return [];

        var rawEvents = await logger.ReadTailAsync(1000, ct);

        // Index START events by request_id so we can enrich END events
        // with input_summary (which is only written on START).
        var startEvents = new Dictionary<string, AuditEvent>(StringComparer.Ordinal);
        var toolCalls = new List<ParsedToolCall>();

        foreach (var evt in rawEvents)
        {
            if (string.Equals(evt.Action, "MCP_TOOL_CALL_START", StringComparison.OrdinalIgnoreCase))
            {
                var reqId = GetString(evt.Details, "request_id");
                if (reqId is not null)
                    startEvents[reqId] = evt;
                continue;
            }

            if (!string.Equals(evt.Action, "MCP_TOOL_CALL_END", StringComparison.OrdinalIgnoreCase))
                continue;

            // Look up the matching START event for input_summary
            AuditEvent? startEvt = null;
            var endReqId = GetString(evt.Details, "request_id");
            if (endReqId is not null)
                startEvents.TryGetValue(endReqId, out startEvt);

            var parsed = ParseToolCall(evt, startEvt);
            if (parsed is not null)
                toolCalls.Add(parsed);
        }

        return toolCalls;
    }

    private ParsedToolCall? ParseToolCall(AuditEvent evt, AuditEvent? startEvt)
    {
        var details = evt.Details;

        // The END event stores the canonical tool name in Target but not
        // always in Details (the START event has tool_name_canonical/requested
        // but the END event only has them for error paths via LogEnd).
        // Fall back to evt.Target which is always the canonical name.
        var toolName = GetString(details, "tool_name_canonical")
                    ?? GetString(details, "tool_name_requested")
                    ?? evt.Target
                    ?? "unknown";

        var group = ToolGroupPolicy.ResolveGroup(toolName);
        ToolManifest.TryGetTool(toolName, out var descriptor);

        var category = descriptor?.Category ?? group;
        var resultStatus = DeriveResultStatus(evt.Result, GetString(details, "error_message"));
        var permissionStr = GetString(details, "permission") ?? "not_required";
        var durationMs = GetLong(details, "duration_ms");

        // input_summary is only on the START event
        var inputSummary = GetString(startEvt?.Details, "input_summary") ?? "";

        return new ParsedToolCall
        {
            RequestId = GetString(details, "request_id") ?? "",
            SessionId = GetString(details, "session_id") ?? "",
            ToolName = toolName,
            DisplayName = FormatToolDisplayName(toolName),
            Category = category,
            PermissionGroup = group,
            InputSummary = inputSummary,
            OutputSummary = GetString(details, "output_summary") ?? "",
            PermissionStatus = permissionStr,
            ResultStatus = resultStatus,
            DurationMs = durationMs,
            TimestampUtc = evt.Timestamp,
            ErrorMessage = GetString(details, "error_message"),
        };
    }

    // ── Session summary ──────────────────────────────────────────────

    private static SessionSummaryDto BuildSessionSummary(string sessionId, List<ParsedToolCall> calls)
    {
        if (calls.Count == 0)
        {
            return new SessionSummaryDto(
                SessionId: sessionId,
                TotalToolCalls: 0,
                ApprovedCalls: 0,
                DeniedCalls: 0,
                ErrorCalls: 0,
                FirstCallUtc: null,
                LastCallUtc: null);
        }

        return new SessionSummaryDto(
            SessionId: sessionId,
            TotalToolCalls: calls.Count,
            ApprovedCalls: calls.Count(c => c.ResultStatus is "success" or "completed"),
            DeniedCalls: calls.Count(c => c.ResultStatus == "denied"),
            ErrorCalls: calls.Count(c => c.ResultStatus == "error"),
            FirstCallUtc: calls.Min(c => c.TimestampUtc),
            LastCallUtc: calls.Max(c => c.TimestampUtc));
    }

    // ── Category summaries ───────────────────────────────────────────

    private static List<ToolCategorySummaryDto> BuildCategorySummaries(List<ParsedToolCall> calls)
    {
        return calls
            .GroupBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.Equals(g.Key, "meta", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(g.Key, "time", StringComparison.OrdinalIgnoreCase))
            .Select(g =>
            {
                var groupCalls = g.OrderByDescending(c => c.TimestampUtc).ToList();
                return new ToolCategorySummaryDto(
                    CategoryKey: g.Key,
                    DisplayName: ToolManifest.GetCategoryDisplayName(g.Key),
                    TotalCalls: groupCalls.Count,
                    SucceededCalls: groupCalls.Count(c => c.ResultStatus is "success" or "completed"),
                    DeniedCalls: groupCalls.Count(c => c.ResultStatus == "denied"),
                    ErrorCalls: groupCalls.Count(c => c.ResultStatus == "error"),
                    LastCallUtc: groupCalls.Count > 0 ? groupCalls[0].TimestampUtc : null,
                    RecentCalls: groupCalls.Take(10).Select(ToCallSummary).ToList());
            })
            .OrderByDescending(c => c.LastCallUtc)
            .ToList();
    }

    private static ToolCallSummaryDto ToCallSummary(ParsedToolCall call)
    {
        return new ToolCallSummaryDto(
            RequestId: call.RequestId,
            ToolName: call.ToolName,
            DisplayName: call.DisplayName,
            InputSummary: call.InputSummary,
            OutputSummary: call.OutputSummary,
            PermissionStatus: call.PermissionStatus,
            ResultStatus: call.ResultStatus,
            DurationMs: call.DurationMs,
            TimestampUtc: call.TimestampUtc,
            ErrorMessage: call.ErrorMessage);
    }

    // ── Connection summaries ─────────────────────────────────────────

    private List<McpConnectionSummaryDto> BuildConnectionSummaries(List<ParsedToolCall> calls)
    {
        var settings = _getSettings();
        var snapshot = ToolGroupPolicy.BuildSnapshot(settings, isDebugBuild: false);
        var sessionGrants = _permissionGate;

        var connections = new List<McpConnectionSummaryDto>();

        foreach (var (group, displayName) in ToolManifest.ConnectionDisplayNames)
        {
            var policy = ToolGroupPolicy.ResolveEffectivePolicy(group, snapshot);
            var approvalState = MapPolicyToApprovalState(policy);

            // Find tool calls belonging to this permission group
            var groupCalls = calls.Where(c =>
                string.Equals(c.PermissionGroup, group, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Get distinct tool names in this group
            var toolNames = groupCalls
                .Select(c => c.ToolName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Count tools in the manifest for this group
            var manifestToolCount = ToolManifest.All
                .Count(t => ToolManifest.PermissionGroupToCategories.TryGetValue(group, out var cats)
                         && cats.Any(cat => string.Equals(t.Category, cat, StringComparison.OrdinalIgnoreCase)));

            connections.Add(new McpConnectionSummaryDto(
                ConnectionId: group,
                DisplayName: displayName,
                ApprovalState: approvalState,
                TransportType: "embedded_stdio",
                ToolCount: manifestToolCount,
                TotalCalls: groupCalls.Count,
                LastCallUtc: groupCalls.Count > 0 ? groupCalls.Max(c => c.TimestampUtc) : null,
                ToolNames: toolNames));
        }

        return connections
            .OrderByDescending(c => c.TotalCalls)
            .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static string MapPolicyToApprovalState(string policy) => policy switch
    {
        "always" => ConnectionApprovalStates.AlwaysAllow,
        "ask"    => ConnectionApprovalStates.PerRequest,
        "off"    => ConnectionApprovalStates.Disabled,
        _        => ConnectionApprovalStates.PerRequest,
    };

    private static string DeriveResultStatus(string auditResult, string? errorMessage)
    {
        if (!string.IsNullOrEmpty(errorMessage))
            return "error";

        return auditResult?.ToLowerInvariant() switch
        {
            "ok" or "success" or "completed" => "success",
            "denied" or "blocked"            => "denied",
            "error" or "failed"              => "error",
            _                                => "success",
        };
    }

    private static string FormatToolDisplayName(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return "Tool";

        // web_search → Web Search
        var parts = toolName.Split('_', '.');
        return string.Join(' ', parts.Select(p =>
            p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static string? GetString(Dictionary<string, object>? details, string key)
    {
        if (details is null) return null;
        if (details.TryGetValue(key, out var value))
        {
            if (value is string s) return s;
            if (value is JsonElement je && je.ValueKind == JsonValueKind.String) return je.GetString();
            return value?.ToString();
        }
        return null;
    }

    private static long GetLong(Dictionary<string, object>? details, string key)
    {
        if (details is null) return 0;
        if (details.TryGetValue(key, out var value))
        {
            if (value is long l) return l;
            if (value is int i) return i;
            if (value is JsonElement je && je.ValueKind == JsonValueKind.Number) return je.GetInt64();
            if (value is string s && long.TryParse(s, out var parsed)) return parsed;
        }
        return 0;
    }

    private static string? GetDetail(AuditEvent evt, string key)
    {
        return evt.Details is not null ? GetString(evt.Details, key) : null;
    }

    // ── Internal model ───────────────────────────────────────────────

    private sealed class ParsedToolCall
    {
        public required string RequestId { get; init; }
        public required string SessionId { get; init; }
        public required string ToolName { get; init; }
        public required string DisplayName { get; init; }
        public required string Category { get; init; }
        public required string PermissionGroup { get; init; }
        public required string InputSummary { get; init; }
        public required string OutputSummary { get; init; }
        public required string PermissionStatus { get; init; }
        public required string ResultStatus { get; init; }
        public long DurationMs { get; init; }
        public DateTimeOffset TimestampUtc { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
