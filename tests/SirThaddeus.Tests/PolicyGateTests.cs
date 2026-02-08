using SirThaddeus.Agent;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

// ─────────────────────────────────────────────────────────────────────────
// Policy Gate Tests
//
// Verifies that the deterministic policy table correctly maps intents
// to tool sets. These are the "type safety" tests — if an intent
// exposes a tool it shouldn't, these fail.
// ─────────────────────────────────────────────────────────────────────────

public class PolicyGateTests
{
    // ── Helper: build a set of tool definitions matching the standard set ─
    private static IReadOnlyList<ToolDefinition> AllTools =>
    [
        MakeTool("screen_capture"),
        MakeTool("get_active_window"),
        MakeTool("browser_navigate"),
        MakeTool("web_search"),
        MakeTool("WebSearch"),
        MakeTool("file_read"),
        MakeTool("file_list"),
        MakeTool("system_execute"),
        MakeTool("memory_store_facts"),
        MakeTool("memory_update_fact"),
        MakeTool("MemoryRetrieve"),
    ];

    private static ToolDefinition MakeTool(string name) => new()
    {
        Function = new FunctionDefinition
        {
            Name = name,
            Description = $"Test tool: {name}",
            Parameters = new { type = "object", properties = new { } }
        }
    };

    private static RouterOutput Route(string intent) => new() { Intent = intent };

