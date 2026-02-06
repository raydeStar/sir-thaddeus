using Microsoft.Extensions.Time.Testing;
using SirThaddeus.AuditLog;
using SirThaddeus.PermissionBroker;

namespace SirThaddeus.Tests;

/// <summary>
/// Tests for permission token issuance, validation, and revocation.
/// </summary>
public class PermissionBrokerTests : IDisposable
{
    private readonly string _testFilePath;
    private readonly JsonLineAuditLogger _auditLogger;
    private readonly FakeTimeProvider _timeProvider;
    private readonly InMemoryPermissionBroker _broker;

    public PermissionBrokerTests()
    {
        _testFilePath = Path.Combine(
            Path.GetTempPath(),
            $"permission_broker_test_{Guid.NewGuid()}.jsonl");
        _auditLogger = new JsonLineAuditLogger(_testFilePath);
        _timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _broker = new InMemoryPermissionBroker(_auditLogger, _timeProvider);
    }

    public void Dispose()
    {
        _auditLogger.Dispose();
        if (File.Exists(_testFilePath))
        {
            File.Delete(_testFilePath);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Token Issuance Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void IssueToken_CreatesValidToken()
    {
        // Arrange
        var request = new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test screen capture",
            Duration = TimeSpan.FromSeconds(30)
        };

        // Act
        var token = _broker.IssueToken(request);

        // Assert
        Assert.NotNull(token);
        Assert.NotEmpty(token.Id);
        Assert.NotEmpty(token.Nonce);
        Assert.Equal(Capability.ScreenRead, token.Capability);
        Assert.Equal("Test screen capture", token.Purpose);
        Assert.True(token.Revocable);
    }

    [Fact]
    public void IssueToken_SetsCorrectExpiry()
    {
        // Arrange
        var duration = TimeSpan.FromMinutes(5);
        var request = new PermissionRequest
        {
            Capability = Capability.BrowserControl,
            Purpose = "Test browser control",
            Duration = duration
        };

        // Act
        var token = _broker.IssueToken(request);

        // Assert
        var expectedExpiry = _timeProvider.GetUtcNow() + duration;
        Assert.Equal(expectedExpiry, token.ExpiresAt);
    }

    [Fact]
    public void IssueToken_IncrementsActiveTokenCount()
    {
        // Arrange
        Assert.Equal(0, _broker.ActiveTokenCount);

        // Act
        _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.Microphone,
            Purpose = "Test"
        });

