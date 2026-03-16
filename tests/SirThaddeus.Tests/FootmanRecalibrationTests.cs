using SirThaddeus.Agent;
using SirThaddeus.Agent.Routing;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

/// <summary>
/// Tests for the Footman authority recalibration:
/// - ActionTier classification
/// - FootmanBlockReason parsing and tier validation
/// - Deterministic retrieval bypasses Footman veto
/// - Footman can refine queries without blocking
/// - Footman blocks require typed reason codes
/// - Ambiguous intent still routes through Footman
/// - Disagreement logging emitted
/// </summary>
public class FootmanRecalibrationTests
{
    // ── ActionTier Classification ─────────────────────────────────────

    [Theory]
    [InlineData(Intents.UtilityDeterministic, false, false)]
    [InlineData(Intents.MemoryRead, false, false)]
    [InlineData(Intents.ChatOnly, false, false)]
    public void Classify_Tier0_RetrievalSafeLocal(
        string intent, bool needsWeb, bool needsSearch)
    {
        var route = new RouterOutput
        {
            Intent = intent,
            Confidence = 0.95,
            NeedsWeb = needsWeb,
            NeedsSearch = needsSearch
        };
        var evidence = new IntentFeatureExtractor.WebLookupHeuristicEvidence();

        var tier = ActionTierClassifier.Classify(route, "hello", evidence);

        Assert.Equal(ActionTier.RetrievalSafeLocal, tier);
    }

    [Theory]
    [InlineData(Intents.LookupFact)]
    [InlineData(Intents.LookupNews)]
    [InlineData(Intents.LookupDeepDive)]
    [InlineData(Intents.LookupSearch)]
    [InlineData(Intents.BrowseOnce)]
    [InlineData(Intents.ScreenObserve)]
    public void Classify_Tier1_RetrievalSafeExternal(string intent)
    {
        var route = new RouterOutput
        {
            Intent = intent,
            Confidence = 0.93,
            NeedsWeb = true,
            NeedsSearch = true
        };
        var evidence = new IntentFeatureExtractor.WebLookupHeuristicEvidence();

        var tier = ActionTierClassifier.Classify(route, "test query", evidence);

        Assert.Equal(ActionTier.RetrievalSafeExternal, tier);
    }

    [Theory]
    [InlineData(Intents.FileTask)]
    [InlineData(Intents.SystemTask)]
    [InlineData(Intents.MemoryWrite)]
    [InlineData(Intents.GeneralTool)]
    public void Classify_Tier2_PlanComplex(string intent)
    {
        var route = new RouterOutput
        {
            Intent = intent,
            Confidence = 0.95,
            NeedsFileAccess = true
        };
        var evidence = new IntentFeatureExtractor.WebLookupHeuristicEvidence();

        var tier = ActionTierClassifier.Classify(route, "test", evidence);

        Assert.Equal(ActionTier.PlanComplex, tier);
    }

    // ── FootmanBlockReason Parsing ────────────────────────────────────

    [Theory]
    [InlineData("safety_block", FootmanBlockReason.SafetyBlock)]
    [InlineData("SAFETY_BLOCK", FootmanBlockReason.SafetyBlock)]
    [InlineData("safety", FootmanBlockReason.SafetyBlock)]
    [InlineData("content_policy", FootmanBlockReason.SafetyBlock)]
    [InlineData("policy_scope_mismatch", FootmanBlockReason.PolicyScopeMismatch)]
    [InlineData("scope_mismatch", FootmanBlockReason.PolicyScopeMismatch)]
    [InlineData("missing_required_param", FootmanBlockReason.MissingRequiredParam)]
    [InlineData("missing_param", FootmanBlockReason.MissingRequiredParam)]
    [InlineData("tool_unavailable", FootmanBlockReason.ToolUnavailable)]
    [InlineData("tool_disabled", FootmanBlockReason.ToolUnavailable)]
    [InlineData("ambiguous_intent", FootmanBlockReason.AmbiguousIntent)]
    [InlineData("too_ambiguous", FootmanBlockReason.AmbiguousIntent)]
    [InlineData("unclear", FootmanBlockReason.AmbiguousIntent)]
    public void Parse_RecognizedCodes_MapCorrectly(string raw, FootmanBlockReason expected)
    {
        Assert.Equal(expected, FootmanBlockReasonPolicy.Parse(raw));
    }

    [Theory]
    [InlineData(null, FootmanBlockReason.None)]
    [InlineData("", FootmanBlockReason.None)]
    [InlineData("   ", FootmanBlockReason.None)]
    public void Parse_Empty_ReturnsNone(string? raw, FootmanBlockReason expected)
    {
        Assert.Equal(expected, FootmanBlockReasonPolicy.Parse(raw));
    }

    [Theory]
    [InlineData("greeting_detected")]
    [InlineData("footman_llm")]
    [InlineData("some_random_reason")]
    public void Parse_UnrecognizedCodes_ReturnUnknown(string raw)
    {
        Assert.Equal(FootmanBlockReason.Unknown, FootmanBlockReasonPolicy.Parse(raw));
    }

