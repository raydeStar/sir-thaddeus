using System.Diagnostics;
using System.Text.Json;
using SirThaddeus.Agent.ConversationSegmentation;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;

namespace SirThaddeus.Agent;

// ─────────────────────────────────────────────────────────────────────────
// Audited MCP Tool Client — The Single Enforcement Point
//
// Wraps any IMcpToolClient and becomes the ONLY path the agent uses to
// call MCP tools. Every call writes two audit events:
//
//   1. MCP_TOOL_CALL_START  — before execution (with redacted input)
//   2. MCP_TOOL_CALL_END    — after execution  (with redacted output,
//                             duration, permission outcome, error info)
//
// Permission gating is delegated to IToolPermissionGate, which the
// runtime implements using the real prompter/broker. If a tool requires
// permission and the gate denies it, the call is blocked and the agent
// receives a clear error message — nothing executes silently.
//
// Invariants enforced:
//   I3 — No side effects without explicit permission
//   I4 — Audit is always on; every tool call is logged
//   D2 — Redaction by default (OCR text, file content, etc.)
//
// TODO: ROADMAP MARKER - Full MCP Protocol Support
// This architecture must eventually support the `Resources` and `Prompts`
// endpoints of the Model Context Protocol (MCP) to be truly enterprise-grade.
// Currently, only the `Tools` facet is orchestrated and audited here.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Wraps an <see cref="IMcpToolClient"/> with audit logging, permission
/// gating, and output redaction. This is the single enforcement point
/// for all agent MCP tool calls.
/// </summary>
public sealed class AuditedMcpToolClient : IMcpToolClient
{
    private readonly IMcpToolClient     _inner;
    private readonly IAuditLogger       _audit;
    private readonly IToolPermissionGate _gate;
    private readonly string             _sessionId;
    private readonly Func<RuntimeControlState> _getRuntimeControls;
    private readonly object _budgetGate = new();
    private long _sessionToolCalls;
    private string _activeTurnKey = "";
    private int _turnToolCalls;
    private int _turnWebCalls;
    private readonly Queue<DateTimeOffset> _fileOpsWindow = new();

    /// <param name="inner">The real MCP tool client to wrap.</param>
    /// <param name="audit">Audit logger for event recording.</param>
    /// <param name="gate">Permission gate callback (runtime-provided).</param>
    /// <param name="sessionId">Current session/run ID for correlation.</param>
    /// <param name="runtimeControls">Optional factory for runtime control state (STOP switch, budget overrides).</param>
    public AuditedMcpToolClient(
        IMcpToolClient     inner,
        IAuditLogger       audit,
        IToolPermissionGate gate,
        string             sessionId,
        Func<RuntimeControlState>? runtimeControls = null)
    {
        _inner     = inner     ?? throw new ArgumentNullException(nameof(inner));
        _audit     = audit     ?? throw new ArgumentNullException(nameof(audit));
        _gate      = gate      ?? throw new ArgumentNullException(nameof(gate));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _getRuntimeControls = runtimeControls ?? (() => new RuntimeControlState());
    }

