using System.Text.Json;
using SirThaddeus.McpShared;

namespace SirThaddeus.Tests;

// ─────────────────────────────────────────────────────────────────────────
// MCP Shared Tests — Pure Helper Verification
//
// Tests for the cross-platform helpers in SirThaddeus.McpShared:
//   - ToolManifest: completeness, consistency, determinism
//   - TimeHelper: format correctness, boundary conditions
//
// These run on net8.0 without Windows dependencies.
// ─────────────────────────────────────────────────────────────────────────

public class ToolManifestTests
{
    [Fact]
    public void Manifest_ContainsAllExpectedTools()
    {
        var all = ToolManifest.All;

        // Verify minimum expected tool count (15 as of this writing)
        Assert.True(all.Count >= 15,
            $"Expected at least 15 tools in manifest, got {all.Count}");

        // Spot-check key tools exist
        Assert.Contains(all, t => t.Name == "web_search");
        Assert.Contains(all, t => t.Name == "screen_capture");
        Assert.Contains(all, t => t.Name == "system_execute");
        Assert.Contains(all, t => t.Name == "memory_retrieve");
        Assert.Contains(all, t => t.Name == "tool_ping");
        Assert.Contains(all, t => t.Name == "tool_list_capabilities");
        Assert.Contains(all, t => t.Name == "time_now");
        Assert.Contains(all, t => t.Name == "memory_list_facts");
        Assert.Contains(all, t => t.Name == "memory_delete_fact");
        Assert.Contains(all, t => t.Name == "weather_geocode");
        Assert.Contains(all, t => t.Name == "weather_forecast");
    }

    [Fact]
    public void Manifest_AllToolsHaveRequiredFields()
    {
        foreach (var tool in ToolManifest.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Name),
                "Tool name must not be empty");
            Assert.False(string.IsNullOrWhiteSpace(tool.Category),
                $"Tool {tool.Name} must have a category");
            Assert.False(string.IsNullOrWhiteSpace(tool.ReadWrite),
                $"Tool {tool.Name} must declare read/write");
            Assert.False(string.IsNullOrWhiteSpace(tool.Permission),
                $"Tool {tool.Name} must declare permission requirement");
            Assert.False(string.IsNullOrWhiteSpace(tool.Description),
                $"Tool {tool.Name} must have a description");
        }
    }

    [Fact]
    public void Manifest_NamesAreUnique()
    {
        var names = ToolManifest.All.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Manifest_AllNamesAreSnakeCase()
    {
        foreach (var tool in ToolManifest.All)
        {
            Assert.DoesNotContain(tool.Name, tool.Name.Where(char.IsUpper).Select(c => c.ToString()));
            Assert.True(tool.Name == tool.Name.ToLowerInvariant(),
                $"Tool name '{tool.Name}' should be snake_case");
        }
    }

    [Fact]
    public void Manifest_ToJson_IsDeterministic()
    {
        var json1 = ToolManifest.ToJson();
        var json2 = ToolManifest.ToJson();

        Assert.Equal(json1, json2);
        Assert.True(json1.Length > 100, "Manifest JSON should be non-trivial");
    }

    [Fact]
    public void Manifest_ToJson_IsValidJson()
    {
        var json = ToolManifest.ToJson();

        // Should parse without error
        var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.GetArrayLength() > 0);
    }

    [Fact]
    public void Manifest_PermissionRequiredTools_MatchExpectedSet()
    {
        var requiresPermission = ToolManifest.All
            .Where(t => t.Permission == "required")
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        // These tools must require permission per the guardrails
        Assert.Contains("screen_capture", requiresPermission);
        Assert.Contains("get_active_window", requiresPermission);
        Assert.Contains("file_read", requiresPermission);
        Assert.Contains("file_list", requiresPermission);
        Assert.Contains("system_execute", requiresPermission);
    }
}

public class TimeHelperTests
{
    [Fact]
    public void BuildTimePayload_ReturnsValidJson()
    {
        var now = new DateTimeOffset(2026, 2, 9, 10, 30, 0, TimeSpan.FromHours(-5));
        var json = TimeHelper.BuildTimePayload(now, "Eastern Standard Time");

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("iso", out var iso));
        Assert.True(root.TryGetProperty("unix_ms", out var unixMs));
        Assert.True(root.TryGetProperty("timezone", out var tz));
        Assert.True(root.TryGetProperty("offset", out var offset));

        Assert.Contains("2026-02-09", iso.GetString()!);
        Assert.True(unixMs.GetInt64() > 0);
        Assert.Equal("Eastern Standard Time", tz.GetString());
        Assert.Equal("-05:00", offset.GetString());
    }

    [Fact]
    public void BuildTimePayload_PositiveOffset_FormatsCorrectly()
    {
        var now = new DateTimeOffset(2026, 7, 15, 14, 0, 0, TimeSpan.FromHours(5.5));
        var json = TimeHelper.BuildTimePayload(now, "India Standard Time");

        var doc = JsonDocument.Parse(json);
        Assert.Equal("+05:30", doc.RootElement.GetProperty("offset").GetString());
        Assert.Equal("India Standard Time", doc.RootElement.GetProperty("timezone").GetString());
    }

    [Fact]
    public void BuildTimePayload_Utc_FormatsCorrectly()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var json = TimeHelper.BuildTimePayload(now, "UTC");

        var doc = JsonDocument.Parse(json);
        Assert.Equal("+00:00", doc.RootElement.GetProperty("offset").GetString());
        Assert.Equal("UTC", doc.RootElement.GetProperty("timezone").GetString());
    }

    [Fact]
    public void BuildTimePayload_UnixMs_MatchesExpected()
    {
        var epoch = new DateTimeOffset(2026, 2, 9, 15, 30, 0, TimeSpan.Zero);
        var expectedMs = epoch.ToUnixTimeMilliseconds();

        var json = TimeHelper.BuildTimePayload(epoch, "UTC");
        var doc = JsonDocument.Parse(json);

        Assert.Equal(expectedMs, doc.RootElement.GetProperty("unix_ms").GetInt64());
    }

    [Fact]
    public void BuildTimePayload_IsDeterministic()
    {
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.FromHours(-4));

        var json1 = TimeHelper.BuildTimePayload(now, "Eastern Daylight Time");
        var json2 = TimeHelper.BuildTimePayload(now, "Eastern Daylight Time");

        Assert.Equal(json1, json2);
    }
}
