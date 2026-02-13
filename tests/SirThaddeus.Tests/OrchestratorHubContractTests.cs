using SirThaddeus.Agent;
using SirThaddeus.AuditLog;

namespace SirThaddeus.Tests;

public class OrchestratorHubContractTests
{
    [Fact]
    public void AgentOrchestrator_FileStaysUnderOneThousandLines()
    {
        var repoRoot = FindRepoRoot();
        var file = Path.Combine(
            repoRoot,
            "packages",
            "agent",
            "SirThaddeus.Agent",
            "AgentOrchestrator.cs");

        var lineCount = File.ReadLines(file).Count();
        Assert.True(
            lineCount < 1000,
            $"Expected AgentOrchestrator.cs to stay under 1000 lines, but found {lineCount}.");
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
