using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class FootmanRouterTests
{
    // ── JSON Sanitization ────────────────────────────────────────────

    [Fact]
    public void SanitizeJson_CleanJson_ReturnsUnchanged()
    {
        var json = """{"schemaVersion":1,"nextState":"Chat","confidence":0.95}""";
        Assert.Equal(json, FastLlmFootmanRouter.SanitizeJson(json));
    }

    [Fact]
    public void SanitizeJson_MarkdownFences_StripsToJson()
    {
        var raw = """
            ```json
            {"schemaVersion":1,"nextState":"Chat","confidence":0.9}
            ```
            """;
        var result = FastLlmFootmanRouter.SanitizeJson(raw);
        Assert.StartsWith("{", result);
        Assert.EndsWith("}", result);
        Assert.Contains("\"nextState\"", result);
    }

    [Fact]
    public void SanitizeJson_LeadingProse_ExtractsJsonObject()
    {
        var raw = "Here is the routing decision: {\"schemaVersion\":1,\"nextState\":\"Chat\",\"confidence\":0.85} end";
        var result = FastLlmFootmanRouter.SanitizeJson(raw);
        Assert.StartsWith("{", result);
        Assert.EndsWith("}", result);
    }

    [Fact]
    public void SanitizeJson_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, FastLlmFootmanRouter.SanitizeJson(""));
        Assert.Equal(string.Empty, FastLlmFootmanRouter.SanitizeJson("   "));
    }

    [Fact]
    public void SanitizeJson_NoJsonObject_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, FastLlmFootmanRouter.SanitizeJson("no json here"));
        Assert.Equal(string.Empty, FastLlmFootmanRouter.SanitizeJson("just { incomplete"));
    }

    // ── ParseAndValidate ─────────────────────────────────────────────

    [Fact]
    public void ParseAndValidate_ValidChatDecision_ReturnsCorrectState()
    {
        var router = CreateRouter("unused");
        var json = """
        {
            "schemaVersion": 1,
            "requestId": "abc123",
            "nextState": "Chat",
            "contextPolicy": "ChatSessionSnapshot",
            "confidence": 0.92,
            "abstain": false,
            "reasonCode": "greeting_detected"
        }
        """;

        var decision = router.ParseAndValidate(json, "abc123");

        Assert.Equal(AgentState.Chat, decision.NextState);
        Assert.Equal(ContextPolicy.ChatSessionSnapshot, decision.ContextPolicy);
        Assert.Equal(0.92, decision.Confidence, 2);
        Assert.False(decision.Abstain);
        Assert.Equal("greeting_detected", decision.ReasonCode);
        Assert.True(decision.IsAuthoritative);
    }

    [Fact]
    public void ParseAndValidate_ValidSearchFact_ReturnsCorrectState()
    {
        var router = CreateRouter("unused");
        var json = """
        {
            "schemaVersion": 1,
            "requestId": "req1",
            "nextState": "SearchFact",
            "contextPolicy": "None",
            "confidence": 0.88,
            "abstain": false,
            "reasonCode": "fact_query"
        }
        """;

        var decision = router.ParseAndValidate(json, "req1");

        Assert.Equal(AgentState.SearchFact, decision.NextState);
        Assert.Equal(ContextPolicy.None, decision.ContextPolicy);
        Assert.True(decision.IsAuthoritative);
    }

    [Fact]
    public void ParseAndValidate_LowConfidence_AutoFallback()
    {
        var router = CreateRouter("unused");
        var json = """
        {
            "schemaVersion": 1,
            "requestId": "req2",
            "nextState": "SearchFact",
            "contextPolicy": "None",
            "confidence": 0.45,
            "abstain": false,
            "reasonCode": "unsure"
        }
        """;

        var decision = router.ParseAndValidate(json, "req2");

        Assert.Equal(AgentState.Fallback, decision.NextState);
        Assert.True(decision.Abstain);
        Assert.Equal("low_confidence", decision.ReasonCode);
        Assert.False(decision.IsAuthoritative);
    }

    [Fact]
    public void ParseAndValidate_ExplicitAbstain_ReturnsFallback()
    {
        var router = CreateRouter("unused");
        var json = """
        {
            "schemaVersion": 1,
            "requestId": "req3",
            "nextState": "Chat",
            "contextPolicy": "ChatSessionSnapshot",
            "confidence": 0.80,
            "abstain": true,
            "reasonCode": "too_ambiguous"
        }
        """;

        var decision = router.ParseAndValidate(json, "req3");

        Assert.Equal(AgentState.Fallback, decision.NextState);
        Assert.True(decision.Abstain);
        Assert.Equal("too_ambiguous", decision.ReasonCode);
        Assert.False(decision.IsAuthoritative);
    }

    [Fact]
    public void ParseAndValidate_BadSchemaVersion_ReturnsFallback()
    {
        var router = CreateRouter("unused");
        var json = """{"schemaVersion":2,"nextState":"Chat","confidence":0.9,"abstain":false,"reasonCode":"x"}""";

        var decision = router.ParseAndValidate(json, "req4");

        Assert.Equal(AgentState.Fallback, decision.NextState);
        Assert.True(decision.Abstain);
        Assert.Equal("bad_schema_version", decision.ReasonCode);
    }

    [Fact]
    public void ParseAndValidate_UnknownState_ReturnsFallback()
    {
        var router = CreateRouter("unused");
        var json = """{"schemaVersion":1,"nextState":"DoLaundry","confidence":0.9,"abstain":false,"reasonCode":"x"}""";

        var decision = router.ParseAndValidate(json, "req5");

        Assert.Equal(AgentState.Fallback, decision.NextState);
        Assert.True(decision.Abstain);
        Assert.Equal("unknown_state", decision.ReasonCode);
    }

    [Fact]
    public void ParseAndValidate_EmptyString_ReturnsFallback()
    {
        var router = CreateRouter("unused");
        var decision = router.ParseAndValidate("", "req6");

        Assert.Equal(AgentState.Fallback, decision.NextState);
        Assert.True(decision.Abstain);
        Assert.Equal("parse_empty", decision.ReasonCode);
    }

    [Fact]
    public void ParseAndValidate_InvalidJson_ReturnsFallback()
    {
        var router = CreateRouter("unused");
        var decision = router.ParseAndValidate("{not valid json}", "req7");

        Assert.Equal(AgentState.Fallback, decision.NextState);
        Assert.True(decision.Abstain);
        Assert.Contains("parse_", decision.ReasonCode);
    }

    // ── BlockReason Parsing ──────────────────────────────────────────

    [Fact]
    public void ParseAndValidate_SafetyBlockReason_PopulatesBlockReason()
    {
        var router = CreateRouter("unused");
        var json = """
        {
            "schemaVersion": 1,
            "requestId": "br1",
            "nextState": "Chat",
            "contextPolicy": "ChatSessionSnapshot",
            "confidence": 0.92,
            "abstain": false,
            "reasonCode": "safety_block"
        }
        """;

        var decision = router.ParseAndValidate(json, "br1");

        Assert.Equal(AgentState.Chat, decision.NextState);
        Assert.Equal(FootmanBlockReason.SafetyBlock, decision.BlockReason);
    }

    [Fact]
    public void ParseAndValidate_UnknownReasonCode_BlockReasonIsUnknown()
    {
        var router = CreateRouter("unused");
        var json = """
        {
            "schemaVersion": 1,
            "requestId": "br2",
            "nextState": "SearchFact",
            "confidence": 0.88,
            "abstain": false,
            "reasonCode": "greeting_detected"
        }
        """;

        var decision = router.ParseAndValidate(json, "br2");

        Assert.Equal(AgentState.SearchFact, decision.NextState);
        Assert.Equal(FootmanBlockReason.Unknown, decision.BlockReason);
    }

    [Fact]
    public void ParseAndValidate_LowConfidence_BlockReasonIsUnknown()
    {
        var router = CreateRouter("unused");
        var json = """
        {
            "schemaVersion": 1,
            "requestId": "br3",
            "nextState": "Chat",
            "confidence": 0.40,
            "abstain": false,
            "reasonCode": "safety_block"
        }
        """;

        var decision = router.ParseAndValidate(json, "br3");

        // Low confidence auto-fallback should set BlockReason to Unknown.
        Assert.Equal(AgentState.Fallback, decision.NextState);
        Assert.Equal(FootmanBlockReason.Unknown, decision.BlockReason);
    }

    [Fact]
    public void ParseAndValidate_Abstain_PreservesBlockReason()
    {
        var router = CreateRouter("unused");
        var json = """
        {
            "schemaVersion": 1,
            "requestId": "br4",
            "nextState": "Chat",
            "confidence": 0.80,
            "abstain": true,
            "reasonCode": "ambiguous_intent"
        }
        """;

        var decision = router.ParseAndValidate(json, "br4");

        Assert.Equal(AgentState.Fallback, decision.NextState);
        Assert.True(decision.Abstain);
        Assert.Equal(FootmanBlockReason.AmbiguousIntent, decision.BlockReason);
    }

    [Fact]
    public void ParseAndValidate_MissingContextPolicy_DefaultsFromState()
    {
        var router = CreateRouter("unused");
        var json = """
        {
            "schemaVersion": 1,
            "requestId": "req8",
            "nextState": "SearchFact",
            "confidence": 0.85,
            "abstain": false,
            "reasonCode": "fact"
        }
        """;

        var decision = router.ParseAndValidate(json, "req8");

        Assert.Equal(AgentState.SearchFact, decision.NextState);
        // SearchFact defaults to ContextPolicy.None
        Assert.Equal(ContextPolicy.None, decision.ContextPolicy);
    }

    [Fact]
    public void ParseAndValidate_ConfidenceClamped()
    {
        var router = CreateRouter("unused");
        var json = """{"schemaVersion":1,"nextState":"Chat","confidence":1.5,"abstain":false,"reasonCode":"x"}""";

        var decision = router.ParseAndValidate(json, "req9");
        Assert.Equal(1.0, decision.Confidence, 2);
    }

    // ── AgentState Mapper ────────────────────────────────────────────

    [Theory]
    [InlineData("Chat", AgentState.Chat)]
    [InlineData("chat", AgentState.Chat)]
    [InlineData("SearchFact", AgentState.SearchFact)]
    [InlineData("search_fact", AgentState.SearchFact)]
    [InlineData("SearchNews", AgentState.SearchNews)]
    [InlineData("search_news", AgentState.SearchNews)]
    [InlineData("ScreenObserve", AgentState.ScreenObserve)]
    [InlineData("screen_observe", AgentState.ScreenObserve)]
    [InlineData("MemoryWrite", AgentState.MemoryWrite)]
    [InlineData("memory_write", AgentState.MemoryWrite)]
    [InlineData("Fallback", AgentState.Fallback)]
    public void AgentStateMapper_TryParse_RecognisedValues(string raw, AgentState expected)
    {
        var result = AgentStateMapper.TryParse(raw);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DoLaundry")]
    [InlineData("unknown_state")]
    public void AgentStateMapper_TryParse_UnrecognisedValues_ReturnsNull(string? raw)
    {
        Assert.Null(AgentStateMapper.TryParse(raw));
    }

    [Theory]
    [InlineData(AgentState.Chat, "chat_only")]
    [InlineData(AgentState.SearchFact, "lookup_fact")]
    [InlineData(AgentState.SearchNews, "lookup_news")]
    [InlineData(AgentState.ScreenObserve, "screen_observe")]
    [InlineData(AgentState.Fallback, "general_tool")]
    public void AgentStateMapper_ToIntentString_MapsCorrectly(AgentState state, string expectedIntent)
    {
        Assert.Equal(expectedIntent, AgentStateMapper.ToIntentString(state));
    }

    // ── ContextPolicy Defaults ───────────────────────────────────────

    [Theory]
    [InlineData(AgentState.Chat, ContextPolicy.ChatSessionSnapshot)]
    [InlineData(AgentState.SearchFact, ContextPolicy.None)]
    [InlineData(AgentState.SearchNews, ContextPolicy.None)]
    [InlineData(AgentState.SearchDeepDive, ContextPolicy.LastTurns)]
    [InlineData(AgentState.ScreenObserve, ContextPolicy.ScreenSnapshot)]
    [InlineData(AgentState.MemoryWrite, ContextPolicy.LastAssistantOnly)]
    [InlineData(AgentState.BrowseOnce, ContextPolicy.None)]
    [InlineData(AgentState.Fallback, ContextPolicy.ChatSessionSnapshot)]
    public void ContextPolicyDefaults_ReturnsExpected(AgentState state, ContextPolicy expected)
    {
        Assert.Equal(expected, ContextPolicyDefaults.For(state));
    }

    // ── ToolFamily Policy ────────────────────────────────────────────

    [Fact]
    public void ToolFamilyPolicy_Chat_OnlyMemoryReadAndMeta()
    {
        var families = ToolFamilyPolicy.AllowedFamilies(AgentState.Chat);
        Assert.True(families.HasFlag(ToolFamily.MemoryRead));
        Assert.True(families.HasFlag(ToolFamily.Meta));
        Assert.False(families.HasFlag(ToolFamily.WebSearch));
        Assert.False(families.HasFlag(ToolFamily.SystemExecute));
    }

    [Fact]
    public void ToolFamilyPolicy_SearchFact_IncludesWebSearch()
    {
        var families = ToolFamilyPolicy.AllowedFamilies(AgentState.SearchFact);
        Assert.True(families.HasFlag(ToolFamily.WebSearch));
        Assert.False(families.HasFlag(ToolFamily.SystemExecute));
        Assert.False(families.HasFlag(ToolFamily.ScreenCapture));
    }

    [Fact]
    public void ToolFamilyPolicy_SystemTask_IncludesSystemAndFile()
    {
        var families = ToolFamilyPolicy.AllowedFamilies(AgentState.SystemTask);
        Assert.True(families.HasFlag(ToolFamily.SystemExecute));
        Assert.True(families.HasFlag(ToolFamily.FileSystem));
    }

    [Fact]
    public void ToolFamilyPolicy_ToCapabilities_ExpandsCorrectly()
    {
        var families = ToolFamily.WebSearch | ToolFamily.MemoryRead;
        var caps = ToolFamilyPolicy.ToCapabilities(families);

        Assert.Contains(SirThaddeus.Agent.ToolCapability.WebSearch, caps);
        Assert.Contains(SirThaddeus.Agent.ToolCapability.MemoryRead, caps);
        Assert.DoesNotContain(SirThaddeus.Agent.ToolCapability.ScreenCapture, caps);
    }

    // ── RoutingFeatures ──────────────────────────────────────────────

    [Fact]
    public void RoutingFeatures_Extract_Greeting()
    {
        var features = RoutingFeatures.Extract("hello");
        Assert.True(features.IsGreeting);
        Assert.False(features.LooksLikeFactLookup);
    }

    [Fact]
    public void RoutingFeatures_Extract_WebSearch()
    {
        var features = RoutingFeatures.Extract("what is the weather in Portland?");
        Assert.True(features.LooksLikeWebSearch);
        Assert.True(features.HasQuestionMark);
    }

    [Fact]
    public void RoutingFeatures_Extract_ScreenRequest()
    {
        var features = RoutingFeatures.Extract("what's on my screen");
        Assert.True(features.LooksLikeScreenRequest);
    }

    [Fact]
    public void RoutingFeatures_Extract_MemoryWrite()
    {
        var features = RoutingFeatures.Extract("remember that my name is Alice");
        Assert.True(features.LooksLikeMemoryWrite);
    }

    [Fact]
    public void RoutingFeatures_Extract_SlashCommand()
    {
        var features = RoutingFeatures.Extract("/search bitcoin price");
        Assert.True(features.IsSlashCommand);
    }

    [Fact]
    public void RoutingFeatures_ToPromptSummary_EmptyMessage_ShowsNone()
    {
        var features = RoutingFeatures.Extract("");
        Assert.Contains("signals: none", features.ToPromptSummary());
    }

    [Fact]
    public void RoutingFeatures_ToPromptSummary_WithSignals_ListsThem()
    {
        var features = RoutingFeatures.Extract("hello");
        var summary = features.ToPromptSummary();
        Assert.Contains("greeting", summary);
        Assert.Contains("words:", summary);
    }

    [Fact]
    public void RoutingFeatures_WordCount_Correct()
    {
        var features = RoutingFeatures.Extract("one two three four");
        Assert.Equal(4, features.WordCount);
    }

    // ── RoutingDecision Factory ──────────────────────────────────────

    [Fact]
    public void RoutingDecision_CreateFallback_IsNotAuthoritative()
    {
        var decision = RoutingDecision.CreateFallback("req1", "test_reason");

        Assert.Equal(AgentState.Fallback, decision.NextState);
        Assert.True(decision.Abstain);
        Assert.Equal(0.0, decision.Confidence);
        Assert.False(decision.IsAuthoritative);
        Assert.Equal("test_reason", decision.ReasonCode);
    }

    [Fact]
    public void RoutingDecision_CreateDeterministic_IsAuthoritative()
    {
        var decision = RoutingDecision.CreateDeterministic("req2", AgentState.Chat, "greeting");

        Assert.Equal(AgentState.Chat, decision.NextState);
        Assert.False(decision.Abstain);
        Assert.Equal(1.0, decision.Confidence);
        Assert.True(decision.IsAuthoritative);
    }

    [Fact]
    public void RoutingDecision_EffectiveContextPolicy_FallsBackOnAbstain()
    {
        var decision = RoutingDecision.CreateFallback("req3", "timeout");

        // Abstain → should use Fallback state's default policy
        Assert.Equal(ContextPolicyDefaults.For(AgentState.Fallback), decision.EffectiveContextPolicy);
    }

    [Fact]
    public void RoutingDecision_ConfidenceThreshold_BelowIsNotAuthoritative()
    {
        var decision = new RoutingDecision
        {
            NextState = AgentState.Chat,
            Confidence = 0.59,
            Abstain = false
        };

        Assert.False(decision.IsAuthoritative);
    }

    [Fact]
    public void RoutingDecision_ConfidenceThreshold_AtThresholdIsAuthoritative()
    {
        var decision = new RoutingDecision
        {
            NextState = AgentState.Chat,
            Confidence = 0.60,
            Abstain = false
        };

        Assert.True(decision.IsAuthoritative);
    }

    // ── Full RouteAsync Integration ──────────────────────────────────

    [Fact]
    public async Task RouteAsync_ValidResponse_ReturnsAuthoritativeDecision()
    {
        var llm = new FakeLlmClient(_ => """
        {
            "schemaVersion": 1,
            "requestId": "test",
            "nextState": "Chat",
            "contextPolicy": "ChatSessionSnapshot",
            "confidence": 0.92,
            "abstain": false,
            "reasonCode": "greeting"
        }
        """);

        var router = new FastLlmFootmanRouter(llm);
        var features = RoutingFeatures.Extract("hello there");
        var decision = await router.RouteAsync("hello there", features);

        Assert.Equal(AgentState.Chat, decision.NextState);
        Assert.True(decision.IsAuthoritative);
    }

    [Fact]
    public async Task RouteAsync_LlmThrows_ReturnsFallback()
    {
        var llm = new FakeLlmClient((_, _) =>
            throw new HttpRequestException("LLM is down"));

        var router = new FastLlmFootmanRouter(llm);
        // The test needs a prompt that does NOT match any deterministic
        // short-circuit — otherwise we never call the LLM and the throw
        // is irrelevant. A vague, opinion-ish ask has no single-family
        // signal and is exactly the shape that should defer to the LLM.
        const string ambiguousPrompt = "think about that for a moment";
        var features = RoutingFeatures.Extract(ambiguousPrompt);
        var decision = await router.RouteAsync(ambiguousPrompt, features);

        Assert.Equal(AgentState.Fallback, decision.NextState);
        Assert.True(decision.Abstain);
        Assert.Equal("footman_error", decision.ReasonCode);
    }

    [Fact]
    public async Task RouteAsync_LlmReturnsGarbage_ReturnsFallback()
    {
        var llm = new FakeLlmClient(_ => "I don't know how to route this message.");

        var router = new FastLlmFootmanRouter(llm);
        var features = RoutingFeatures.Extract("test message");
        var decision = await router.RouteAsync("test message", features);

        Assert.Equal(AgentState.Fallback, decision.NextState);
        Assert.True(decision.Abstain);
    }

    [Fact]
    public async Task RouteAsync_Timeout_ReturnsFallback()
    {
        // Use a slow async LLM that respects cancellation tokens
        var llm = new SlowFakeLlmClient(delayMs: 5000);

        var router = new FastLlmFootmanRouter(llm, timeout: TimeSpan.FromMilliseconds(50));
        var features = RoutingFeatures.Extract("test");
        var decision = await router.RouteAsync("test", features);

        Assert.Equal(AgentState.Fallback, decision.NextState);
        Assert.True(decision.Abstain);
        Assert.Equal("footman_timeout", decision.ReasonCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static FastLlmFootmanRouter CreateRouter(string fixedResponse)
    {
        var llm = new FakeLlmClient(_ => fixedResponse);
        return new FastLlmFootmanRouter(llm);
    }
}

/// <summary>
/// Fake LLM client that delays asynchronously before responding.
/// Respects cancellation tokens so the Footman's timeout logic works.
/// </summary>
internal sealed class SlowFakeLlmClient : ILlmClient
{
    private readonly int _delayMs;

    public SlowFakeLlmClient(int delayMs) => _delayMs = delayMs;

    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(_delayMs, cancellationToken);
        return new LlmResponse { IsComplete = true, Content = "{}" };
    }

    public Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokensOverride,
        CancellationToken cancellationToken = default)
        => ChatAsync(messages, tools, cancellationToken);

    public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>("slow-fake-model");
}

