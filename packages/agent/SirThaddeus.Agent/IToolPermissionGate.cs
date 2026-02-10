namespace SirThaddeus.Agent;

// ─────────────────────────────────────────────────────────────────────────
// Tool Permission Gate — Runtime Callback Interface
//
// Thin abstraction for runtime-enforced permission gating. The agent
// package stays free of WPF/UI/PermissionBroker dependencies; the
// desktop runtime provides an implementation that bridges to the real
// IPermissionPrompter + IPermissionBroker infrastructure.
//
// Tests use simple stubs (AlwaysGrantGate, AlwaysDenyGate).
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Callback for runtime-enforced permission gating on MCP tool calls.
/// The runtime implements this with the actual prompter + broker;
/// tests use stubs. Keeps the agent package free of UI dependencies.
/// </summary>
public interface IToolPermissionGate
{
    /// <summary>
    /// Determines whether a tool call is permitted. If the tool requires
    /// explicit permission, the implementation should prompt the user
    /// (or apply a policy) and return the result.
    /// </summary>
    /// <param name="toolName">Canonical MCP tool name.</param>
    /// <param name="argumentsJson">JSON arguments (for display/audit).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Permission result with grant/deny status.</returns>
    Task<ToolPermissionResult> CheckAsync(
        string toolName, string argumentsJson, CancellationToken ct);
}

/// <summary>
/// Result of a tool permission gate check.
/// </summary>
public sealed record ToolPermissionResult
{
    /// <summary>Whether the call is allowed to proceed.</summary>
    public bool Granted { get; init; }

    /// <summary>Whether this tool even requires permission.</summary>
    public bool PermissionRequired { get; init; }

    /// <summary>Token ID if a permission token was issued.</summary>
    public string? TokenId { get; init; }

    /// <summary>Reason for denial (when <see cref="Granted"/> is false).</summary>
    public string? DenialReason { get; init; }

    /// <summary>Tool does not require permission — always allowed.</summary>
    public static ToolPermissionResult NotRequired() => new()
    {
        Granted = true, PermissionRequired = false
    };

    /// <summary>Permission was requested and granted.</summary>
    public static ToolPermissionResult Grant(string? tokenId = null) => new()
    {
        Granted = true, PermissionRequired = true, TokenId = tokenId
    };

    /// <summary>Permission was requested and denied.</summary>
    public static ToolPermissionResult Deny(string reason) => new()
    {
        Granted = false, PermissionRequired = true, DenialReason = reason
    };
}

// ─────────────────────────────────────────────────────────────────────────
// Test Stubs
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Gate that grants all tool calls without prompting.
/// For testing and scenarios where permissions are pre-approved.
/// </summary>
public sealed class AlwaysGrantGate : IToolPermissionGate
{
    public Task<ToolPermissionResult> CheckAsync(
        string toolName, string argumentsJson, CancellationToken ct)
        => Task.FromResult(ToolPermissionResult.NotRequired());
}

/// <summary>
/// Gate that denies all tool calls.
/// For testing denial flows and headless safe-default mode.
/// </summary>
public sealed class AlwaysDenyGate : IToolPermissionGate
{
    private readonly string _reason;

    public AlwaysDenyGate(string reason = "Auto-denied (headless/test)")
        => _reason = reason;

    public Task<ToolPermissionResult> CheckAsync(
        string toolName, string argumentsJson, CancellationToken ct)
        => Task.FromResult(ToolPermissionResult.Deny(_reason));
}
