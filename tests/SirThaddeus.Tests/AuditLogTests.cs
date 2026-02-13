using System.Text.Json;
using SirThaddeus.AuditLog;

namespace SirThaddeus.Tests;

/// <summary>
/// Tests for the JSONL audit logger.
/// </summary>
public class AuditLogTests : IDisposable
{
    private readonly string _testFilePath;
    private readonly JsonLineAuditLogger _logger;

    public AuditLogTests()
    {
        // Use a unique temp file for each test run
        _testFilePath = Path.Combine(
            Path.GetTempPath(), 
            $"meaningful_copilot_test_{Guid.NewGuid()}.jsonl");
        _logger = new JsonLineAuditLogger(_testFilePath);
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (File.Exists(_testFilePath))
        {
            File.Delete(_testFilePath);
        }
    }

    [Fact]
    public void Append_CreatesFileIfNotExists()
    {
        // Arrange - file shouldn't exist yet
        Assert.False(File.Exists(_testFilePath));

        // Act
        _logger.Append(new AuditEvent
        {
            Actor = "test",
            Action = "TEST_ACTION"
        });

        // Assert
        Assert.True(File.Exists(_testFilePath));
    }

    [Fact]
    public void Append_WritesNewlineDelimitedJson()
    {
        // Arrange & Act
        _logger.Append(new AuditEvent { Actor = "test", Action = "ACTION_1" });
        _logger.Append(new AuditEvent { Actor = "test", Action = "ACTION_2" });

        // Assert
        var lines = File.ReadAllLines(_testFilePath);
        Assert.Equal(2, lines.Length);
        
        // Each line should be valid JSON
        foreach (var line in lines)
        {
            var doc = JsonDocument.Parse(line);
            Assert.NotNull(doc);
        }
    }

    [Fact]
    public void ReadTail_ReturnsEmptyListWhenFileDoesNotExist()
    {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid()}.jsonl");
        using var logger = new JsonLineAuditLogger(nonExistentPath);

        // Act
        var events = logger.ReadTail(10);

        // Assert
        Assert.Empty(events);
    }

    [Fact]
    public void ReadTail_ReturnsNewestEventsInChronologicalOrder()
    {
        // Arrange - write 15 events
        for (int i = 1; i <= 15; i++)
        {
            _logger.Append(new AuditEvent 
            { 
                Actor = "test", 
                Action = $"ACTION_{i}" 
            });
        }

        // Act - read last 10
        var events = _logger.ReadTail(10);

        // Assert
        Assert.Equal(10, events.Count);
        
        // Should be events 6-15 (the newest 10)
        Assert.Equal("ACTION_6", events[0].Action);
        Assert.Equal("ACTION_15", events[9].Action);
    }

    [Fact]
    public void ReadTail_ReturnsAllEventsWhenFewerThanRequested()
    {
        // Arrange - write only 3 events
        for (int i = 1; i <= 3; i++)
        {
            _logger.Append(new AuditEvent 
            { 
                Actor = "test", 
                Action = $"ACTION_{i}" 
            });
        }

        // Act - request 10
        var events = _logger.ReadTail(10);

        // Assert
        Assert.Equal(3, events.Count);
    }

    [Fact]
    public void ReadTail_SkipsMalformedLines()
    {
        // Arrange - write valid event, then corrupt the file, then write another
        _logger.Append(new AuditEvent { Actor = "test", Action = "VALID_1" });
        
        // Manually append malformed JSON
        File.AppendAllText(_testFilePath, "{ this is not valid json }\n");
        
        _logger.Append(new AuditEvent { Actor = "test", Action = "VALID_2" });

        // Act
        var events = _logger.ReadTail(10);

        // Assert - should have 2 valid events, malformed one skipped
        Assert.Equal(2, events.Count);
        Assert.Equal("VALID_1", events[0].Action);
        Assert.Equal("VALID_2", events[1].Action);
    }

    [Fact]
    public void AuditEvent_SerializesWithCorrectPropertyNames()
    {
        // Arrange
        var evt = new AuditEvent
        {
            Actor = "runtime",
            Action = "TEST_ACTION",
            Target = "test_target",
            Result = "ok",
            Details = new Dictionary<string, object> { ["key"] = "value" }
        };

        // Act
        _logger.Append(evt);
        var json = File.ReadAllText(_testFilePath);

        // Assert - check snake_case property names
        Assert.Contains("\"actor\":", json);
        Assert.Contains("\"action\":", json);
        Assert.Contains("\"target\":", json);
        Assert.Contains("\"result\":", json);
        Assert.Contains("\"event_version\":", json);
        Assert.Contains("\"ts\":", json);
    }

    [Fact]
    public void Append_RedactsSensitiveJsonKeysBeforePersist()
    {
        _logger.Append(new AuditEvent
        {
            Actor = "test",
            Action = "SCRUB_KEYS",
            Details = new Dictionary<string, object>
            {
                ["input_summary"] = """{"query":"weather","api_key":"live-key-123","nested":{"password":"hunter2"}}""",
                ["authorization"] = "Bearer top-secret-token"
            }
        });

        var json = File.ReadAllText(_testFilePath);

        Assert.DoesNotContain("live-key-123", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret-token", json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
        Assert.Contains("weather", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Append_RedactsJwtLikeValuesBeforePersist()
    {
        var jwtLike = "abcde12345.fghij67890.klmno12345";
        _logger.Append(new AuditEvent
        {
            Actor = "test",
            Action = "SCRUB_JWT",
            Details = new Dictionary<string, object>
            {
                ["note"] = $"authorization token: {jwtLike}"
            }
        });

        var json = File.ReadAllText(_testFilePath);
        Assert.DoesNotContain(jwtLike, json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_JWT]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_RedactsLongRandomSecretLikeValuesBeforePersist()
    {
        var randomLike = "Qz3f9Lk2Pn8Vx4Mb7Hd1Rw6Ty0Ua5Se8Jm2Nc9Gp4Df7Hk1Lq";
        _logger.Append(new AuditEvent
        {
            Actor = "test",
            Action = "SCRUB_LONG_RANDOM",
            Details = new Dictionary<string, object>
            {
                ["note"] = $"candidate={randomLike}"
            }
        });

        var json = File.ReadAllText(_testFilePath);
        Assert.DoesNotContain(randomLike, json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_SECRET]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void GetDefaultPath_ReturnsPathUnderLocalAppData()
    {
        // Act
        var path = JsonLineAuditLogger.GetDefaultPath();

        // Assert
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(localAppData, path);
        Assert.Contains("SirThaddeus", path);
        Assert.EndsWith(".jsonl", path);
    }

    [Fact]
    public void AuditEvent_PermissionTokenId_SerializesWhenPresent()
    {
        // Arrange
        var evt = new AuditEvent
        {
            Actor = "runtime",
            Action = "TOOL_EXECUTE",
            PermissionTokenId = "perm_abc123"
        };

        // Act
        _logger.Append(evt);
        var json = File.ReadAllText(_testFilePath);

        // Assert
        Assert.Contains("\"permission_token_id\":", json);
        Assert.Contains("perm_abc123", json);
    }

    [Fact]
    public void AuditEvent_PermissionTokenId_OmittedWhenNull()
    {
        // Arrange
        var evt = new AuditEvent
        {
            Actor = "runtime",
            Action = "STATE_CHANGE"
            // No PermissionTokenId set
        };

        // Act
        _logger.Append(evt);
        var json = File.ReadAllText(_testFilePath);

        // Assert - should not contain permission_token_id at all
        Assert.DoesNotContain("permission_token_id", json);
    }
}