// ── Deterministic Single-Family Routing ─────────────────────────────────
//
// Covers the extension to `TryDeterministicRoute` that bypasses the 2B
// gatekeeper LLM on unambiguous single-family signals. When the footman
// gatekeeper abstains or fails, the tool filter opens up — so for common
// shapes (news lookup, file task, etc.) we short-circuit to the right
// AgentState before the gatekeeper call runs.
public class FootmanDeterministicRouteTests
{
    [Fact]
    public void News_lookup_alone_routes_to_SearchNews()
    {
        var features = new RoutingFeatures { LooksLikeNewsLookup = true };
        var d = FastLlmFootmanRouter.TryDeterministicRoute(features, "req");
        Assert.NotNull(d);
        Assert.Equal(AgentState.SearchNews, d!.NextState);
    }

    [Fact]
    public void Local_business_alone_routes_to_SearchDeepDive()
    {
        var features = new RoutingFeatures { LooksLikeLocalBusiness = true };
        var d = FastLlmFootmanRouter.TryDeterministicRoute(features, "req");
        Assert.NotNull(d);
        Assert.Equal(AgentState.SearchDeepDive, d!.NextState);
    }

    [Fact]
    public void Fact_lookup_alone_routes_to_SearchFact()
    {
        var features = new RoutingFeatures { LooksLikeFactLookup = true };
        var d = FastLlmFootmanRouter.TryDeterministicRoute(features, "req");
        Assert.NotNull(d);
        Assert.Equal(AgentState.SearchFact, d!.NextState);
    }

