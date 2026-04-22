using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Routing;

public class FootmanToolFilterTests
{
    private static ToolDefinition Def(string name) => new()
    {
        Function = new FunctionDefinition
        {
            Name = name,
            Description = $"{name} description",
            Parameters = new { type = "object" },
        },
    };

    private static readonly IReadOnlyList<ToolDefinition> Menu =
    [
        Def("web_search"),
        Def("browser_navigate"),
        Def("file_read"),
        Def("file_list"),
        Def("screen_capture"),
        Def("memory_retrieve"),
        Def("propose_automation"),
        Def("shiny_new_unregistered_tool"),
    ];

    private static RoutingDecision DecisionFor(AgentState state, double confidence = 0.9) => new()
    {
        SchemaVersion = 1,
        RequestId = "test",
        NextState = state,
        Confidence = confidence,
        Abstain = false,
        ReasonCode = "test",
    };

    [Fact]
    public void Abstain_ReturnsInputUnchanged()
    {
        var decision = RoutingDecision.CreateFallback("id", "low_conf");
        var result = FootmanToolFilter.Filter(Menu, decision);
        Assert.Same(Menu, result);
    }

    [Fact]
    public void LowConfidence_ReturnsInputUnchanged()
    {
        var decision = DecisionFor(AgentState.Chat, confidence: 0.30);
        var result = FootmanToolFilter.Filter(Menu, decision);
        Assert.Same(Menu, result);
    }

    [Fact]
    public void EmptyMenu_ReturnsEmpty()
    {
        var result = FootmanToolFilter.Filter(
            Array.Empty<ToolDefinition>(),
            DecisionFor(AgentState.SearchFact));
        Assert.Empty(result);
    }

    [Fact]
    public void Chat_DropsBrowserAndFileAndScreenTools()
    {
        // Chat state should only keep memory-read + meta; web/file/screen
        // should not be pitched to the primary model.
        var result = FootmanToolFilter.Filter(Menu, DecisionFor(AgentState.Chat));
        var names = result.Select(t => t.Function.Name).ToList();
        Assert.DoesNotContain("web_search", names);
        Assert.DoesNotContain("browser_navigate", names);
        Assert.DoesNotContain("file_read", names);
        Assert.DoesNotContain("screen_capture", names);
        Assert.Contains("memory_retrieve", names);
    }

    [Fact]
    public void SearchDeepDive_KeepsWebSearchAndBrowserNavigate()
    {
        var result = FootmanToolFilter.Filter(Menu, DecisionFor(AgentState.SearchDeepDive));
        var names = result.Select(t => t.Function.Name).ToList();
        Assert.Contains("web_search", names);
        Assert.Contains("browser_navigate", names);
    }

    [Fact]
    public void FileTask_KeepsFileToolsDropsScreenAndBrowser()
    {
        var result = FootmanToolFilter.Filter(Menu, DecisionFor(AgentState.FileTask));
        var names = result.Select(t => t.Function.Name).ToList();
        Assert.Contains("file_read", names);
        Assert.Contains("file_list", names);
        Assert.DoesNotContain("screen_capture", names);
        Assert.DoesNotContain("browser_navigate", names);
    }

    [Fact]
    public void ScreenObserve_KeepsScreenCaptureDropsOthers()
    {
        var result = FootmanToolFilter.Filter(Menu, DecisionFor(AgentState.ScreenObserve));
        var names = result.Select(t => t.Function.Name).ToList();
        Assert.Contains("screen_capture", names);
        Assert.DoesNotContain("web_search", names);
        Assert.DoesNotContain("file_read", names);
    }

    [Fact]
    public void UnregisteredTool_PassesThrough()
    {
        // A tool not in the capability registry stays in the list so a
        // freshly-shipped MCP tool doesn't get accidentally hidden by a
        // stale registry.
        var result = FootmanToolFilter.Filter(Menu, DecisionFor(AgentState.Chat));
        Assert.Contains(result, t => t.Function.Name == "shiny_new_unregistered_tool");
    }

    [Fact]
    public void AlwaysAllowList_ForcesToolThrough()
    {
        // propose_automation is a runtime virtual tool — no registry
        // capability — but the caller still wants it surfaced on chat turns.
        var result = FootmanToolFilter.Filter(
            Menu,
            DecisionFor(AgentState.Chat),
            alwaysAllowToolNames: new[] { "propose_automation" });
        Assert.Contains(result, t => t.Function.Name == "propose_automation");
    }

    [Fact]
    public void AllFiltered_FallsBackToFullMenu()
    {
        // Construct a menu where every tool's capability is outside the
        // Chat state's allowed families. Without the unregistered tool
        // escape hatch, the filter would return empty — fallback kicks in
        // and we get the whole menu instead.
        var narrowMenu = new[] { Def("web_search"), Def("browser_navigate") };
        var result = FootmanToolFilter.Filter(narrowMenu, DecisionFor(AgentState.Chat));
        Assert.Equal(narrowMenu.Length, result.Count);
    }
}
