using System.Text.Json;
using SirThaddeus.McpServer.Tools;
using Thaddeus.SharedTypes;

namespace SirThaddeus.Tests.MCP;

[Collection(RuntimeEnvironmentVariableCollection.Name)]
public sealed class MetaToolsPolicyStateTests
{
    [Fact]
    public void PolicyGetState_ReadsCurrentRuntimeLimitsAndPermissions()
    {
        var currentRuntimeJson = JsonSerializer.Serialize(
            SettingsDocument.Defaults(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var settings = new TemporarySettingsFile(currentRuntimeJson);

        using var result = JsonDocument.Parse(MetaTools.PolicyGetState());
        var root = result.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        var budgets = root.GetProperty("budgets");
        Assert.True(budgets.GetProperty("enabled").GetBoolean());
        Assert.Equal(12, budgets.GetProperty("max_tool_calls_per_turn").GetInt32());
        Assert.Equal(200, budgets.GetProperty("max_tool_calls_per_session").GetInt32());
        Assert.Equal(12, budgets.GetProperty("max_web_pulls_per_turn").GetInt32());
        Assert.Equal(30, budgets.GetProperty("max_file_ops_per_minute").GetInt32());

        var groups = root.GetProperty("enabled_tool_groups");
        Assert.Equal("ask", groups.GetProperty("files").GetString());
        Assert.Equal("ask", groups.GetProperty("system").GetString());
        Assert.Equal("always", groups.GetProperty("memory_read").GetString());
    }

    [Fact]
    public void PolicyGetState_PreservesLegacyControlPlaneShape()
    {
        using var settings = new TemporarySettingsFile(
            """
            {
              "runtimeSafety": {
                "panicMode": true,
                "safeMode": true,
                "safeModeReason": "operator_test"
              },
              "toolBudgets": {
                "enabled": false,
                "maxToolCallsPerTurn": 7,
                "maxToolCallsPerSession": 80,
                "maxWebPullsPerTurn": 4,
                "maxFileOpsPerMinute": 9
              },
              "mcp": {
                "permissions": {
                  "screen": "off",
                  "files": "always",
                  "system": "ask",
                  "web": "off",
                  "memoryRead": "ask",
                  "memoryWrite": "off"
                }
              }
            }
            """);

        using var result = JsonDocument.Parse(MetaTools.PolicyGetState());
        var root = result.RootElement;

        Assert.True(root.GetProperty("panic_mode").GetBoolean());
        Assert.True(root.GetProperty("safe_mode").GetBoolean());
        Assert.Equal("operator_test", root.GetProperty("safe_mode_reason").GetString());
        var budgets = root.GetProperty("budgets");
        Assert.False(budgets.GetProperty("enabled").GetBoolean());
        Assert.Equal(7, budgets.GetProperty("max_tool_calls_per_turn").GetInt32());
        Assert.Equal(80, budgets.GetProperty("max_tool_calls_per_session").GetInt32());
        var groups = root.GetProperty("enabled_tool_groups");
        Assert.Equal("always", groups.GetProperty("files").GetString());
        Assert.Equal("off", groups.GetProperty("web").GetString());
    }

    private sealed class TemporarySettingsFile : IDisposable
    {
        private readonly string? _previous;
        private readonly string _directory;

        public TemporarySettingsFile(string json)
        {
            _previous = Environment.GetEnvironmentVariable("ST_SETTINGS_PATH");
            _directory = Path.Combine(Path.GetTempPath(), "SirThaddeus.MetaTools.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "settings.json");
            File.WriteAllText(path, json);
            Environment.SetEnvironmentVariable("ST_SETTINGS_PATH", path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ST_SETTINGS_PATH", _previous);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
