using SirThaddeus.Agent;
using SirThaddeus.LlmClient;
using SirThaddeus.McpShared;

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
        MakeTool("weather_geocode"),
        MakeTool("weather_forecast"),
        MakeTool("resolve_timezone"),
        MakeTool("holidays_get"),
        MakeTool("holidays_next"),
        MakeTool("holidays_is_today"),
        MakeTool("feed_fetch"),
        MakeTool("status_check_url"),
        MakeTool("file_read"),
        MakeTool("file_list"),
        MakeTool("system_execute"),
        MakeTool("memory_retrieve"),
        MakeTool("memory_list_facts"),
        MakeTool("memory_delete_fact"),
        MakeTool("memory_store_facts"),
        MakeTool("memory_update_fact"),
        MakeTool("MemoryRetrieve"),
        MakeTool("tool_ping"),
        MakeTool("tool_list_capabilities"),
        MakeTool("time_now"),
        MakeTool("mystery_unmapped_tool")
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

    private static RouterOutput Route(string intent, bool needsWeb = false, bool needsSearch = false) => new()
    {
        Intent = intent,
        NeedsWeb = needsWeb,
        NeedsSearch = needsSearch
    };

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

    [Fact]
    public void UtilityDeterministic_ExposesNoTools_AndSkipsToolLoop()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.UtilityDeterministic));
        var filtered = PolicyGate.FilterTools(AllTools, policy);

        Assert.Empty(filtered);
        Assert.False(policy.UseToolLoop);
        Assert.Contains(ToolCapability.WebSearch, policy.ForbiddenCapabilities);
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
    public void GeneralTool_ExposesMinimalSafeFallbackSet()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.GeneralTool));
        var filtered = PolicyGate.FilterTools(AllTools, policy);
        var names = filtered.Select(t => t.Function.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("MemoryRetrieve", names);
        Assert.Contains("tool_ping", names);
        Assert.Contains("tool_list_capabilities", names);

        Assert.DoesNotContain("web_search", names);
        Assert.DoesNotContain("system_execute", names);
        Assert.DoesNotContain("screen_capture", names);
        Assert.DoesNotContain("file_read", names);
        Assert.DoesNotContain("memory_store_facts", names);
        Assert.DoesNotContain("time_now", names);
    }

    [Fact]
    public void GeneralTool_ConditionalWeb_IncludedOnlyWhenRouteRequestsCurrentInfo()
    {
        var policyWithoutWeb = PolicyGate.Evaluate(Route(Intents.GeneralTool, needsWeb: false, needsSearch: false));
        var filteredWithoutWeb = PolicyGate.FilterTools(AllTools, policyWithoutWeb);
        Assert.DoesNotContain(filteredWithoutWeb,
            t => t.Function.Name.Equals("web_search", StringComparison.OrdinalIgnoreCase));

        var policyWithWeb = PolicyGate.Evaluate(Route(Intents.GeneralTool, needsWeb: true, needsSearch: true));
        var filteredWithWeb = PolicyGate.FilterTools(AllTools, policyWithWeb);
        Assert.Contains(filteredWithWeb,
            t => t.Function.Name.Equals("web_search", StringComparison.OrdinalIgnoreCase));
    }

    // ─────────────────────────────────────────────────────────────────
    // Unknown Intent (fallback to general_tool)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void UnknownIntent_FallsBackToGeneralTool()
    {
        var policy = PolicyGate.Evaluate(Route("banana_pancake"));
        var filtered = PolicyGate.FilterTools(AllTools, policy);
        var names = filtered.Select(t => t.Function.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("MemoryRetrieve", names);
        Assert.Contains("tool_ping", names);
        Assert.DoesNotContain("system_execute", names);
        Assert.DoesNotContain("screen_capture", names);
        Assert.DoesNotContain("web_search", names);
    }

    // ─────────────────────────────────────────────────────────────────
    // Unmapped tools are hidden by default
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void UnmappedTools_AreHiddenByDefault()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.GeneralTool));
        var filtered = PolicyGate.FilterTools(AllTools, policy);

        Assert.DoesNotContain(filtered,
            t => t.Function.Name.Equals("mystery_unmapped_tool", StringComparison.OrdinalIgnoreCase));
    }

    // ─────────────────────────────────────────────────────────────────
    // Every known intent has a policy
    // ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Intents.ChatOnly)]
    [InlineData(Intents.UtilityDeterministic)]
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
    // Capability registry completeness
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CapabilityRegistry_MapsEveryManifestToolAndAlias()
    {
        var mappings = ToolCapabilityRegistry.GetMappings();

        foreach (var tool in ToolManifest.All)
        {
            Assert.True(mappings.ContainsKey(tool.Name),
                $"Tool '{tool.Name}' is missing from ToolCapabilityRegistry.");

            foreach (var alias in tool.Aliases)
            {
                Assert.True(mappings.ContainsKey(alias),
                    $"Alias '{alias}' for tool '{tool.Name}' is missing from ToolCapabilityRegistry.");
            }
        }
    }

    [Fact]
    public void ExposedTools_MapToExactlyOneCapability()
    {
        var policy = PolicyGate.Evaluate(Route(Intents.OneShotDiscovery));
        var filtered = PolicyGate.FilterTools(AllTools, policy);

        Assert.All(filtered, tool =>
        {
            var capability = ToolCapabilityRegistry.ResolveCapability(tool.Function.Name);
            Assert.True(capability.HasValue,
                $"Exposed tool '{tool.Function.Name}' was not mapped to a capability.");
        });
    }
}
