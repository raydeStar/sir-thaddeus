using System.Text.Json;
using SirThaddeus.Contracts;
using SirThaddeus.McpShared;

namespace SirThaddeus.Tests;

public class ActivityContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ── Contract Serialization ───────────────────────────────────────

    [Fact]
    public void ActivitySummaryResponse_RoundTrips_ViaJson()
    {
        var response = new ActivitySummaryResponse(
            Session: new SessionSummaryDto(
                SessionId: "test-session",
                TotalToolCalls: 5,
                ApprovedCalls: 4,
                DeniedCalls: 1,
                ErrorCalls: 0,
                FirstCallUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
                LastCallUtc: DateTimeOffset.UtcNow),
            Categories:
            [
                new ToolCategorySummaryDto(
                    CategoryKey: "web",
                    DisplayName: "Web searches",
                    TotalCalls: 3,
                    SucceededCalls: 3,
                    DeniedCalls: 0,
                    ErrorCalls: 0,
                    LastCallUtc: DateTimeOffset.UtcNow,
                    RecentCalls:
                    [
                        new ToolCallSummaryDto(
                            RequestId: "abc123",
                            ToolName: "web_search",
                            DisplayName: "Web Search",
                            InputSummary: "weather in seattle",
                            OutputSummary: "[search: 5 result(s) returned]",
                            PermissionStatus: "policy_always",
                            ResultStatus: "success",
                            DurationMs: 1200,
                            TimestampUtc: DateTimeOffset.UtcNow)
                    ])
            ],
            Connections:
            [
                new McpConnectionSummaryDto(
                    ConnectionId: "web",
                    DisplayName: "Web Access",
                    ApprovalState: ConnectionApprovalStates.AlwaysAllow,
                    TransportType: "embedded_stdio",
                    ToolCount: 12,
                    TotalCalls: 3,
                    LastCallUtc: DateTimeOffset.UtcNow,
                    ToolNames: ["web_search", "browser_navigate"])
            ]);

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ActivitySummaryResponse>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("test-session", deserialized.Session.SessionId);
        Assert.Equal(5, deserialized.Session.TotalToolCalls);
        Assert.Single(deserialized.Categories);
        Assert.Equal("web", deserialized.Categories[0].CategoryKey);
        Assert.Single(deserialized.Categories[0].RecentCalls);
        Assert.Single(deserialized.Connections);
        Assert.Equal(ConnectionApprovalStates.AlwaysAllow, deserialized.Connections[0].ApprovalState);
    }

    [Fact]
    public void EmptySession_SerializesCorrectly()
    {
        var response = new ActivitySummaryResponse(
            Session: new SessionSummaryDto("empty", 0, 0, 0, 0, null, null),
            Categories: [],
            Connections: []);

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ActivitySummaryResponse>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(0, deserialized.Session.TotalToolCalls);
        Assert.Null(deserialized.Session.FirstCallUtc);
        Assert.Empty(deserialized.Categories);
        Assert.Empty(deserialized.Connections);
    }

    [Fact]
    public void ConnectionApprovalChangeRequest_RoundTrips()
    {
        var request = new ConnectionApprovalChangeRequest("web", ConnectionApprovalStates.Revoked);
        var json = JsonSerializer.Serialize(request, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ConnectionApprovalChangeRequest>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("web", deserialized.ConnectionId);
        Assert.Equal(ConnectionApprovalStates.Revoked, deserialized.NewApprovalState);
    }

    // ── Tool Manifest Category Mappings ──────────────────────────────

    [Theory]
    [InlineData("web", "Web searches")]
    [InlineData("file", "File operations")]
    [InlineData("system", "System commands")]
    [InlineData("screen", "Screen capture")]
    [InlineData("memory", "Memory")]
    public void GetCategoryDisplayName_ReturnsExpected(string category, string expected)
    {
        Assert.Equal(expected, ToolManifest.GetCategoryDisplayName(category));
    }

    [Fact]
    public void GetCategoryDisplayName_FallsBackForUnknown()
    {
        Assert.Equal("custom_cat", ToolManifest.GetCategoryDisplayName("custom_cat"));
    }

    [Theory]
    [InlineData("screen", "Screen Capture")]
    [InlineData("files", "File System")]
    [InlineData("system", "System Commands")]
    [InlineData("web", "Web Access")]
    [InlineData("memoryRead", "Memory (Read)")]
    [InlineData("memoryWrite", "Memory (Write)")]
    public void GetConnectionDisplayName_ReturnsExpected(string group, string expected)
    {
        Assert.Equal(expected, ToolManifest.GetConnectionDisplayName(group));
    }

    [Fact]
    public void GetToolsByCategory_GroupsCorrectly()
    {
        var groups = ToolManifest.GetToolsByCategory();

        Assert.True(groups.ContainsKey("web"));
        Assert.True(groups.ContainsKey("file"));
        Assert.True(groups.ContainsKey("system"));
        Assert.True(groups.ContainsKey("screen"));
        Assert.True(groups.ContainsKey("memory"));
        Assert.True(groups.ContainsKey("meta"));
        Assert.True(groups.ContainsKey("time"));

        Assert.Contains(groups["web"], t => t.Name == "web_search");
        Assert.Contains(groups["file"], t => t.Name == "file_read");
        Assert.Contains(groups["system"], t => t.Name == "system_execute");
    }

    [Fact]
    public void PermissionGroupToCategories_CoversAllGroups()
    {
        var mapping = ToolManifest.PermissionGroupToCategories;

        Assert.True(mapping.ContainsKey("screen"));
        Assert.True(mapping.ContainsKey("files"));
        Assert.True(mapping.ContainsKey("system"));
        Assert.True(mapping.ContainsKey("web"));
        Assert.True(mapping.ContainsKey("memoryRead"));
        Assert.True(mapping.ContainsKey("memoryWrite"));

        Assert.Contains("file", mapping["files"]);
        Assert.Contains("web", mapping["web"]);
    }

    // ── Approval State Constants ─────────────────────────────────────

    [Fact]
    public void ConnectionApprovalStates_AreDistinct()
    {
        var states = new[]
        {
            ConnectionApprovalStates.AlwaysAllow,
            ConnectionApprovalStates.PerRequest,
            ConnectionApprovalStates.SessionAllow,
            ConnectionApprovalStates.Revoked,
            ConnectionApprovalStates.Disabled
        };

        Assert.Equal(states.Length, states.Distinct().Count());
    }
}