        // Assert
        Assert.Equal(1, _broker.ActiveTokenCount);
    }

    [Fact]
    public void IssueToken_LogsGrantEvent()
    {
        // Act
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.FileAccess,
            Purpose = "Test file access"
        });

        // Assert
        var events = _auditLogger.ReadTail(10);
        var grantEvent = events.FirstOrDefault(e => e.Action == "PERMISSION_GRANTED");
        
        Assert.NotNull(grantEvent);
        Assert.Equal(token.Id, grantEvent.PermissionTokenId);
        Assert.Equal("FileAccess", grantEvent.Details?["capability"]?.ToString());
    }

    // ─────────────────────────────────────────────────────────────────
    // Token Validation Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_ValidToken_ReturnsSuccess()
    {
        // Arrange
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test"
        });

        // Act
        var result = _broker.Validate(token.Id, Capability.ScreenRead);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(token.Id, result.Token?.Id);
    }

    [Fact]
    public void Validate_NonexistentToken_ReturnsInvalid()
    {
        // Act
        var result = _broker.Validate("nonexistent-token-id", Capability.ScreenRead);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("not found", result.InvalidReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_WrongCapability_ReturnsInvalid()
    {
        // Arrange
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test"
        });

        // Act
        var result = _broker.Validate(token.Id, Capability.Microphone);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("capability mismatch", result.InvalidReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ExpiredToken_ReturnsInvalid()
    {
        // Arrange
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test",
            Duration = TimeSpan.FromSeconds(30)
        });

        // Fast-forward time past expiry
        _timeProvider.Advance(TimeSpan.FromSeconds(31));

        // Act
        var result = _broker.Validate(token.Id, Capability.ScreenRead);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("expired", result.InvalidReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RevokedToken_ReturnsInvalid()
    {
        // Arrange
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test"
        });
        _broker.Revoke(token.Id, "User requested revocation");

        // Act
        var result = _broker.Validate(token.Id, Capability.ScreenRead);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("revoked", result.InvalidReason, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────
    // Token Revocation Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Revoke_ValidToken_ReturnsTrue()
    {
        // Arrange
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test"
        });

        // Act
        var result = _broker.Revoke(token.Id, "Test revocation");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void Revoke_NonexistentToken_ReturnsFalse()
    {
        // Act
        var result = _broker.Revoke("nonexistent-token-id", "Test");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Revoke_DecrementsActiveTokenCount()
    {
        // Arrange
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test"
        });
        Assert.Equal(1, _broker.ActiveTokenCount);

        // Act
        _broker.Revoke(token.Id, "Test");

        // Assert
        Assert.Equal(0, _broker.ActiveTokenCount);
    }

    [Fact]
    public void Revoke_LogsRevokeEvent()
    {
        // Arrange
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test"
        });

        // Act
        _broker.Revoke(token.Id, "User canceled");

        // Assert
        var events = _auditLogger.ReadTail(10);
        var revokeEvent = events.FirstOrDefault(e => e.Action == "PERMISSION_REVOKED");
        
        Assert.NotNull(revokeEvent);
        Assert.Equal(token.Id, revokeEvent.PermissionTokenId);
        Assert.Equal("User canceled", revokeEvent.Details?["reason"]?.ToString());
    }

    // ─────────────────────────────────────────────────────────────────
    // RevokeAll (STOP ALL) Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void RevokeAll_RevokesAllActiveTokens()
    {
        // Arrange
        _broker.IssueToken(new PermissionRequest { Capability = Capability.ScreenRead, Purpose = "Test 1" });
        _broker.IssueToken(new PermissionRequest { Capability = Capability.Microphone, Purpose = "Test 2" });
        _broker.IssueToken(new PermissionRequest { Capability = Capability.BrowserControl, Purpose = "Test 3" });
        Assert.Equal(3, _broker.ActiveTokenCount);

        // Act
        var revokedCount = _broker.RevokeAll("STOP ALL triggered");

        // Assert
        Assert.Equal(3, revokedCount);
        Assert.Equal(0, _broker.ActiveTokenCount);
    }

    [Fact]
    public void RevokeAll_ReturnsZeroWhenNoTokens()
    {
        // Act
        var revokedCount = _broker.RevokeAll("STOP ALL");

        // Assert
        Assert.Equal(0, revokedCount);
    }

    [Fact]
    public void RevokeAll_LogsRevokeAllEvent()
    {
        // Arrange
        _broker.IssueToken(new PermissionRequest { Capability = Capability.ScreenRead, Purpose = "Test 1" });
        _broker.IssueToken(new PermissionRequest { Capability = Capability.Microphone, Purpose = "Test 2" });

        // Act
        _broker.RevokeAll("Emergency stop");

        // Assert
        var events = _auditLogger.ReadTail(10);
        var revokeAllEvent = events.FirstOrDefault(e => e.Action == "PERMISSION_REVOKE_ALL");
        
        Assert.NotNull(revokeAllEvent);
        Assert.Equal("Emergency stop", revokeAllEvent.Details?["reason"]?.ToString());
        Assert.Equal("2", revokeAllEvent.Details?["tokensRevoked"]?.ToString());
    }

    [Fact]
    public void RevokeAll_MakesAllTokensInvalid()
    {
        // Arrange
        var token1 = _broker.IssueToken(new PermissionRequest { Capability = Capability.ScreenRead, Purpose = "Test 1" });
        var token2 = _broker.IssueToken(new PermissionRequest { Capability = Capability.Microphone, Purpose = "Test 2" });

        // Act
        _broker.RevokeAll("STOP ALL");

        // Assert
        Assert.False(_broker.Validate(token1.Id, Capability.ScreenRead).IsValid);
        Assert.False(_broker.Validate(token2.Id, Capability.Microphone).IsValid);
    }

    // ─────────────────────────────────────────────────────────────────
    // Token Expiry with TimeProvider Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Token_ExpiresAfterDuration()
    {
        // Arrange
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test",
            Duration = TimeSpan.FromMinutes(1)
        });

        // Act - advance time by 30 seconds (still valid)
        _timeProvider.Advance(TimeSpan.FromSeconds(30));
        var midResult = _broker.Validate(token.Id, Capability.ScreenRead);

        // Advance past expiry
        _timeProvider.Advance(TimeSpan.FromSeconds(31));
        var expiredResult = _broker.Validate(token.Id, Capability.ScreenRead);

        // Assert
        Assert.True(midResult.IsValid);
        Assert.False(expiredResult.IsValid);
    }

    [Fact]
    public void Token_RemainingDuration_CalculatesCorrectly()
    {
        // Arrange
        var now = _timeProvider.GetUtcNow();
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test",
            Duration = TimeSpan.FromMinutes(5)
        });

        // Act - advance 2 minutes
        _timeProvider.Advance(TimeSpan.FromMinutes(2));
        var remaining = token.RemainingDuration(_timeProvider.GetUtcNow());

        // Assert - should be about 3 minutes remaining
        Assert.True(remaining > TimeSpan.FromMinutes(2.9));
        Assert.True(remaining <= TimeSpan.FromMinutes(3));
    }

    // ─────────────────────────────────────────────────────────────────
    // Denial Logging Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void LogDenial_WritesAuditEvent()
    {
        // Arrange
        var request = new PermissionRequest
        {
            Capability = Capability.SystemExecute,
            Purpose = "Execute dangerous command"
        };

        // Act
        _broker.LogDenial(request, "User declined permission");

        // Assert
        var events = _auditLogger.ReadTail(10);
        var denialEvent = events.FirstOrDefault(e => e.Action == "PERMISSION_DENIED");
        
        Assert.NotNull(denialEvent);
        Assert.Equal("denied", denialEvent.Result);
        Assert.Equal("SystemExecute", denialEvent.Details?["capability"]?.ToString());
        Assert.Equal("User declined permission", denialEvent.Details?["reason"]?.ToString());
    }

    // ─────────────────────────────────────────────────────────────────
    // Scope Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void IssueToken_PreservesScope()
    {
        // Arrange
        var scope = new PermissionScope
        {
            Window = "active",
            MaxFrames = 3,
            AllowedDomain = "example.com"
        };
        var request = new PermissionRequest
        {
            Capability = Capability.BrowserControl,
            Purpose = "Test with scope",
            Scope = scope
        };

        // Act
        var token = _broker.IssueToken(request);

        // Assert
        Assert.Equal("active", token.Scope.Window);
        Assert.Equal(3, token.Scope.MaxFrames);
        Assert.Equal("example.com", token.Scope.AllowedDomain);
    }

    [Fact]
    public void PermissionScope_ActiveWindow_HasCorrectDefaults()
    {
        // Act
        var scope = PermissionScope.ActiveWindow;

        // Assert
        Assert.Equal("active", scope.Window);
        Assert.Equal(1, scope.MaxFrames);
    }

    [Fact]
    public void PermissionScope_ToSummary_GeneratesReadableString()
    {
        // Arrange
        var scope = new PermissionScope
        {
            Window = "active",
            AllowedDomain = "example.com",
            PathPrefix = "/api/"
        };

        // Act
        var summary = scope.ToSummary();

        // Assert
        Assert.Contains("active", summary);
        Assert.Contains("example.com", summary);
        Assert.Contains("/api/", summary);
    }

    // ─────────────────────────────────────────────────────────────────
    // Capability Extension Tests
    // ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Capability.ScreenRead, "Screen Reading")]
    [InlineData(Capability.BrowserControl, "Browser Control")]
    [InlineData(Capability.Microphone, "Microphone Access")]
    [InlineData(Capability.SystemExecute, "System Command Execution")]
    [InlineData(Capability.FileAccess, "File System Access")]
    public void Capability_ToDisplayName_ReturnsHumanReadable(Capability capability, string expected)
    {
        // Act
        var displayName = capability.ToDisplayName();

        // Assert
        Assert.Equal(expected, displayName);
    }

    [Fact]
    public void Capability_ToDescription_ReturnsNonEmpty()
    {
        // Act & Assert
        foreach (Capability cap in Enum.GetValues<Capability>())
        {
            var description = cap.ToDescription();
            Assert.False(string.IsNullOrWhiteSpace(description));
        }
    }
}
