using SirThaddeus.Agent;
using SirThaddeus.Agent.Routing;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class RoutingAccuracyTests
{
    [Fact]
    public async Task LookupRoute_HighConfidence_StillRunsFootmanArbitration()
    {
        var router = new FixedRouter(new RouterOutput
        {
            Intent = Intents.LookupFact,
            Confidence = 0.96,
            NeedsWeb = true,
            NeedsSearch = true,
            RequiredCapabilities = [ToolCapability.WebSearch]
        });

        var footman = new FixedFootmanRouter(new RoutingDecision
        {
            NextState = AgentState.Chat,
            ContextPolicy = ContextPolicy.ChatSessionSnapshot,
            Confidence = 0.9,
            Abstain = false,
            ReasonCode = "test_override"
        });

        var llm = new FakeLlmClient((messages, tools) => new LlmResponse
        {
            IsComplete = true,
            Content = "No web lookup needed.",
            FinishReason = "stop"
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "web_search" or "WebSearch" => "should not be called",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            router: router,
            footmanRouter: footman);

        var result = await agent.ProcessAsync("can you tell me a joke about databases?");

        Assert.True(footman.Called);
        Assert.True(result.Success);
        Assert.DoesNotContain(mcp.Calls, c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChatOnly_UncertaintyLanguage_DoesNotAutoEscalateToSearch()
    {
        var llm = new FakeLlmClient((messages, tools) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("Classify", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = "chat",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "It might help to take a short walk.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "web_search" or "WebSearch" => "should not be called",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("Hey, what do you think I should do tonight?");

        Assert.True(result.Success);
        Assert.Contains("might help", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mcp.Calls, c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Footman_IsBypassed_ForStrongDeepDiveSignal()
    {
        var router = new FixedRouter(new RouterOutput
        {
            Intent = Intents.LookupDeepDive,
            Confidence = 0.96,
            NeedsWeb = true,
            NeedsSearch = true,
            NeedsBrowserAutomation = true,
            RequiredCapabilities = [ToolCapability.WebSearch, ToolCapability.BrowserNavigate]
        });

        var footman = new FixedFootmanRouter(new RoutingDecision
        {
            NextState = AgentState.Chat,
            ContextPolicy = ContextPolicy.ChatSessionSnapshot,
            Confidence = 0.92,
            Abstain = false,
            ReasonCode = "test_chat_downgrade"
        });

        var llm = new FakeLlmClient((messages, tools) => new LlmResponse
        {
            IsComplete = true,
            Content = "Briefing ready.",
            FinishReason = "stop"
        });

        var placesPayload = """
            {
              "place": {
                "name": "Seattle Flowers",
                "address": "100 Pike St, Seattle, WA",
                "openNow": true,
                "weekdayText": ["Mon: 9:00 AM - 6:00 PM"]
              },
              "sources": [
                {
                  "name": "Google Places",
                  "url": "https://example.com/seattle-flowers",
                  "fetchedIso": "2026-03-03T00:00:00Z"
                }
              ]
            }
            """;

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "places_lookup" or "PlacesLookup" => placesPayload,
                "web_search" or "WebSearch" => "No results found for \"test\".",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            router: router,
            footmanRouter: footman);

        var result = await agent.ProcessAsync("Deep dive Seattle Flowers with hours + reviews and what to expect.");

        Assert.False(footman.Called);
        Assert.True(result.Success);
        Assert.Contains(mcp.Calls, c =>
            c.Tool.Equals("places_lookup", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("PlacesLookup", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(audit.GetByAction("FOOTMAN_DOWNGRADE_BLOCKED"));
    }

    [Fact]
    public async Task ChatRoute_WithStrongDeepDiveSignal_IsUpgradedByLookupFloor()
    {
        var router = new FixedRouter(new RouterOutput
        {
            Intent = Intents.ChatOnly,
            Confidence = 0.98,
            NeedsWeb = false,
            NeedsSearch = false,
            RequiredCapabilities = []
        });

        var llm = new FakeLlmClient((messages, tools) => new LlmResponse
        {
            IsComplete = true,
            Content = "Briefing ready.",
            FinishReason = "stop"
        });

        var placesPayload = """
            {
              "place": {
                "name": "Seattle Flowers",
                "address": "100 Pike St, Seattle, WA",
                "openNow": true,
                "weekdayText": ["Mon: 9:00 AM - 6:00 PM"]
              },
              "sources": [
                {
                  "name": "Google Places",
                  "url": "https://example.com/seattle-flowers",
                  "fetchedIso": "2026-03-03T00:00:00Z"
                }
              ]
            }
            """;

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "places_lookup" or "PlacesLookup" => placesPayload,
                "web_search" or "WebSearch" => "No results found for \"test\".",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            router: router);

        var result = await agent.ProcessAsync("Deep dive Seattle Flowers with hours + reviews and what to expect.");

        Assert.True(result.Success);
        Assert.Contains(mcp.Calls, c =>
            c.Tool.Equals("places_lookup", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("PlacesLookup", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(audit.GetByAction("LOOKUP_FLOOR_UPGRADE"));
    }

    [Fact]
    public async Task ChatRoute_WithStrongFactLookupSignal_IsUpgradedByLookupFloor_AndCallsWebSearch()
    {
        var router = new FixedRouter(new RouterOutput
        {
            Intent = Intents.ChatOnly,
            Confidence = 0.98,
            NeedsWeb = false,
            NeedsSearch = false,
            RequiredCapabilities = []
        });

        var llm = new FakeLlmClient((messages, tools) => new LlmResponse
        {
            IsComplete = true,
            Content = "Overview\nCommon Points\nDifferences\nPractical Takeaway",
            FinishReason = "stop"
        });

        var webSearchPayload =
            "1. .NET Aspire update article\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/aspire-updates\",\"title\":\".NET Aspire updates\"}]";

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "web_search" or "WebSearch" => webSearchPayload,
                "browser_navigate" or "BrowserNavigate" => "Article body content.",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            router: router);

        var result = await agent.ProcessAsync(
            "Search for recent updates and developments in .NET Aspire from the last year. " +
            "Synthesize information from multiple sources, compare what overlaps and what differs. " +
            "Provide a structured response with: Overview, Common Points, Differences, Practical Takeaway.");

        Assert.True(result.Success);
        Assert.Contains(mcp.Calls, c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(audit.GetByAction("LOOKUP_FLOOR_UPGRADE"));
    }

    [Fact]
    public async Task ChatRoute_SelfPreferencePrompt_DoesNotUpgradeToLookupFloor()
    {
        var router = new FixedRouter(new RouterOutput
        {
            Intent = Intents.ChatOnly,
            Confidence = 0.98,
            NeedsWeb = false,
            NeedsSearch = false,
            RequiredCapabilities = []
        });

        var llm = new FakeLlmClient((messages, tools) => new LlmResponse
        {
            IsComplete = true,
            Content = "I help with practical problem-solving and clear next steps.",
            FinishReason = "stop"
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "web_search" or "WebSearch" => "should not be called",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            router: router);

        var result = await agent.ProcessAsync(
            "Tell me about your favorite thing to help people with. What makes you good at it?");

        Assert.True(result.Success);
        Assert.DoesNotContain(mcp.Calls, c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(audit.GetByAction("LOOKUP_FLOOR_UPGRADE"));
    }

    /// <summary>
    /// Regression test: a search follow-up ("bring me up more info on the
    /// bakers house and coffee co") must NOT be downgraded by the Footman.
    /// The deterministic Tier-1 follow-up detection is authoritative when
    /// HasRecentSearchResults is true.
    /// </summary>
    [Fact]
    public async Task SearchFollowUp_IsNotDowngradedByFootman_WhenSessionHasRecentResults()
    {
        // Simulate a router that returns LookupSearch (as Tier-1 would
        // when IsFollowUpMessage is true and HasRecentSearchResults is true).
        var router = new FixedRouter(new RouterOutput
        {
            Intent = Intents.LookupSearch,
            Confidence = 0.95,
            NeedsWeb = true,
            NeedsSearch = true,
            NeedsBrowserAutomation = true,
            RequiredCapabilities = [ToolCapability.WebSearch, ToolCapability.BrowserNavigate]
        });

        // Footman tries to downgrade to Chat — this must be blocked.
        var footman = new FixedFootmanRouter(new RoutingDecision
        {
            NextState = AgentState.Chat,
            ContextPolicy = ContextPolicy.ChatSessionSnapshot,
            Confidence = 0.88,
            Abstain = false,
            ReasonCode = "test_chat_downgrade"
        });

        var webSearchPayload =
            "1. Baker's House and Coffee Co - Local bakery and coffee shop\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/bakers-house\",\"title\":\"Baker's House\"}]";

        var llm = new FakeLlmClient((messages, tools) => new LlmResponse
        {
            IsComplete = true,
            Content = "Baker's House and Coffee Co is a local bakery.",
            FinishReason = "stop"
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "web_search" or "WebSearch" => webSearchPayload,
                "browser_navigate" or "BrowserNavigate" => "Full article content about Baker's House.",
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            router: router,
            footmanRouter: footman);

        var result = await agent.ProcessAsync(
            "bring me up more info on the bakers house and coffee co");

        // The Footman should NOT be called because the deterministic
        // follow-up detection bypasses it for LookupSearch.
        Assert.False(footman.Called,
            "Footman should be bypassed for LookupSearch follow-ups");

        // The query should reach the search orchestrator and succeed.
        Assert.True(result.Success);
    }

    private sealed class FixedRouter : IRouter
    {
        private readonly RouterOutput _route;

        public FixedRouter(RouterOutput route) => _route = route;

        public Task<RouterOutput> RouteAsync(RouterRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(_route);
    }

    private sealed class FixedFootmanRouter : IFootmanRouter
    {
        private readonly RoutingDecision _decision;

        public bool Called { get; private set; }

        public FixedFootmanRouter(RoutingDecision decision) => _decision = decision;

        public Task<RoutingDecision> RouteAsync(
            string userMessage,
            RoutingFeatures features,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(_decision);
        }
    }
}