    /// <inheritdoc />
    public async Task<string> CallToolAsync(
        string toolName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        var requestId     = Guid.NewGuid().ToString("N")[..12];
        var canonical     = Canonicalize(toolName);
        var redactedInput = ToolCallRedactor.RedactInput(canonical, argumentsJson);
        var runtimeControls = _getRuntimeControls();

        var segmentId = SegmentExecutionContext.CurrentSegmentId;
        var startDetails = new Dictionary<string, object>
        {
            ["session_id"]           = _sessionId,
            ["request_id"]           = requestId,
            ["tool_name_requested"]  = toolName,
            ["tool_name_canonical"]  = canonical,
            ["input_summary"]        = redactedInput,
            ["panic_mode"]           = runtimeControls.PanicModeEnabled,
            ["safe_mode"]            = runtimeControls.SafeModeEnabled
        };
        if (!string.IsNullOrWhiteSpace(segmentId))
            startDetails["segment_id"] = segmentId;

        // ── START event ──────────────────────────────────────────────
        _audit.Append(new AuditEvent
        {
            Actor  = "agent",
            Action = "MCP_TOOL_CALL_START",
            Target = canonical,
            Result = "pending",
            Details = startDetails
        });

        if (runtimeControls.SafeModeEnabled)
        {
            const string reason = "Safe mode is active.";
            LogEnd(requestId, canonical, "blocked", "not_required", null, 0, reason);
            return "Error: Tool call blocked — safe mode is active.";
        }

        if (runtimeControls.PanicModeEnabled && IsBlockedByPanic(canonical))
        {
            const string reason = "Blocked by panic mode.";
            LogEnd(requestId, canonical, "blocked", "not_required", null, 0, reason);
            return "Error: Tool call blocked — panic mode blocks side-effect tools.";
        }

        if (!TryConsumeBudget(runtimeControls.ToolBudgets, canonical, segmentId, out var budgetErrorJson))
        {
            _audit.Append(new AuditEvent
            {
                Actor = "agent",
                Action = "MCP_TOOL_BUDGET_EXCEEDED",
                Target = canonical,
                Result = "blocked",
                Details = new Dictionary<string, object>
                {
                    ["session_id"] = _sessionId,
                    ["request_id"] = requestId,
                    ["segment_id"] = segmentId ?? "",
                    ["budget_error"] = budgetErrorJson
                }
            });
            LogEnd(requestId, canonical, "blocked", "not_required", null, 0, "Tool budget exceeded");
            return budgetErrorJson;
        }

        // ── Permission gate ──────────────────────────────────────────
        ToolPermissionResult permission;
        try
        {
            permission = await _gate.CheckAsync(canonical, argumentsJson, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogEnd(requestId, canonical, "cancelled", null, null, 0, "Permission check cancelled");
            throw;
        }
        catch (Exception ex)
        {
            LogEnd(requestId, canonical, "error", null, null, 0, $"Permission gate error: {ex.Message}");
            return $"Error: Permission check failed — {ex.Message}";
        }

        if (!permission.Granted)
        {
            var reason = permission.DenialReason ?? "Denied by user";
            LogEnd(requestId, canonical, "blocked",
                permission.PermissionRequired ? "denied" : "not_required",
                null, 0, reason);
            return $"Error: Tool call blocked — {reason}";
        }

        // ── Execute the actual tool call ─────────────────────────────
        var sw = Stopwatch.StartNew();
        string output;
        try
        {
            output = await _inner.CallToolAsync(toolName, argumentsJson, cancellationToken);
            sw.Stop();
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            LogEnd(requestId, canonical, "cancelled",
                FormatPermissionStatus(permission),
                permission.TokenId, sw.ElapsedMilliseconds, "Cancelled");
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogEnd(requestId, canonical, "error",
                FormatPermissionStatus(permission),
                permission.TokenId, sw.ElapsedMilliseconds, ex.Message);
            return $"Error: Tool execution failed — {ex.Message}";
        }

        // ── END event (success) ──────────────────────────────────────
        var redactedOutput = ToolCallRedactor.RedactOutput(canonical, output);

        var endDetails = new Dictionary<string, object>
        {
            ["session_id"]      = _sessionId,
            ["request_id"]      = requestId,
            ["permission"]      = FormatPermissionStatus(permission),
            ["output_summary"]  = redactedOutput,
            ["duration_ms"]     = sw.ElapsedMilliseconds
        };
        if (!string.IsNullOrWhiteSpace(segmentId))
            endDetails["segment_id"] = segmentId;

        _audit.Append(new AuditEvent
        {
            Actor            = "agent",
            Action           = "MCP_TOOL_CALL_END",
            Target           = canonical,
            Result           = "ok",
            PermissionTokenId = permission.TokenId,
            Details = endDetails
        });

        return output;
    }

    /// <summary>
    /// Resets per-turn budget counters. Call this at the start of each
    /// user message to prevent budget from carrying over between turns
    /// when no segment ID is set.
    /// </summary>
    public void NotifyNewTurn()
    {
        lock (_budgetGate)
        {
            _activeTurnKey = $"turn:{Guid.NewGuid():N}";
            _turnToolCalls = 0;
            _turnWebCalls = 0;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        // Listing tools is a meta/read operation — no auditing needed
        return _inner.ListToolsAsync(cancellationToken);
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private void LogEnd(
        string requestId, string canonical, string result,
        string? permissionStatus, string? tokenId,
        long durationMs, string? errorMessage)
    {
        var details = new Dictionary<string, object>
        {
            ["session_id"]  = _sessionId,
            ["request_id"]  = requestId,
            ["duration_ms"] = durationMs
        };
        var segmentId = SegmentExecutionContext.CurrentSegmentId;
        if (!string.IsNullOrWhiteSpace(segmentId))
            details["segment_id"] = segmentId;

        if (permissionStatus is not null)
            details["permission"] = permissionStatus;
        if (errorMessage is not null)
            details["error_message"] = errorMessage;

        _audit.Append(new AuditEvent
        {
            Actor             = "agent",
            Action            = "MCP_TOOL_CALL_END",
            Target            = canonical,
            Result            = result,
            PermissionTokenId = tokenId,
            Details           = details
        });
    }

    /// <summary>
    /// Normalizes tool names to snake_case for consistent audit entries.
    /// MCP tools may appear as PascalCase or snake_case depending on
    /// the client; we canonicalize to snake_case for log consistency.
    /// </summary>
    public static string Canonicalize(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return "unknown";

        // Already snake_case
        if (toolName.Contains('_'))
            return toolName.ToLowerInvariant();

        // Convert PascalCase to snake_case
        var sb = new System.Text.StringBuilder(toolName.Length + 4);
        for (int i = 0; i < toolName.Length; i++)
        {
            var c = toolName[i];
            if (char.IsUpper(c) && i > 0)
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static bool IsBlockedByPanic(string canonicalToolName)
    {
        var group = ToolGroupPolicy.ResolveGroup(canonicalToolName);
        if (string.Equals(group, "unknown", StringComparison.OrdinalIgnoreCase))
            return true;

        return ToolGroupPolicy.SideEffectGroups.Contains(group);
    }

    private bool TryConsumeBudget(
        ToolBudgetSettings budgets,
        string canonicalToolName,
        string? segmentId,
        out string budgetErrorJson)
    {
        budgetErrorJson = "";
        if (!budgets.Enabled)
            return true;

        var normalized = budgets.Normalize();
        var group = ToolGroupPolicy.ResolveGroup(canonicalToolName);
        var turnKey = string.IsNullOrWhiteSpace(segmentId) ? "turn:default" : $"segment:{segmentId}";
        var now = DateTimeOffset.UtcNow;

        lock (_budgetGate)
        {
            if (!string.Equals(_activeTurnKey, turnKey, StringComparison.Ordinal))
            {
                _activeTurnKey = turnKey;
                _turnToolCalls = 0;
                _turnWebCalls = 0;
            }

            while (_fileOpsWindow.Count > 0 &&
                   now - _fileOpsWindow.Peek() > TimeSpan.FromMinutes(1))
            {
                _fileOpsWindow.Dequeue();
            }

            if (_sessionToolCalls >= normalized.MaxToolCallsPerSession)
            {
                budgetErrorJson = BuildBudgetErrorJson(
                    "max_tool_calls_per_session",
                    normalized.MaxToolCallsPerSession,
                    canonicalToolName);
                return false;
            }

            if (_turnToolCalls >= normalized.MaxToolCallsPerTurn)
            {
                budgetErrorJson = BuildBudgetErrorJson(
                    "max_tool_calls_per_turn",
                    normalized.MaxToolCallsPerTurn,
                    canonicalToolName);
                return false;
            }

            if (string.Equals(group, "web", StringComparison.OrdinalIgnoreCase) &&
                _turnWebCalls >= normalized.MaxWebPullsPerTurn)
            {
                budgetErrorJson = BuildBudgetErrorJson(
                    "max_web_pulls_per_turn",
                    normalized.MaxWebPullsPerTurn,
                    canonicalToolName);
                return false;
            }

            if (string.Equals(group, "files", StringComparison.OrdinalIgnoreCase) &&
                normalized.MaxFileOpsPerMinute >= 0 &&
                _fileOpsWindow.Count >= normalized.MaxFileOpsPerMinute)
            {
                budgetErrorJson = BuildBudgetErrorJson(
                    "max_file_ops_per_minute",
                    normalized.MaxFileOpsPerMinute,
                    canonicalToolName);
                return false;
            }

            _sessionToolCalls++;
            _turnToolCalls++;
            if (string.Equals(group, "web", StringComparison.OrdinalIgnoreCase))
                _turnWebCalls++;
            if (string.Equals(group, "files", StringComparison.OrdinalIgnoreCase))
                _fileOpsWindow.Enqueue(now);
        }

        return true;
    }

    private static string BuildBudgetErrorJson(string budgetName, int limit, string toolName)
    {
        return JsonSerializer.Serialize(new
        {
            error = "tool_budget_exceeded",
            budget = budgetName,
            limit,
            tool = toolName
        });
    }

    private static string FormatPermissionStatus(ToolPermissionResult p) =>
        p switch
        {
            { PermissionRequired: false }              => "not_required",
            { Granted: true, TokenId: not null }       => "granted",
            { Granted: true }                          => "granted",
            _                                          => "denied"
        };
}