    [Fact]
    public void File_request_alone_routes_to_FileTask()
    {
        var features = new RoutingFeatures { LooksLikeFileRequest = true };
        var d = FastLlmFootmanRouter.TryDeterministicRoute(features, "req");
        Assert.NotNull(d);
        Assert.Equal(AgentState.FileTask, d!.NextState);
    }

    [Fact]
    public void System_command_alone_routes_to_SystemTask()
    {
        var features = new RoutingFeatures { LooksLikeSystemCommand = true };
        var d = FastLlmFootmanRouter.TryDeterministicRoute(features, "req");
        Assert.NotNull(d);
        Assert.Equal(AgentState.SystemTask, d!.NextState);
    }

    [Fact]
    public void Browse_request_alone_routes_to_BrowseOnce()
    {
        var features = new RoutingFeatures { LooksLikeBrowseRequest = true };
        var d = FastLlmFootmanRouter.TryDeterministicRoute(features, "req");
        Assert.NotNull(d);
        Assert.Equal(AgentState.BrowseOnce, d!.NextState);
    }

    [Fact]
    public void Screen_request_alone_routes_to_ScreenObserve()
    {
        var features = new RoutingFeatures { LooksLikeScreenRequest = true };
        var d = FastLlmFootmanRouter.TryDeterministicRoute(features, "req");
        Assert.NotNull(d);
        Assert.Equal(AgentState.ScreenObserve, d!.NextState);
    }

    [Fact]
    public void Mixed_signals_defer_to_LLM_classifier()
    {
        // Conservative: when two family flags are set, no deterministic
        // route fires — the gatekeeper has to decide.
        var features = new RoutingFeatures
        {
            LooksLikeNewsLookup = true,
            LooksLikeLocalBusiness = true,
        };
        var d = FastLlmFootmanRouter.TryDeterministicRoute(features, "req");
        Assert.Null(d);
    }

    [Fact]
    public void Greeting_still_takes_precedence_over_family_signals()
    {
        // If the feature extractor somehow flagged both greeting AND
        // fact_lookup, greeting wins — it's the cheapest to handle and
        // most benign tool-wise (no tools).
        var features = new RoutingFeatures
        {
            IsGreeting = true,
            LooksLikeFactLookup = true,
        };
        var d = FastLlmFootmanRouter.TryDeterministicRoute(features, "req");
        Assert.NotNull(d);
        Assert.Equal(AgentState.Chat, d!.NextState);
    }

    [Fact]
    public void No_signals_defers_to_LLM_classifier()
    {
        var features = new RoutingFeatures();
        var d = FastLlmFootmanRouter.TryDeterministicRoute(features, "req");
        Assert.Null(d);
    }
}
