using Microsoft.Extensions.Time.Testing;
using SirThaddeus.AuditLog;
using SirThaddeus.PermissionBroker;
using SirThaddeus.ToolRunner;
using SirThaddeus.ToolRunner.Tools;

namespace SirThaddeus.Tests;

/// <summary>
/// Tests for tool execution with permission enforcement.
/// </summary>
public class ToolRunnerTests : IDisposable
{
    private readonly string _testFilePath;
    private readonly JsonLineAuditLogger _auditLogger;
    private readonly FakeTimeProvider _timeProvider;
    private readonly InMemoryPermissionBroker _broker;
    private readonly EnforcingToolRunner _runner;

    public ToolRunnerTests()
    {
        _testFilePath = Path.Combine(
            Path.GetTempPath(),
            $"tool_runner_test_{Guid.NewGuid()}.jsonl");
        _auditLogger = new JsonLineAuditLogger(_testFilePath);
        _timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _broker = new InMemoryPermissionBroker(_auditLogger, _timeProvider);
        _runner = new EnforcingToolRunner(_broker, _auditLogger);

        // Register stub tools
        _runner.RegisterTool(new ScreenCaptureTool());
        _runner.RegisterTool(new BrowserNavigateTool());
        _runner.RegisterTool(new FileReadTool());
        _runner.RegisterTool(new SystemCommandTool());
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
    // Registration Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void RegisteredTools_ContainsAllRegisteredTools()
    {
        // Assert
        Assert.Contains("screen_capture", _runner.RegisteredTools);
        Assert.Contains("browser_navigate", _runner.RegisteredTools);
        Assert.Contains("file_read", _runner.RegisteredTools);
        Assert.Contains("system_execute", _runner.RegisteredTools);
    }

    [Fact]
    public void HasTool_ReturnsTrueForRegisteredTool()
    {
        // Assert
        Assert.True(_runner.HasTool("screen_capture"));
        Assert.True(_runner.HasTool("browser_navigate"));
    }

    [Fact]
    public void HasTool_ReturnsFalseForUnknownTool()
    {
        // Assert
        Assert.False(_runner.HasTool("nonexistent_tool"));
    }

    // ─────────────────────────────────────────────────────────────────
    // Permission Enforcement Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_WithoutToken_ReturnsPermissionDenied()
    {
        // Arrange
        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "screen_capture",
            Purpose = "Test capture",
            RequiredCapability = Capability.ScreenRead
        };

        // Act
        var result = await _runner.ExecuteAsync(call, tokenId: null);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Permission denied", result.Error);
        Assert.Contains("No permission token", result.Error);
    }

    [Fact]
    public async Task Execute_WithInvalidToken_ReturnsPermissionDenied()
    {
        // Arrange
        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "screen_capture",
            Purpose = "Test capture",
            RequiredCapability = Capability.ScreenRead
        };

