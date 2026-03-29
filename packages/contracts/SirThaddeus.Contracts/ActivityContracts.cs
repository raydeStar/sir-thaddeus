namespace SirThaddeus.Contracts;

// ── Activity Drawer Summary ──────────────────────────────────────────

/// <summary>
/// Complete activity summary for the trust-ledger drawer.
/// Returned by GET /api/activity/summary.
/// </summary>
public sealed record ActivitySummaryResponse(
    SessionSummaryDto Session,
    IReadOnlyList<ToolCategorySummaryDto> Categories,
    IReadOnlyList<McpConnectionSummaryDto> Connections);

/// <summary>
/// Session-level totals for the current conversation.
/// </summary>
public sealed record SessionSummaryDto(
    string SessionId,
    int TotalToolCalls,
    int ApprovedCalls,
    int DeniedCalls,
    int ErrorCalls,
    DateTimeOffset? FirstCallUtc,
    DateTimeOffset? LastCallUtc);

/// <summary>
/// Aggregated summary for one tool category (e.g. "Web searches", "File reads").
/// </summary>
public sealed record ToolCategorySummaryDto(
    string CategoryKey,
    string DisplayName,
    int TotalCalls,
    int SucceededCalls,
    int DeniedCalls,
    int ErrorCalls,
    DateTimeOffset? LastCallUtc,
    IReadOnlyList<ToolCallSummaryDto> RecentCalls);

/// <summary>
/// Individual tool call detail for the expanded view in the drawer.
/// </summary>
public sealed record ToolCallSummaryDto(
    string RequestId,
    string ToolName,
    string DisplayName,
    string InputSummary,
    string OutputSummary,
    string PermissionStatus,
    string ResultStatus,
    long DurationMs,
    DateTimeOffset TimestampUtc,
    string? ErrorMessage = null);

// ── MCP Connections ──────────────────────────────────────────────────

/// <summary>
/// Summary of a logical MCP connection/provider for the trust ledger.
/// </summary>
public sealed record McpConnectionSummaryDto(
    string ConnectionId,
    string DisplayName,
    string ApprovalState,
    string TransportType,
    int ToolCount,
    int TotalCalls,
    DateTimeOffset? LastCallUtc,
    IReadOnlyList<string> ToolNames);

/// <summary>
/// Request to change the approval state of an MCP connection.
/// </summary>
public sealed record ConnectionApprovalChangeRequest(
    string ConnectionId,
    string NewApprovalState);

/// <summary>
/// Response after changing connection approval state.
/// </summary>
public sealed record ConnectionApprovalChangeResponse(
    string ConnectionId,
    string ApprovalState,
    bool Applied);

/// <summary>
/// Known approval state values for MCP connections.
/// </summary>
public static class ConnectionApprovalStates
{
    public const string AlwaysAllow = "always_allow";
    public const string PerRequest = "per_request";
    public const string SessionAllow = "session_allow";
    public const string Revoked = "revoked";
    public const string Disabled = "disabled";
}
