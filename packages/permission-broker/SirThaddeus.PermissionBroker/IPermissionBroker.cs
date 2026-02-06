namespace SirThaddeus.PermissionBroker;

/// <summary>
/// Manages permission tokens: issuance, validation, and revocation.
/// </summary>
public interface IPermissionBroker
{
    /// <summary>
    /// Issues a new permission token for an approved request.
    /// </summary>
    /// <param name="request">The approved permission request.</param>
    /// <returns>The issued token.</returns>
    PermissionToken IssueToken(PermissionRequest request);

    /// <summary>
    /// Validates a token for a required capability.
    /// </summary>
    /// <param name="tokenId">The token ID to validate.</param>
    /// <param name="requiredCapability">The capability required for the operation.</param>
    /// <returns>Validation result with details.</returns>
    TokenValidationResult Validate(string tokenId, Capability requiredCapability);

    /// <summary>
    /// Revokes a specific token.
    /// </summary>
    /// <param name="tokenId">The token ID to revoke.</param>
    /// <param name="reason">Reason for revocation (for audit).</param>
    /// <returns>True if the token was found and revoked.</returns>
    bool Revoke(string tokenId, string reason);

    /// <summary>
    /// Revokes all outstanding tokens (for STOP ALL).
    /// </summary>
    /// <param name="reason">Reason for revocation (for audit).</param>
    /// <returns>Number of tokens revoked.</returns>
    int RevokeAll(string reason);

    /// <summary>
    /// Gets the count of currently valid (non-expired, non-revoked) tokens.
    /// </summary>
    int ActiveTokenCount { get; }

    /// <summary>
    /// Logs a permission denial (when user denies the request).
    /// </summary>
    /// <param name="request">The request that was denied.</param>
    /// <param name="reason">Reason for denial.</param>
    void LogDenial(PermissionRequest request, string reason);
}

/// <summary>
/// Result of token validation.
/// </summary>
public sealed record TokenValidationResult
{
    /// <summary>
    /// Whether the token is valid for the requested capability.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// The token if valid; null otherwise.
    /// </summary>
    public PermissionToken? Token { get; init; }

    /// <summary>
    /// Reason for invalidity, if applicable.
    /// </summary>
    public string? InvalidReason { get; init; }

    /// <summary>
    /// Creates a valid result.
    /// </summary>
    public static TokenValidationResult Valid(PermissionToken token) => new()
    {
        IsValid = true,
        Token = token
    };

    /// <summary>
    /// Creates an invalid result.
    /// </summary>
    public static TokenValidationResult Invalid(string reason) => new()
    {
        IsValid = false,
        InvalidReason = reason
    };
}
