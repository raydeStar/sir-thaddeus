using SirThaddeus.Agent;
using SirThaddeus.AuditLog;

namespace SirThaddeus.Tests;

// ─────────────────────────────────────────────────────────────────────────
// AuditedMcpToolClient Tests
//
// Verifies the wrapper correctly:
//   - Writes START + END audit events for ok / error / blocked calls
//   - Applies redaction per tool type
//   - Delegates permission gating and blocks denied calls
//   - Caches and uses permission token IDs
// ─────────────────────────────────────────────────────────────────────────

public class AuditedMcpToolClientTests
{
    private const string SessionId = "test-session-01";

    // ─────────────────────────────────────────────────────────────────
    // Audit Events — Happy Path
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CallToolAsync_WritesStartAndEndEvents_OnSuccess()
    {
        var inner = new FakeMcpClient("tool output here");
        var audit = new TestAuditLogger();
        var gate  = new AlwaysGrantGate();
        var sut   = new AuditedMcpToolClient(inner, audit, gate, SessionId);

        await sut.CallToolAsync("WebSearch", "{\"query\":\"test\"}");

        var events = audit.Events;
        Assert.True(events.Count >= 2, "Should have at least START + END events");

        var start = events.First(e => e.Action == "MCP_TOOL_CALL_START");
        var end   = events.First(e => e.Action == "MCP_TOOL_CALL_END");

        Assert.Equal("agent", start.Actor);
        Assert.Equal("web_search", start.Target);   // Canonicalized
        Assert.Equal("pending", start.Result);
        Assert.Contains("session_id", start.Details!.Keys);
        Assert.Contains("request_id", start.Details!.Keys);

        Assert.Equal("agent", end.Actor);
        Assert.Equal("web_search", end.Target);
        Assert.Equal("ok", end.Result);
        Assert.Contains("duration_ms", end.Details!.Keys);
        Assert.Contains("output_summary", end.Details!.Keys);
    }

    [Fact]
    public async Task CallToolAsync_WritesStartAndEndEvents_OnToolError()
    {
        var inner = new ErrorThrowingMcpClient(new InvalidOperationException("MCP error: Boom"));
        var audit = new TestAuditLogger();
        var gate  = new AlwaysGrantGate();
        var sut   = new AuditedMcpToolClient(inner, audit, gate, SessionId);

        var result = await sut.CallToolAsync("file_read", "{\"path\":\"C:\\\\test.txt\"}");

        Assert.Contains("Tool execution failed", result);

        var end = audit.Events.Last(e => e.Action == "MCP_TOOL_CALL_END");
        Assert.Equal("error", end.Result);
        Assert.Contains("error_message", end.Details!.Keys);
    }

    // ─────────────────────────────────────────────────────────────────
    // Permission Gating — Blocked
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CallToolAsync_BlocksCall_WhenPermissionDenied()
    {
        var inner = new FakeMcpClient("should not reach this");
        var audit = new TestAuditLogger();
        var gate  = new AlwaysDenyGate("User said no");
        var sut   = new AuditedMcpToolClient(inner, audit, gate, SessionId);

        var result = await sut.CallToolAsync("ScreenCapture", "{}");

        Assert.Contains("Tool call blocked", result);
        Assert.Contains("User said no", result);

        var end = audit.Events.Last(e => e.Action == "MCP_TOOL_CALL_END");
        Assert.Equal("blocked", end.Result);

        // Inner client should NOT have been called
        Assert.Empty(inner.Calls);
    }

    // ─────────────────────────────────────────────────────────────────
    // Permission Token — Recorded in Audit
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CallToolAsync_RecordsTokenId_WhenGateGrantsWithToken()
    {
        var inner = new FakeMcpClient("ok");
        var audit = new TestAuditLogger();
        var gate  = new FixedTokenGate("tok-abc123");
        var sut   = new AuditedMcpToolClient(inner, audit, gate, SessionId);

        await sut.CallToolAsync("system_execute", "{\"command\":\"whoami\"}");

        var end = audit.Events.Last(e => e.Action == "MCP_TOOL_CALL_END");
        Assert.Equal("tok-abc123", end.PermissionTokenId);
        Assert.Equal("granted", end.Details!["permission"]);
    }

    // ─────────────────────────────────────────────────────────────────
    // Redaction — ScreenCapture
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CallToolAsync_RedactsScreenCaptureOutput()
    {
        var ocrText = "Lots of sensitive text visible on screen " + new string('x', 500);
        var inner   = new FakeMcpClient(ocrText);
        var audit   = new TestAuditLogger();
        var gate    = new AlwaysGrantGate();
        var sut     = new AuditedMcpToolClient(inner, audit, gate, SessionId);

        await sut.CallToolAsync("screen_capture", "{\"target\":\"full_screen\"}");

        var end = audit.Events.Last(e => e.Action == "MCP_TOOL_CALL_END");
        var outputSummary = end.Details!["output_summary"].ToString()!;

        // Must contain char count + hash, NOT the raw text
        Assert.Contains("OCR output:", outputSummary);
        Assert.Contains("sha256=", outputSummary);
        Assert.DoesNotContain("sensitive text", outputSummary);
    }