    // ─────────────────────────────────────────────────────────────────
    // Chat Only
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ChatOnly_ExposesNoTools()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.ChatOnly));
        var filtered = PolicyGate.FilterTools(AllTools, policy);

        Assert.Empty(filtered);
        Assert.False(policy.UseToolLoop);
    }

    // ─────────────────────────────────────────────────────────────────
    // Lookup Search
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void LookupSearch_OnlyExposesSearchTools()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.LookupSearch));
        var filtered = PolicyGate.FilterTools(AllTools, policy);
        var names = filtered.Select(t => t.Function.Name).ToHashSet();

        Assert.Contains("web_search", names);
        Assert.DoesNotContain("screen_capture", names);
        Assert.DoesNotContain("system_execute", names);
        Assert.DoesNotContain("memory_store_facts", names);
        Assert.DoesNotContain("file_read", names);
    }

    // ─────────────────────────────────────────────────────────────────
    // Screen Observe
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ScreenObserve_OnlyExposesScreenTools()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.ScreenObserve));
        var filtered = PolicyGate.FilterTools(AllTools, policy);
        var names = filtered.Select(t => t.Function.Name).ToHashSet();

        Assert.Contains("screen_capture", names);
        Assert.Contains("get_active_window", names);
        Assert.DoesNotContain("web_search", names);
        Assert.DoesNotContain("system_execute", names);
        Assert.DoesNotContain("file_read", names);
        Assert.DoesNotContain("memory_store_facts", names);
    }

    // ─────────────────────────────────────────────────────────────────
    // File Task
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void FileTask_OnlyExposesFileTools()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.FileTask));
        var filtered = PolicyGate.FilterTools(AllTools, policy);
        var names = filtered.Select(t => t.Function.Name).ToHashSet();

        Assert.Contains("file_read", names);
        Assert.Contains("file_list", names);
        Assert.DoesNotContain("screen_capture", names);
        Assert.DoesNotContain("system_execute", names);
        Assert.DoesNotContain("web_search", names);
    }

    // ─────────────────────────────────────────────────────────────────
    // System Task
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SystemTask_OnlyExposesSystemExecute()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.SystemTask));
        var filtered = PolicyGate.FilterTools(AllTools, policy);
        var names = filtered.Select(t => t.Function.Name).ToHashSet();

        Assert.Single(filtered);
        Assert.Contains("system_execute", names);
    }

    // ─────────────────────────────────────────────────────────────────
    // Browse Once
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BrowseOnce_OnlyExposesBrowserNavigate()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.BrowseOnce));
        var filtered = PolicyGate.FilterTools(AllTools, policy);
        var names = filtered.Select(t => t.Function.Name).ToHashSet();

        Assert.Contains("browser_navigate", names);
        Assert.DoesNotContain("system_execute", names);
        Assert.DoesNotContain("screen_capture", names);
    }

    // ─────────────────────────────────────────────────────────────────
    // One-Shot Discovery
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void OneShotDiscovery_ExposesSearchAndBrowse()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.OneShotDiscovery));
        var filtered = PolicyGate.FilterTools(AllTools, policy);
        var names = filtered.Select(t => t.Function.Name).ToHashSet();

        Assert.Contains("web_search", names);
        Assert.Contains("browser_navigate", names);
        Assert.DoesNotContain("system_execute", names);
        Assert.DoesNotContain("screen_capture", names);
        Assert.DoesNotContain("memory_store_facts", names);
    }

    // ─────────────────────────────────────────────────────────────────
    // Memory Write
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void MemoryWrite_OnlyExposesMemoryTools()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.MemoryWrite));
        var filtered = PolicyGate.FilterTools(AllTools, policy);
        var names = filtered.Select(t => t.Function.Name).ToHashSet();

        Assert.Contains("memory_store_facts", names);
        Assert.Contains("memory_update_fact", names);
        Assert.DoesNotContain("screen_capture", names);
        Assert.DoesNotContain("web_search", names);
        Assert.DoesNotContain("system_execute", names);
        Assert.DoesNotContain("file_read", names);
    }

    // ─────────────────────────────────────────────────────────────────
    // Memory Read
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void MemoryRead_ExposesNoTools()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.MemoryRead));
        var filtered = PolicyGate.FilterTools(AllTools, policy);

        // Memory reads are handled via pre-fetch, not the tool loop
        Assert.Empty(filtered);
        Assert.False(policy.UseToolLoop);
    }

    // ─────────────────────────────────────────────────────────────────
    // General Tool (fallback)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void GeneralTool_ExposesAllTools()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.GeneralTool));
        var filtered = PolicyGate.FilterTools(AllTools, policy);

        // Wildcard — should pass everything through
        Assert.Equal(AllTools.Count, filtered.Count);
    }

    // ─────────────────────────────────────────────────────────────────
    // Unknown Intent (fallback to general_tool)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void UnknownIntent_FallsBackToGeneralTool()
    {
        var policy = PolicyGate.Evaluate(Route("banana_pancake"));
        var filtered = PolicyGate.FilterTools(AllTools, policy);

        // Unknown intent → general_tool → all tools
        Assert.Equal(AllTools.Count, filtered.Count);
    }

    // ─────────────────────────────────────────────────────────────────
    // Forbidden tools are excluded even with wildcard
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ForbiddenTools_ExcludedFromFiltering()
    {
        // ScreenObserve forbids system_execute, web_search, etc.
        var policy = PolicyGate.Evaluate(Route(Intents.ScreenObserve));
        var filtered = PolicyGate.FilterTools(AllTools, policy);

        foreach (var forbidden in policy.ForbiddenTools)
        {
            Assert.DoesNotContain(filtered,
                t => t.Function.Name.Equals(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Every known intent has a policy
    // ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Intents.ChatOnly)]
    [InlineData(Intents.LookupSearch)]
    [InlineData(Intents.BrowseOnce)]
    [InlineData(Intents.OneShotDiscovery)]
    [InlineData(Intents.ScreenObserve)]
    [InlineData(Intents.FileTask)]
    [InlineData(Intents.SystemTask)]
    [InlineData(Intents.MemoryRead)]
    [InlineData(Intents.MemoryWrite)]
    [InlineData(Intents.GeneralTool)]
    public void AllKnownIntents_HavePolicyEntries(string intent)
    {
        Assert.True(PolicyGate.HasPolicy(intent),
            $"Intent '{intent}' has no policy entry");

        // Evaluate should not throw
        var policy = PolicyGate.Evaluate(new RouterOutput { Intent = intent });
        Assert.NotNull(policy);
    }

    // ─────────────────────────────────────────────────────────────────
    // Empty tool list returns empty regardless of policy
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyToolList_ReturnsEmpty()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.GeneralTool));
        var filtered = PolicyGate.FilterTools([], policy);

        Assert.Empty(filtered);
    }

    // ─────────────────────────────────────────────────────────────────
    // No tool collisions across intents
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ScreenObserve_CannotCallSearch()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.ScreenObserve));
        var filtered = PolicyGate.FilterTools(AllTools, policy);

        Assert.DoesNotContain(filtered, t => t.Function.Name == "web_search");
    }

    [Fact]
    public void LookupSearch_CannotCallScreenCapture()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.LookupSearch));
        var filtered = PolicyGate.FilterTools(AllTools, policy);

        Assert.DoesNotContain(filtered, t => t.Function.Name == "screen_capture");
    }

    [Fact]
    public void MemoryWrite_CannotCallSystemExecute()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.MemoryWrite));
        var filtered = PolicyGate.FilterTools(AllTools, policy);

        Assert.DoesNotContain(filtered, t => t.Function.Name == "system_execute");
    }
}