        // Act
        var result = await _runner.ExecuteAsync(call, tokenId: "invalid-token-id");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Permission denied", result.Error);
    }

    [Fact]
    public async Task Execute_WithExpiredToken_ReturnsPermissionDenied()
    {
        // Arrange
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test",
            Duration = TimeSpan.FromSeconds(10)
        });

        // Expire the token
        _timeProvider.Advance(TimeSpan.FromSeconds(15));

        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "screen_capture",
            Purpose = "Test capture",
            RequiredCapability = Capability.ScreenRead
        };

        // Act
        var result = await _runner.ExecuteAsync(call, tokenId: token.Id);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Permission denied", result.Error);
        Assert.Contains("expired", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_WithRevokedToken_ReturnsPermissionDenied()
    {
        // Arrange
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test"
        });

        _broker.Revoke(token.Id, "Test revocation");

        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "screen_capture",
            Purpose = "Test capture",
            RequiredCapability = Capability.ScreenRead
        };

        // Act
        var result = await _runner.ExecuteAsync(call, tokenId: token.Id);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Permission denied", result.Error);
        Assert.Contains("revoked", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_WithWrongCapabilityToken_ReturnsPermissionDenied()
    {
        // Arrange - issue token for ScreenRead
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test"
        });

        // Try to use it for BrowserControl
        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "browser_navigate",
            Purpose = "Test navigation",
            RequiredCapability = Capability.BrowserControl,
            Arguments = new Dictionary<string, object> { ["url"] = "https://example.com" }
        };

        // Act
        var result = await _runner.ExecuteAsync(call, tokenId: token.Id);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Permission denied", result.Error);
        Assert.Contains("mismatch", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────
    // Successful Execution Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_WithValidToken_ReturnsSuccess()
    {
        // Arrange
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test screen capture"
        });

        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "screen_capture",
            Purpose = "Test capture",
            RequiredCapability = Capability.ScreenRead
        };

        // Act
        var result = await _runner.ExecuteAsync(call, tokenId: token.Id);

        // Assert
        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.NotNull(result.Output);
        Assert.Equal(token.Id, result.PermissionTokenId);
    }

    [Fact]
    public async Task Execute_RecordsDuration()
    {
        // Arrange
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test"
        });

        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "screen_capture",
            Purpose = "Test capture",
            RequiredCapability = Capability.ScreenRead
        };

        // Act
        var result = await _runner.ExecuteAsync(call, tokenId: token.Id);

        // Assert
        Assert.NotNull(result.DurationMs);
        Assert.True(result.DurationMs >= 0);
    }

    [Fact]
    public async Task Execute_BrowserNavigate_WithValidArgs_Succeeds()
    {
        // Arrange
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.BrowserControl,
            Purpose = "Test navigation"
        });

        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "browser_navigate",
            Purpose = "Navigate to example.com",
            RequiredCapability = Capability.BrowserControl,
            Arguments = new Dictionary<string, object>
            {
                ["url"] = "https://example.com"
            }
        };

        // Act
        var result = await _runner.ExecuteAsync(call, tokenId: token.Id);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Output);
    }

    // ─────────────────────────────────────────────────────────────────
    // Tool Error Handling Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_ToolNotFound_ReturnsFailure()
    {
        // Arrange
        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "nonexistent_tool",
            Purpose = "Test",
            RequiredCapability = Capability.ScreenRead
        };

        // Act - token doesn't matter since tool isn't found
        var result = await _runner.ExecuteAsync(call, tokenId: "any");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_ToolThrowsException_ReturnsFailure()
    {
        // Arrange
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.BrowserControl,
            Purpose = "Test"
        });

        // Invalid URL will cause exception
        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "browser_navigate",
            Purpose = "Navigate to invalid URL",
            RequiredCapability = Capability.BrowserControl,
            Arguments = new Dictionary<string, object>
            {
                ["url"] = "not-a-valid-url"
            }
        };

        // Act
        var result = await _runner.ExecuteAsync(call, tokenId: token.Id);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    // ─────────────────────────────────────────────────────────────────
    // Audit Logging Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_LogsSuccessfulExecution()
    {
        // Arrange
        var token = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test"
        });

        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "screen_capture",
            Purpose = "Test capture",
            RequiredCapability = Capability.ScreenRead
        };

        // Act
        await _runner.ExecuteAsync(call, tokenId: token.Id);

        // Assert
        var events = _auditLogger.ReadTail(10);
        var executeEvent = events.FirstOrDefault(e => e.Action == "TOOL_EXECUTE" && e.Result == "ok");
        
        Assert.NotNull(executeEvent);
        Assert.Equal("screen_capture", executeEvent.Target);
        Assert.Equal(token.Id, executeEvent.PermissionTokenId);
    }

    [Fact]
    public async Task Execute_LogsFailedExecution()
    {
        // Arrange
        var call = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "screen_capture",
            Purpose = "Test capture",
            RequiredCapability = Capability.ScreenRead
        };

        // Act - no token provided
        await _runner.ExecuteAsync(call, tokenId: null);

        // Assert
        var events = _auditLogger.ReadTail(10);
        var executeEvent = events.FirstOrDefault(e => e.Action == "TOOL_EXECUTE" && e.Result == "error");
        
        Assert.NotNull(executeEvent);
        Assert.Equal("screen_capture", executeEvent.Target);
        Assert.NotNull(executeEvent.Details);
        Assert.True(executeEvent.Details.ContainsKey("error"));
    }

    // ─────────────────────────────────────────────────────────────────
    // STOP ALL Integration Test
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_AfterRevokeAll_AllTokensInvalid()
    {
        // Arrange
        var token1 = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.ScreenRead,
            Purpose = "Test 1"
        });
        var token2 = _broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.BrowserControl,
            Purpose = "Test 2"
        });

        // Simulate STOP ALL
        _broker.RevokeAll("Emergency stop");

        var call1 = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "screen_capture",
            Purpose = "Test",
            RequiredCapability = Capability.ScreenRead
        };
        var call2 = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "browser_navigate",
            Purpose = "Test",
            RequiredCapability = Capability.BrowserControl,
            Arguments = new Dictionary<string, object> { ["url"] = "https://example.com" }
        };

        // Act
        var result1 = await _runner.ExecuteAsync(call1, tokenId: token1.Id);
        var result2 = await _runner.ExecuteAsync(call2, tokenId: token2.Id);

        // Assert
        Assert.False(result1.Success);
        Assert.False(result2.Success);
        Assert.Contains("revoked", result1.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("revoked", result2.Error, StringComparison.OrdinalIgnoreCase);
    }
}