    // ─────────────────────────────────────────────────────────────────
    // Redaction — FileRead
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CallToolAsync_RedactsFileReadOutput()
    {
        var fileContent = "SECRET_API_KEY=abc123xyz\nDATABASE_PASSWORD=hunter2";
        var inner = new FakeMcpClient(fileContent);
        var audit = new TestAuditLogger();
        var gate  = new AlwaysGrantGate();
        var sut   = new AuditedMcpToolClient(inner, audit, gate, SessionId);

        await sut.CallToolAsync("FileRead", "{\"path\":\"C:\\\\secrets.env\"}");

        var end = audit.Events.Last(e => e.Action == "MCP_TOOL_CALL_END");
        var outputSummary = end.Details!["output_summary"].ToString()!;

        Assert.Contains("File content:", outputSummary);
        Assert.Contains("sha256=", outputSummary);
        Assert.DoesNotContain("SECRET_API_KEY", outputSummary);
        Assert.DoesNotContain("hunter2", outputSummary);
    }

    // ─────────────────────────────────────────────────────────────────
    // Canonicalize
    // ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("WebSearch", "web_search")]
    [InlineData("web_search", "web_search")]
    [InlineData("ScreenCapture", "screen_capture")]
    [InlineData("BrowserNavigate", "browser_navigate")]
    [InlineData("MemoryRetrieve", "memory_retrieve")]
    [InlineData("system_execute", "system_execute")]
    [InlineData("FileRead", "file_read")]
    [InlineData("GetActiveWindow", "get_active_window")]
    [InlineData("ToolPing", "tool_ping")]
    [InlineData("TimeNow", "time_now")]
    public void Canonicalize_ConvertsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, AuditedMcpToolClient.Canonicalize(input));
    }

    // ─────────────────────────────────────────────────────────────────
    // ListToolsAsync — delegates without audit
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListToolsAsync_DelegatesToInner_NoAuditEvents()
    {
        var tools = new List<McpToolInfo>
        {
            new() { Name = "web_search", Description = "Search", InputSchema = new { } }
        };
        var inner = new FakeMcpClient((_, _) => "", tools);
        var audit = new TestAuditLogger();
        var gate  = new AlwaysGrantGate();
        var sut   = new AuditedMcpToolClient(inner, audit, gate, SessionId);

        var result = await sut.ListToolsAsync();

        Assert.Single(result);
        Assert.Equal("web_search", result[0].Name);
        Assert.Empty(audit.Events);
    }

    // ═════════════════════════════════════════════════════════════════
    // Test Doubles
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fake MCP client that always throws on CallToolAsync.
    /// Used to verify error handling in the wrapper.
    /// </summary>
    private sealed class ErrorThrowingMcpClient : IMcpToolClient
    {
        private readonly Exception _ex;
        public ErrorThrowingMcpClient(Exception ex) => _ex = ex;

        public Task<string> CallToolAsync(
            string toolName, string argumentsJson, CancellationToken ct = default)
            => throw _ex;

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>([]);
    }

    /// <summary>
    /// Gate that grants with a specific token ID. For testing token
    /// propagation into audit events.
    /// </summary>
    private sealed class FixedTokenGate : IToolPermissionGate
    {
        private readonly string _tokenId;
        public FixedTokenGate(string tokenId) => _tokenId = tokenId;

        public Task<ToolPermissionResult> CheckAsync(
            string toolName, string argumentsJson, CancellationToken ct)
            => Task.FromResult(ToolPermissionResult.Grant(_tokenId));
    }

    /// <summary>Reused FakeMcpClient from orchestrator tests.</summary>
    private sealed class FakeMcpClient : IMcpToolClient
    {
        private readonly Func<string, string, string> _handler;
        private readonly IReadOnlyList<McpToolInfo> _tools;

        public List<(string Tool, string Args)> Calls { get; } = [];

        public FakeMcpClient(string returnValue)
            : this((_, _) => returnValue, []) { }

        public FakeMcpClient(
            Func<string, string, string> handler,
            IReadOnlyList<McpToolInfo>? tools = null)
        {
            _handler = handler;
            _tools   = tools ?? [];
        }

        public Task<string> CallToolAsync(
            string toolName, string argumentsJson, CancellationToken ct = default)
        {
            Calls.Add((toolName, argumentsJson));
            return Task.FromResult(_handler(toolName, argumentsJson));
        }

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
            => Task.FromResult(_tools);
    }
}
