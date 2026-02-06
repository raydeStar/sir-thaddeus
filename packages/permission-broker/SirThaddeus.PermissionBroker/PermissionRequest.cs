namespace SirThaddeus.PermissionBroker;

/// <summary>
/// A request for permission that the user must approve or deny.
/// </summary>
public sealed record PermissionRequest
{
    /// <summary>
    /// The capability being requested.
    /// </summary>
    public required Capability Capability { get; init; }

    /// <summary>
    /// Human-readable purpose for this permission.
    /// </summary>
    public required string Purpose { get; init; }

    /// <summary>
    /// The scope/constraints of the requested permission.
    /// </summary>
    public PermissionScope Scope { get; init; } = PermissionScope.Empty;

    /// <summary>
    /// Requested duration for the token.
    /// </summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The actor making the request (for audit purposes).
    /// </summary>
    public string Requester { get; init; } = "runtime";
}

/// <summary>
/// The result of a permission request.
/// </summary>
public sealed record PermissionResult
{
    /// <summary>
    /// Whether the permission was granted.
    /// </summary>
    public required bool Granted { get; init; }

    /// <summary>
    /// The token if granted; null if denied.
    /// </summary>
    public PermissionToken? Token { get; init; }

    /// <summary>
    /// Reason for denial, if applicable.
    /// </summary>
    public string? DenialReason { get; init; }

    /// <summary>
    /// Creates a granted result with the given token.
    /// </summary>
    public static PermissionResult Grant(PermissionToken token) => new()
    {
        Granted = true,
        Token = token
    };

    /// <summary>
    /// Creates a denied result with the given reason.
    /// </summary>
    public static PermissionResult Deny(string reason) => new()
    {
        Granted = false,
        DenialReason = reason
    };
}
