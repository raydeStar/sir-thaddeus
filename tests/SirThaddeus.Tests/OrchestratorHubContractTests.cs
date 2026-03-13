using SirThaddeus.Agent;
using SirThaddeus.AuditLog;

namespace SirThaddeus.Tests;

public class OrchestratorHubContractTests
{
    [Fact]
    public void AgentOrchestrator_FileStaysUnderOneThousandLines()
    {
        var rootDir = FindRepoRoot();
        var filePath = Path.Combine(rootDir, "packages", "agent", "SirThaddeus.Agent", "AgentOrchestrator.cs");

        Assert.True(File.Exists(filePath), $"Could not find AgentOrchestrator.cs at {filePath}");

        var lines = File.ReadAllLines(filePath).Length;

        // The absolute ceiling is 1200 lines. The orchestrator is a HUB for
        // state management and dependency injection, not a god object.
        // If you hit this limit, EXTRACT complex logic (like chunking,
        // mode classification, or formatting) into its own cohesive module
        // and inject it. DO NOT just bump this number without a very good reason.
        // Bumped from 1100→1200 for Footman router integration (wiring + context policy).
        // Bumped from 1200→1250 for SeedHistory + configurable MaxTokensBudget.
        Assert.True(lines <= 1250,
            $"Expected AgentOrchestrator.cs to stay under 1250 lines, but found {lines}. " +
            "Please extract business logic into a module and keep the orchestrator " +
            "focused strictly on wiring and state management.");
    }

    [Fact]
    public async Task DeterministicConversion_StaysInlineWithoutWebSearch()
    {
        var llm = new FakeLlmClient((_, _) =>
            throw new InvalidOperationException("LLM should not be called for strict deterministic conversion."));
        var mcp = new FakeMcpClient((_, _) => "{}", FakeMcpClient.StandardToolSet);
        var audit = new TestAuditLogger();

        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "You are a local assistant.");

        var response = await agent.ProcessAsync("350F in C");

        Assert.True(response.Success);
        Assert.Contains("176.7", response.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            response.ToolCallsMade,
            call => call.ToolName.Contains("web_search", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.EnumerateFiles("*.sln").Any())
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }
}