    // ── Block validity per tier ───────────────────────────────────────

    [Fact]
    public void SafetyBlock_ValidForAllTiers()
    {
        Assert.True(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.SafetyBlock, ActionTier.RetrievalSafeLocal));
        Assert.True(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.SafetyBlock, ActionTier.RetrievalSafeExternal));
        Assert.True(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.SafetyBlock, ActionTier.PlanComplex));
    }

    [Fact]
    public void ToolUnavailable_ValidForAllTiers()
    {
        Assert.True(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.ToolUnavailable, ActionTier.RetrievalSafeLocal));
        Assert.True(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.ToolUnavailable, ActionTier.RetrievalSafeExternal));
        Assert.True(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.ToolUnavailable, ActionTier.PlanComplex));
    }

    [Fact]
    public void AmbiguousIntent_OnlyValidForTier2()
    {
        Assert.False(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.AmbiguousIntent, ActionTier.RetrievalSafeLocal));
        Assert.False(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.AmbiguousIntent, ActionTier.RetrievalSafeExternal));
        Assert.True(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.AmbiguousIntent, ActionTier.PlanComplex));
    }

    [Fact]
    public void MissingRequiredParam_ValidForTier1AndTier2()
    {
        Assert.False(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.MissingRequiredParam, ActionTier.RetrievalSafeLocal));
        Assert.True(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.MissingRequiredParam, ActionTier.RetrievalSafeExternal));
        Assert.True(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.MissingRequiredParam, ActionTier.PlanComplex));
    }

    [Fact]
    public void UnknownReason_InvalidForAllTiers()
    {
        Assert.False(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.Unknown, ActionTier.RetrievalSafeLocal));
        Assert.False(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.Unknown, ActionTier.RetrievalSafeExternal));
        Assert.False(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.Unknown, ActionTier.PlanComplex));
    }

    [Fact]
    public void NoneReason_InvalidForAllTiers()
    {
        Assert.False(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.None, ActionTier.RetrievalSafeLocal));
        Assert.False(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.None, ActionTier.RetrievalSafeExternal));
        Assert.False(FootmanBlockReasonPolicy.IsValidBlockForTier(
            FootmanBlockReason.None, ActionTier.PlanComplex));
    }

    // ── Integration: local business discovery not downgraded ──────────

    [Fact]
    public async Task LocalBusiness_NotDowngradedByFootman_DespiteChatDecision()
    {
        // Deterministic router: LookupFact for local business discovery.
        var router = new FixedRouter(new RouterOutput
        {
            Intent = Intents.LookupFact,
            Confidence = 0.93,
            NeedsWeb = true,
            NeedsSearch = true,
            RequiredCapabilities = [ToolCapability.WebSearch]
        });

        // Footman tries to downgrade to Chat with a generic reason.
        var footman = new FixedFootmanRouter(new RoutingDecision
        {
            NextState = AgentState.Chat,
            ContextPolicy = ContextPolicy.ChatSessionSnapshot,
            Confidence = 0.88,
            Abstain = false,
            ReasonCode = "greeting_detected",
            BlockReason = FootmanBlockReason.Unknown
        });

        var webSearchPayload =
            "1. Joe's Deli — Famous pastrami\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/joes-deli\",\"title\":\"Joe's Deli\"}]";

        var llm = new FakeLlmClient((messages, tools) => new LlmResponse
        {
            IsComplete = true,
            Content = "Here are some local delis near you.",
            FinishReason = "stop"
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "web_search" or "WebSearch" => webSearchPayload,
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
            "can you bring me up some local delis please?");

        // Footman should be bypassed entirely for local business
        // discovery (strong deterministic signal).
        Assert.False(footman.Called,
            "Footman should be bypassed for local business discovery queries");
        Assert.True(result.Success);
    }

    [Fact]
    public async Task FactLookup_FootmanCanRefineWithoutBlocking()
    {
        // Deterministic router: weak LookupFact.
        var router = new FixedRouter(new RouterOutput
        {
            Intent = Intents.LookupFact,
            Confidence = 0.90,
            NeedsWeb = true,
            NeedsSearch = true,
            RequiredCapabilities = [ToolCapability.WebSearch]
        });

        // Footman confirms as SearchFact (refinement, not downgrade).
        var footman = new FixedFootmanRouter(new RoutingDecision
        {
            NextState = AgentState.SearchFact,
            ContextPolicy = ContextPolicy.None,
            Confidence = 0.92,
            Abstain = false,
            ReasonCode = "fact_query",
            BlockReason = FootmanBlockReason.None
        });

        var webSearchPayload =
            "1. Latest updates on .NET Aspire\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/aspire\",\"title\":\".NET Aspire Updates\"}]";

        var llm = new FakeLlmClient((messages, tools) => new LlmResponse
        {
            IsComplete = true,
            Content = ".NET Aspire is a stack for building cloud-native apps.",
            FinishReason = "stop"
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "web_search" or "WebSearch" => webSearchPayload,
                "browser_navigate" or "BrowserNavigate" => "Content",
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

        var result = await agent.ProcessAsync("What is .NET Aspire?");

        // Footman confirmed the lookup — web search should proceed.
        Assert.True(footman.Called, "Footman should run for weak-confidence fact lookup");
        Assert.True(result.Success);
        Assert.Contains(mcp.Calls, c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Tier1_FootmanDowngrade_BlockedWithoutTypedReason()
    {
        // Deterministic router: strong LookupFact with high web score.
        var router = new FixedRouter(new RouterOutput
        {
            Intent = Intents.LookupFact,
            Confidence = 0.90,
            NeedsWeb = true,
            NeedsSearch = true,
            RequiredCapabilities = [ToolCapability.WebSearch]
        });

        // Footman tries to downgrade to Chat with an unrecognized reason.
        var footman = new FixedFootmanRouter(new RoutingDecision
        {
            NextState = AgentState.Chat,
            ContextPolicy = ContextPolicy.ChatSessionSnapshot,
            Confidence = 0.85,
            Abstain = false,
            ReasonCode = "i_think_its_chat",
            BlockReason = FootmanBlockReason.Unknown
        });

        var webSearchPayload =
            "1. Weather in Portland today\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/weather\",\"title\":\"Portland Weather\"}]";

        var llm = new FakeLlmClient((messages, tools) => new LlmResponse
        {
            IsComplete = true,
            Content = "Current weather in Portland is 55°F.",
            FinishReason = "stop"
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "web_search" or "WebSearch" => webSearchPayload,
                "browser_navigate" or "BrowserNavigate" => "Content",
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

        var result = await agent.ProcessAsync("What's the weather in Portland?");

        // The downgrade should be blocked because the Footman's reason
        // code is Unknown, which is insufficient for Tier 1.
        Assert.True(result.Success);
        Assert.NotEmpty(audit.GetByAction("FOOTMAN_DOWNGRADE_BLOCKED"));

        // Disagreement should be logged.
        Assert.NotEmpty(audit.GetByAction("ROUTER_DISAGREEMENT"));
    }

    [Fact]
    public async Task Tier2_FootmanRetainsFullAuthority_ForWriteActions()
    {
        // Deterministic router: memory write.
        var router = new FixedRouter(new RouterOutput
        {
            Intent = Intents.MemoryWrite,
            Confidence = 0.97,
            NeedsMemoryWrite = true,
            RequiredCapabilities = [ToolCapability.MemoryWrite]
        });

        // Footman overrides to Chat (maybe it interprets differently).
        var footman = new FixedFootmanRouter(new RoutingDecision
        {
            NextState = AgentState.Chat,
            ContextPolicy = ContextPolicy.ChatSessionSnapshot,
            Confidence = 0.88,
            Abstain = false,
            ReasonCode = "ambiguous_intent",
            BlockReason = FootmanBlockReason.AmbiguousIntent
        });

        var llm = new FakeLlmClient((messages, tools) => new LlmResponse
        {
            IsComplete = true,
            Content = "I noted that for you.",
            FinishReason = "stop"
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
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

        var result = await agent.ProcessAsync("Remember that my favorite color is blue.");

        // Footman should run for Tier 2 (write action).
        Assert.True(footman.Called,
            "Footman should run for write-action queries (Tier 2)");
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Tier1_SafetyBlock_AllowsFootmanToVeto()
    {
        // Deterministic router: LookupFact (Tier 1).
        var router = new FixedRouter(new RouterOutput
        {
            Intent = Intents.LookupFact,
            Confidence = 0.90,
            NeedsWeb = true,
            NeedsSearch = true,
            RequiredCapabilities = [ToolCapability.WebSearch]
        });

        // Footman blocks with safety_block — this IS a valid Tier 1 reason.
        var footman = new FixedFootmanRouter(new RoutingDecision
        {
            NextState = AgentState.Chat,
            ContextPolicy = ContextPolicy.ChatSessionSnapshot,
            Confidence = 0.95,
            Abstain = false,
            ReasonCode = "safety_block",
            BlockReason = FootmanBlockReason.SafetyBlock
        });

        var llm = new FakeLlmClient((messages, tools) => new LlmResponse
        {
            IsComplete = true,
            Content = "I can't help with that.",
            FinishReason = "stop"
        });

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "MemoryRetrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
                "memory_retrieve" => """{"facts":0,"events":0,"chunks":0,"packText":"","hasContent":false}""",
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

        var result = await agent.ProcessAsync("How do I make dangerous things?");

        // Safety block should be accepted even for Tier 1.
        Assert.True(footman.Called);
        Assert.True(result.Success);
        // No web search should be made.
        Assert.DoesNotContain(mcp.Calls, c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));
        // The downgrade should NOT be blocked.
        Assert.Empty(audit.GetByAction("FOOTMAN_DOWNGRADE_BLOCKED"));
    }

    // ── Helper types (reuse the pattern from RoutingAccuracyTests) ────

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
