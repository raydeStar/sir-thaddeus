using System.Text.Json;
using System.Text.Json.Serialization;
using SirThaddeus.AuditLog;

namespace Thaddeus.Runtime.Api;

/// <summary>
/// Local-only audit and product-quality measurements. Metrics are derived from
/// append-only audit evidence; unavailable signals stay explicitly unavailable
/// instead of being inferred from proxies.
/// </summary>
public static class AuditInsightsApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapAuditInsightsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audit", async (
            int? limit,
            string? action,
            string? result,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var capped = Math.Clamp(limit ?? 200, 1, 2_000);
            var events = await audit.ReadTailAsync(capped, ct).ConfigureAwait(false);
            var filtered = events
                .Where(item => string.IsNullOrWhiteSpace(action) ||
                    item.Action.Contains(action, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.IsNullOrWhiteSpace(result) ||
                    string.Equals(item.Result, result, StringComparison.OrdinalIgnoreCase))
                .Select(ToDto)
                .Reverse()
                .ToArray();
            return Results.Json(new AuditTrailResponse(filtered), JsonOptions);
        });

        app.MapGet("/api/insights", async (
            int? limit,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var capped = Math.Clamp(limit ?? 2_000, 100, 10_000);
            var events = await audit.ReadTailAsync(capped, ct).ConfigureAwait(false);
            return Results.Json(Compute(events), JsonOptions);
        });

        app.MapGet("/api/audit/export", async (
            int? limit,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var capped = Math.Clamp(limit ?? 10_000, 1, 50_000);
            var events = await audit.ReadTailAsync(capped, ct).ConfigureAwait(false);
            var jsonl = string.Join(
                Environment.NewLine,
                events.Select(item => JsonSerializer.Serialize(ToDto(item), JsonOptions)));
            return Results.Text(
                jsonl + (events.Count > 0 ? Environment.NewLine : string.Empty),
                "application/x-ndjson");
        });

        app.MapPost("/api/insights/feedback", async (
            AssistantOutcomeFeedbackRequest? request,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (request is null ||
                string.IsNullOrWhiteSpace(request.MessageId) ||
                request.Confidence is < 0 or > 1)
            {
                return Results.BadRequest(new { error = "messageId and confidence from 0 to 1 are required" });
            }

            await audit.AppendAsync(new AuditEvent
            {
                Actor = "user",
                Action = "ASSISTANT_OUTCOME_FEEDBACK",
                Target = request.MessageId.Trim(),
                Result = request.Success ? "success" : "correction",
                Details = new Dictionary<string, object>
                {
                    ["confidence"] = request.Confidence,
                    ["actualSuccess"] = request.Success,
                    ["evidenceLevel"] = request.EvidenceLevel?.Trim() ?? "unspecified",
                },
            }, ct).ConfigureAwait(false);
            return Results.Ok(new { recorded = true });
        });

        return app;
    }

    internal static AssistantInsightsResponse Compute(IReadOnlyList<AuditEvent> events)
    {
        var outcomes = events
            .Where(item => item.Action is "CHAT_RUN_COMPLETED" or "CHAT_RUN_FAILED" or "CHAT_RUN_CANCELLED")
            .GroupBy(item => item.Target ?? string.Empty, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        var completed = outcomes.Count(item => item.Action == "CHAT_RUN_COMPLETED");
        var interventionActions = new HashSet<string>(StringComparer.Ordinal)
        {
            "CHAT_RUN_PAUSE", "CHAT_RUN_TAKE_OVER", "CHAT_RUN_REDIRECT", "CHAT_RUN_CANCEL",
        };
        var interventionRuns = events
            .Where(item => interventionActions.Contains(item.Action) && !string.IsNullOrWhiteSpace(item.Target))
            .Select(item => item.Target!)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var permissions = events.Where(item => item.Action == "TOOL_PERMISSION_DECISION").ToArray();
        var toolActions = events.Count(item => item.Action == "MCP_TOOL_CALL_START");
        var escalations = permissions.Length + events.Count(item => item.Action == "CHAT_PLAN_APPROVE");
        var recoveries = events.Where(item => item.Action == "MCP_SERVER_RECOVERY").ToArray();
        var errorSessions = events
            .Where(item => item.Action == "MCP_TOOL_CALL_END" &&
                item.Result is "error" or "blocked" or "cancelled")
            .Select(item => ReadString(item, "session_id"))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.Ordinal);
        var recoveredTasks = outcomes.Count(item =>
            item.Action == "CHAT_RUN_COMPLETED" &&
            errorSessions.Contains(ReadString(item, "assistantMessageId")));
        var erroredTasks = outcomes.Count(item =>
            errorSessions.Contains(ReadString(item, "assistantMessageId")));
        var fastOrBroad = permissions.Count(item =>
            ReadLong(item, "latencyMs") is >= 0 and < 1_000 ||
            string.Equals(ReadString(item, "decision"), "always", StringComparison.OrdinalIgnoreCase));
        var feedback = events
            .Where(item => item.Action == "ASSISTANT_OUTCOME_FEEDBACK" && !string.IsNullOrWhiteSpace(item.Target))
            .GroupBy(item => item.Target!, StringComparer.Ordinal)
            .Select(group => group.Last())
            .Select(item => new
            {
                Confidence = ReadDouble(item, "confidence"),
                Actual = ReadBoolean(item, "actualSuccess"),
            })
            .Where(item => item.Confidence.HasValue && item.Actual.HasValue)
            .ToArray();
        double? calibration = feedback.Length == 0
            ? null
            : feedback.Average(item =>
                1d - Math.Abs(item.Confidence!.Value - (item.Actual!.Value ? 1d : 0d)));

        var metrics = new[]
        {
            Rate("task-completion", "Task completion", completed, outcomes.Length,
                "Runs completed successfully divided by all terminal runs."),
            Rate("human-intervention", "Human intervention", interventionRuns, outcomes.Length,
                "Terminal runs with pause, redirect, takeover, or stop divided by terminal runs."),
            Rate("hitl-approval", "HITL approval-required", permissions.Length, toolActions,
                "Tool calls requiring an explicit permission decision divided by all tool calls."),
            Rate("recovery-success", "Recovery success",
                erroredTasks > 0 ? recoveredTasks : recoveries.Count(item => item.Result == "ok"),
                erroredTasks > 0 ? erroredTasks : recoveries.Length,
                erroredTasks > 0
                    ? "Runs completing after a recorded tool error divided by terminal runs that hit a tool error."
                    : "Successful MCP recovery events divided by recorded recovery attempts."),
            calibration.HasValue
                ? new AssistantInsightMetric(
                    "trust-calibration",
                    "Trust calibration",
                    calibration,
                    feedback.Length,
                    feedback.Length,
                    "measured",
                    "Agreement between the receipt's displayed evidence confidence and user-confirmed outcome.")
                : new AssistantInsightMetric(
                    "trust-calibration",
                    "Trust calibration",
                    null,
                    0,
                    0,
                    "insufficient-data",
                    "Needs a displayed evidence-confidence and user outcome pair; no proxy is substituted."),
            Rate("escalation-frequency", "Escalation frequency", escalations, outcomes.Length,
                "Plan approvals plus tool permission decisions divided by terminal runs."),
            Rate("approval-fatigue", "Approval fatigue signal", fastOrBroad, permissions.Length,
                "Permission decisions made in under one second or granting always-allow."),
        };

        return new AssistantInsightsResponse(
            DateTimeOffset.UtcNow,
            events.Count,
            metrics);
    }

    private static AssistantInsightMetric Rate(
        string key,
        string label,
        int numerator,
        int denominator,
        string definition) =>
        denominator == 0
            ? new(key, label, null, numerator, denominator, "insufficient-data", definition)
            : new(key, label, (double)numerator / denominator, numerator, denominator, "measured", definition);

    private static string? ReadString(AuditEvent item, string key) =>
        item.Details?.TryGetValue(key, out var value) == true ? value?.ToString() : null;

    private static long? ReadLong(AuditEvent item, string key)
    {
        if (item.Details?.TryGetValue(key, out var value) != true)
            return null;
        if (value is long number)
            return number;
        if (value is int integer)
            return integer;
        return long.TryParse(value?.ToString(), out var parsed) ? parsed : null;
    }

    private static double? ReadDouble(AuditEvent item, string key)
    {
        if (item.Details?.TryGetValue(key, out var value) != true)
            return null;
        if (value is double number)
            return number;
        return double.TryParse(value?.ToString(), out var parsed) ? parsed : null;
    }

    private static bool? ReadBoolean(AuditEvent item, string key)
    {
        if (item.Details?.TryGetValue(key, out var value) != true)
            return null;
        if (value is bool flag)
            return flag;
        return bool.TryParse(value?.ToString(), out var parsed) ? parsed : null;
    }

    private static AuditEventDto ToDto(AuditEvent item) =>
        new(item.Timestamp, item.Actor, item.Action, item.Target, item.Result, item.PermissionTokenId, item.Details);
}

public sealed record AuditEventDto(
    DateTimeOffset Timestamp,
    string Actor,
    string Action,
    string? Target,
    string Result,
    string? PermissionTokenId,
    IReadOnlyDictionary<string, object>? Details);

public sealed record AuditTrailResponse(IReadOnlyList<AuditEventDto> Events);

public sealed record AssistantInsightMetric(
    string Key,
    string Label,
    double? Value,
    int Numerator,
    int Denominator,
    string Status,
    string Definition);

public sealed record AssistantInsightsResponse(
    DateTimeOffset GeneratedAt,
    int SampleEvents,
    IReadOnlyList<AssistantInsightMetric> Metrics);

public sealed record AssistantOutcomeFeedbackRequest(
    string MessageId,
    bool Success,
    double Confidence,
    string? EvidenceLevel);
