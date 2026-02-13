using SirThaddeus.Agent;
using SirThaddeus.Agent.Memory;
using SirThaddeus.AuditLog;

namespace SirThaddeus.Tests;

public class MemoryContextProviderTests
{
    [Fact]
    public async Task GetContextAsync_ReturnsTypedContextAndProvenance_OnSuccess()
    {
        var payload = """
                      {
                        "facts": 2,
                        "events": 1,
                        "chunks": 3,
                        "nuggets": 1,
                        "hasProfile": true,
                        "onboardingNeeded": false,
                        "packText": "[PROFILE] Name: Mark"
                      }
                      """;

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "memory_retrieve" => payload,
            _ => throw new InvalidOperationException("unexpected tool")
        });
        var audit = new TestAuditLogger();
        var provider = new MemoryContextProvider(mcp, audit, TimeProvider.System);

        var result = await provider.GetContextAsync(new MemoryContextRequest
        {
            UserMessage = "hey there",
            MemoryEnabled = true,
            IsColdGreeting = true,
            ActiveProfileId = "user-1",
            Timeout = TimeSpan.FromMilliseconds(500)
        });

        Assert.Equal("[PROFILE] Name: Mark", result.PackText);
        Assert.False(result.OnboardingNeeded);
        Assert.Null(result.Error);

        Assert.Equal("memory_retrieve", result.Provenance.SourceTool);
        Assert.Equal("greet", result.Provenance.RetrievalMode);
        Assert.True(result.Provenance.Success);
        Assert.False(result.Provenance.TimedOut);
        Assert.Equal(2, result.Provenance.Facts);
        Assert.Equal(1, result.Provenance.Events);
        Assert.Equal(3, result.Provenance.Chunks);
        Assert.Equal(1, result.Provenance.Nuggets);
        Assert.True(result.Provenance.HasProfile);
        Assert.Contains("[PROFILE]", result.Provenance.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetContextAsync_ReturnsTimeoutProvenance_WhenCallExceedsTimeout()
    {
        var mcp = new SlowMcpClient(delayMs: 250);
        var audit = new TestAuditLogger();
        var provider = new MemoryContextProvider(mcp, audit, TimeProvider.System);

        var result = await provider.GetContextAsync(new MemoryContextRequest
        {
            UserMessage = "hello",
            MemoryEnabled = true,
            Timeout = TimeSpan.FromMilliseconds(20)
        });

        Assert.True(result.Provenance.TimedOut);
        Assert.False(result.Provenance.Success);
        Assert.Equal("Timeout — skipped.", result.Provenance.Summary);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task GetContextAsync_SkipsWhenMemoryDisabled()
    {
        var mcp = new FakeMcpClient("should-not-run");
        var audit = new TestAuditLogger();
        var provider = new MemoryContextProvider(mcp, audit, TimeProvider.System);

        var result = await provider.GetContextAsync(new MemoryContextRequest
        {
            UserMessage = "hello",
            MemoryEnabled = false
        });

        Assert.True(result.Provenance.Skipped);
        Assert.False(result.Provenance.Success);
        Assert.Equal("Memory disabled — skipped.", result.Provenance.Summary);
        Assert.Empty(mcp.Calls);
    }

    private sealed class SlowMcpClient : IMcpToolClient
    {
        private readonly int _delayMs;

        public SlowMcpClient(int delayMs)
        {
            _delayMs = delayMs;
        }

        public async Task<string> CallToolAsync(
            string toolName,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delayMs, cancellationToken);
            return """{"packText":"late"}""";
        }

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>([]);
    }
}

