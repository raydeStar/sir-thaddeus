using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace SirThaddeus.PermissionBroker;

/// <summary>
/// A time-boxed, revocable permission lease.
/// Tokens are single-purpose and must be presented for privileged operations.
/// </summary>
public sealed record PermissionToken
{
    /// <summary>
    /// Schema version for forward compatibility.
    /// </summary>
    [JsonPropertyName("token_version")]
    public string TokenVersion { get; init; } = "1.0";

    /// <summary>
    /// Unique identifier for this token.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// The principal (user/entity) that granted this permission.
    /// </summary>
    [JsonPropertyName("principal")]
    public string Principal { get; init; } = "user:local";

    /// <summary>
    /// When the token was issued.
    /// </summary>
    [JsonPropertyName("issued_at")]
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>
    /// When the token expires and becomes invalid.
    /// </summary>
    [JsonPropertyName("expires_at")]
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// The capability this token grants.
    /// </summary>
    [JsonPropertyName("capability")]
    public required Capability Capability { get; init; }

    /// <summary>
    /// The scope/constraints of the permission.
    /// </summary>
    [JsonPropertyName("scope")]
    public PermissionScope Scope { get; init; } = PermissionScope.Empty;

    /// <summary>
    /// Human-readable purpose for this permission (shown in UI and audit).
    /// </summary>
    [JsonPropertyName("purpose")]
    public required string Purpose { get; init; }

    /// <summary>
    /// Random nonce for uniqueness.
    /// </summary>
    [JsonPropertyName("nonce")]
    public required string Nonce { get; init; }

    /// <summary>
    /// Whether this token can be revoked before expiry.
    /// </summary>
    [JsonPropertyName("revocable")]
    public bool Revocable { get; init; } = true;

    /// <summary>
    /// Generates a cryptographically random token ID.
    /// </summary>
    public static string GenerateId()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Generates a cryptographically random nonce.
    /// </summary>
    public static string GenerateNonce()
    {
        var bytes = new byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Checks if this token is expired at the given time.
    /// </summary>
    public bool IsExpired(DateTimeOffset atTime) => atTime >= ExpiresAt;

    /// <summary>
    /// Gets the remaining duration until expiry.
    /// </summary>
    public TimeSpan RemainingDuration(DateTimeOffset atTime)
    {
        var remaining = ExpiresAt - atTime;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
