using SirThaddeus.PermissionBroker;

namespace SirThaddeus.Invocation;

/// <summary>
/// Abstraction for prompting the user for permission.
/// Allows unit testing without UI dependencies.
/// </summary>
public interface IPermissionPrompter
{
    /// <summary>
    /// Prompts the user to approve or deny a permission request.
    /// </summary>
    /// <param name="request">The permission being requested.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user's decision.</returns>
    Task<PermissionDecision> PromptAsync(
        PermissionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents the user's decision on a permission request.
/// </summary>
public sealed record PermissionDecision
{
    /// <summary>
    /// Whether the user approved the request.
    /// </summary>
    public required bool Approved { get; init; }

    /// <summary>
    /// Reason for denial (if not approved).
    /// </summary>
    public string? DenialReason { get; init; }

    /// <summary>
    /// When true, the grant should be cached for the remainder of
    /// the current conversation session. Clears on "New Chat" or
    /// app restart. Default false (single-call approval).
    /// </summary>
    public bool RememberForSession { get; init; }

    /// <summary>
    /// When true, the group's policy should be persisted as "always"
    /// in settings.json so the user is never prompted again for this
    /// capability. The runtime is responsible for the actual save.
    /// </summary>
    public bool PersistAsAlways { get; init; }

    public static PermissionDecision AllowOnce()
        => new() { Approved = true };

    public static PermissionDecision AllowSession()
        => new() { Approved = true, RememberForSession = true };

    public static PermissionDecision AllowAlways()
        => new() { Approved = true, PersistAsAlways = true };

    public static PermissionDecision Deny(string reason)
        => new() { Approved = false, DenialReason = reason };

    // Backwards compatibility — existing callers use Allow()
    public static PermissionDecision Allow()
        => AllowOnce();
}

/// <summary>
/// A permission prompter that auto-approves all requests.
/// Useful for testing or headless scenarios with pre-approved scope.
/// </summary>
public sealed class AutoApprovePrompter : IPermissionPrompter
{
    public Task<PermissionDecision> PromptAsync(
        PermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(PermissionDecision.Allow());
    }
}

/// <summary>
/// A permission prompter that auto-denies all requests.
/// Useful for testing denial flows.
/// </summary>
public sealed class AutoDenyPrompter : IPermissionPrompter
{
    private readonly string _reason;

    public AutoDenyPrompter(string reason = "Auto-denied for testing")
    {
        _reason = reason;
    }

    public Task<PermissionDecision> PromptAsync(
        PermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(PermissionDecision.Deny(_reason));
    }
}
