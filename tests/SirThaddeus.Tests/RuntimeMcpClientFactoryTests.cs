using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.RuntimeHost;

namespace SirThaddeus.Tests;

public class RuntimeMcpClientFactoryTests
{
    [Fact]
    public async Task CreateAsync_ReturnsNoToolsClient_WhenToolsDisabled()
    {
        var audit = new RecordingAuditLogger();

        var result = await RuntimeMcpClientFactory.CreateAsync(
            enableTools: false,
            allowDegradedStartup: true,
            overrideServerPath: null,
            settings: new AppSettings(),
            audit,
            baseDirectory: Directory.GetCurrentDirectory(),
            clientName: "TestClient",
            clientVersion: "1.0.0");

        await using var scope = result.Scope;
        var tools = await result.Client.ListToolsAsync();

        Assert.False(result.ToolsAvailable);
        Assert.Equal("MCP tools are disabled.", result.Message);
        Assert.Empty(tools);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task CreateAsync_FallsBackToNoTools_WhenServerStartupFailsAndDegradedStartupAllowed()
    {
        var audit = new RecordingAuditLogger();
        var missingServerPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-mcp.exe");

        var result = await RuntimeMcpClientFactory.CreateAsync(
            enableTools: true,
            allowDegradedStartup: true,
            overrideServerPath: missingServerPath,
            settings: new AppSettings(),
            audit,
            baseDirectory: Directory.GetCurrentDirectory(),
            clientName: "TestClient",
            clientVersion: "1.0.0");

        await using var scope = result.Scope;
        var tools = await result.Client.ListToolsAsync();

        Assert.False(result.ToolsAvailable);
        Assert.Contains("continuing without tools", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(tools);

        var evt = Assert.Single(audit.Events);
        Assert.Equal("runtime", evt.Actor);
        Assert.Equal("MCP_STARTUP_DEGRADED", evt.Action);
        Assert.Equal("warning", evt.Result);
        Assert.Equal(Path.GetFullPath(missingServerPath), evt.Target);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenServerStartupFailsAndDegradedStartupDisallowed()
    {
        var audit = new RecordingAuditLogger();
        var missingServerPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-mcp.exe");

        await Assert.ThrowsAnyAsync<Exception>(() => RuntimeMcpClientFactory.CreateAsync(
            enableTools: true,
            allowDegradedStartup: false,
            overrideServerPath: missingServerPath,
            settings: new AppSettings(),
            audit,
            baseDirectory: Directory.GetCurrentDirectory(),
            clientName: "TestClient",
            clientVersion: "1.0.0"));

        Assert.Empty(audit.Events);
    }

    private sealed class RecordingAuditLogger : IAuditLogger
    {
        public List<AuditEvent> Events { get; } = [];

        public void Append(AuditEvent auditEvent) => Events.Add(auditEvent);

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public IReadOnlyList<AuditEvent> ReadTail(int maxEvents)
            => Events.TakeLast(Math.Max(0, maxEvents)).ToArray();

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int maxEvents, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AuditEvent>>(ReadTail(maxEvents));
    }
}
