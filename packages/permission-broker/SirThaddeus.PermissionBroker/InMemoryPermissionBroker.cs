using System.Collections.Concurrent;
using SirThaddeus.AuditLog;

namespace SirThaddeus.PermissionBroker;

/// <summary>
/// In-memory implementation of the permission broker.
/// Uses TimeProvider for testable time handling.
/// </summary>
public sealed class InMemoryPermissionBroker : IPermissionBroker
{
    private readonly IAuditLogger _auditLogger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, TokenEntry> _tokens = new();

    /// <summary>
    /// Creates a new in-memory permission broker.
    /// </summary>
    /// <param name="auditLogger">Logger for permission events.</param>
    /// <param name="timeProvider">Time provider for expiry checking. Defaults to system time.</param>
    public InMemoryPermissionBroker(IAuditLogger auditLogger, TimeProvider? timeProvider = null)
    {
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public int ActiveTokenCount
    {
        get
        {
            var now = _timeProvider.GetUtcNow();
            return _tokens.Values.Count(e => !e.IsRevoked && !e.Token.IsExpired(now));
        }
    }

    /// <inheritdoc />
    public PermissionToken IssueToken(PermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _timeProvider.GetUtcNow();
        var token = new PermissionToken
        {
            Id = PermissionToken.GenerateId(),
            IssuedAt = now,
            ExpiresAt = now.Add(request.Duration),
            Capability = request.Capability,
            Scope = request.Scope,
            Purpose = request.Purpose,
            Nonce = PermissionToken.GenerateNonce()
        };

        _tokens[token.Id] = new TokenEntry(token, IsRevoked: false);

        _auditLogger.Append(new AuditEvent
        {
            Actor = request.Requester,
            Action = "PERMISSION_GRANTED",
            Target = request.Capability.ToString(),
            Result = "ok",
            PermissionTokenId = token.Id,
            Details = new Dictionary<string, object>
            {
                ["capability"] = request.Capability.ToString(),
                ["purpose"] = request.Purpose,
                ["scope"] = request.Scope.ToSummary(),
                ["expiresAt"] = token.ExpiresAt.ToString("O"),
                ["durationSeconds"] = request.Duration.TotalSeconds
            }
        });

        return token;
    }

    /// <inheritdoc />
    public TokenValidationResult Validate(string tokenId, Capability requiredCapability)
    {
        if (string.IsNullOrEmpty(tokenId))
            return TokenValidationResult.Invalid("Token ID is required");

        if (!_tokens.TryGetValue(tokenId, out var entry))
            return TokenValidationResult.Invalid("Token not found");

        if (entry.IsRevoked)
            return TokenValidationResult.Invalid("Token has been revoked");

        var now = _timeProvider.GetUtcNow();
        if (entry.Token.IsExpired(now))
            return TokenValidationResult.Invalid("Token has expired");

        if (entry.Token.Capability != requiredCapability)
            return TokenValidationResult.Invalid(
                $"Token capability mismatch: has {entry.Token.Capability}, requires {requiredCapability}");

        return TokenValidationResult.Valid(entry.Token);
    }

    /// <inheritdoc />
    public bool Revoke(string tokenId, string reason)
    {
        if (string.IsNullOrEmpty(tokenId))
            return false;

        if (!_tokens.TryGetValue(tokenId, out var entry))
            return false;

        if (entry.IsRevoked)
            return false;

        _tokens[tokenId] = entry with { IsRevoked = true };

        _auditLogger.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "PERMISSION_REVOKED",
            Target = entry.Token.Capability.ToString(),
            Result = "ok",
            PermissionTokenId = tokenId,
            Details = new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["capability"] = entry.Token.Capability.ToString(),
                ["purpose"] = entry.Token.Purpose
            }
        });

        return true;
    }

    /// <inheritdoc />
    public int RevokeAll(string reason)
    {
        var now = _timeProvider.GetUtcNow();
        var revokedCount = 0;

        foreach (var kvp in _tokens)
        {
            var entry = kvp.Value;
            if (!entry.IsRevoked && !entry.Token.IsExpired(now))
            {
                _tokens[kvp.Key] = entry with { IsRevoked = true };
                revokedCount++;
            }
        }

        if (revokedCount > 0)
        {
            _auditLogger.Append(new AuditEvent
            {
                Actor = "user",
                Action = "PERMISSION_REVOKE_ALL",
                Target = null,
                Result = "ok",
                Details = new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["tokensRevoked"] = revokedCount
                }
            });
        }

        return revokedCount;
    }

    /// <summary>
    /// Logs a permission denial (when user denies the request).
    /// </summary>
    public void LogDenial(PermissionRequest request, string reason)
    {
        _auditLogger.Append(new AuditEvent
        {
            Actor = "user",
            Action = "PERMISSION_DENIED",
            Target = request.Capability.ToString(),
            Result = "denied",
            Details = new Dictionary<string, object>
            {
                ["capability"] = request.Capability.ToString(),
                ["purpose"] = request.Purpose,
                ["reason"] = reason
            }
        });
    }

    private sealed record TokenEntry(PermissionToken Token, bool IsRevoked);
}
