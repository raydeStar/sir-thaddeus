using SirThaddeus.Agent;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class RouterV2Tests
{
    [Theory]
    [InlineData("deep dive on portland floral hours and reviews")]
    [InlineData("what time does Portland Floral open and close?")]
    [InlineData("tell me when this place opens and closes with reviews")]
    [InlineData("is Walmart open?")]
    [InlineData("when does Target in Seattle close?")]
    [InlineData("store hours for Costco in Tacoma")]
    public async Task RouteAsync_DeepDiveHeuristics_RouteWithoutLlm(string message)
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest { UserMessage = message });

        Assert.Equal(Intents.LookupDeepDive, route.Intent);
        Assert.Equal(0, getLlmCalls());
    }

    [Theory]
    [InlineData("latest news on Nvidia today")]
    [InlineData("show me articles about Nvidia this week")]
    [InlineData("give me headlines about AI this month")]
    [InlineData("find me top stories on apple today")]
    [InlineData("recent news about tesla")]
    public async Task RouteAsync_NewsHeuristics_RouteWithoutLlm(string message)
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest { UserMessage = message });

        Assert.Equal(Intents.LookupNews, route.Intent);
        Assert.Equal(0, getLlmCalls());
    }

    [Theory]
    [InlineData("find florists nearby")]
    [InlineData("show me restaurants near me")]
    [InlineData("any bakeries around here")]
    [InlineData("best coffee shop in seattle")]
    [InlineData("good grocery stores in my area")]
    [InlineData("Does iPhone 15 exist as a released product?")]
    [InlineData("Does iPhone 99 exist as a released product?")]
    public async Task RouteAsync_LocalBusinessHeuristics_RouteWithoutLlm(string message)
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest { UserMessage = message });

        Assert.Equal(Intents.LookupFact, route.Intent);
        Assert.Equal(0, getLlmCalls());
    }

    [Theory]
    [InlineData("what's on my screen right now?")]
    [InlineData("can you tell me what is on my screen?")]
    [InlineData("ok i want to run a few tests -- can you tell me what is on mys creen right now?")]
    [InlineData("take a screenshot")]
    [InlineData("summarize this page")]
    [InlineData("what can you see")]
    public async Task RouteAsync_ScreenHeuristics_RouteWithoutLlm(string message)
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest { UserMessage = message });

        Assert.Equal(Intents.ScreenObserve, route.Intent);
        Assert.Equal(0, getLlmCalls());
    }

    [Theory]
    [InlineData("testing, testing, one two three")]
    [InlineData("mic check one two")]
    public async Task RouteAsync_MicCheckPhrases_StayChatAndAvoidLlm(string message)
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest { UserMessage = message });

        Assert.Equal(Intents.ChatOnly, route.Intent);
        Assert.False(route.NeedsWeb);
        Assert.False(route.NeedsSearch);
        Assert.Equal(0, getLlmCalls());
    }

    [Fact]
    public async Task RouteAsync_PreferencePrompt_StaysChatAndAvoidsLlm()
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest
        {
            UserMessage = "Tell me about your favorite thing to help people with. What makes you good at it?"
        });

        Assert.Equal(Intents.ChatOnly, route.Intent);
        Assert.False(route.NeedsWeb);
        Assert.False(route.NeedsSearch);
        Assert.Equal(0, getLlmCalls());
    }

    [Fact]
    public async Task RouteAsync_SelfContainedReasoningPrompt_StaysChatAndAvoidsLlm()
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest
        {
            UserMessage = "My name is Alex. What is 2 + 2? Then tell me what my name is."
        });

        Assert.Equal(Intents.ChatOnly, route.Intent);
        Assert.False(route.NeedsWeb);
        Assert.False(route.NeedsSearch);
        Assert.Equal(0, getLlmCalls());
    }

    [Fact]
    public async Task RouteAsync_SelfContainedKnowledgePrompt_StaysChatAndAvoidsLlm()
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest
        {
            UserMessage = "Explain how TCP three-way handshake works and why it matters for reliability."
        });

        Assert.Equal(Intents.ChatOnly, route.Intent);
        Assert.False(route.NeedsWeb);
        Assert.False(route.NeedsSearch);
        Assert.Equal(0, getLlmCalls());
    }

    [Fact]
    public async Task RouteAsync_WhenTier1DoesNotMatch_FallsBackToLlmClassification()
    {
        var llmCalls = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCalls++;
            return new LlmResponse { IsComplete = true, Content = "chat", FinishReason = "stop" };
        });

        var router = new RouterV2(llm, new DeterministicUtilityEngineAdapter());
        var route = await router.RouteAsync(new RouterRequest { UserMessage = "tell me a short joke about databases" });

        Assert.Equal(Intents.ChatOnly, route.Intent);
        Assert.True(llmCalls > 0);
    }

    [Fact]
    public async Task RouteAsync_ConversationalTellMe_DoesNotDeterministicallyForceWebLookup()
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest
        {
            UserMessage = "tell me a short joke about databases"
        });

        Assert.Equal(Intents.ChatOnly, route.Intent);
        Assert.True(getLlmCalls() > 0);
    }

    [Fact]
    public async Task RouteAsync_ExplicitSearchPhrase_UsesEvidenceWeightedConfidence()
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest
        {
            UserMessage = "search for nvidia stock news"
        });

        Assert.Equal(Intents.LookupFact, route.Intent);
        Assert.InRange(route.Confidence, 0.88, 0.96);
        Assert.Equal(0, getLlmCalls());
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("hello there")]
    [InlineData("hi")]
    public async Task RouteAsync_GreetingOnly_StaysChatAndAvoidsLlm(string message)
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest { UserMessage = message });

        Assert.Equal(Intents.ChatOnly, route.Intent);
        Assert.False(route.NeedsWeb);
        Assert.False(route.NeedsSearch);
        Assert.Equal(0, getLlmCalls());
    }

    [Theory]
    [InlineData("world.")]
    [InlineData("world")]
    public async Task RouteAsync_StrayTranscriptFragment_StaysChatAndAvoidsLlm(string message)
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest { UserMessage = message });

        Assert.Equal(Intents.ChatOnly, route.Intent);
        Assert.False(route.NeedsWeb);
        Assert.False(route.NeedsSearch);
        Assert.Equal(0, getLlmCalls());
    }

    [Fact]
    public async Task RouteAsync_GreetingPlusActionableQuery_StillRoutesToLookup()
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest
        {
            UserMessage = "hello, what's the weather in Seattle?"
        });

        Assert.Equal(Intents.LookupFact, route.Intent);
        Assert.True(route.NeedsWeb);
        Assert.True(route.NeedsSearch);
        Assert.Equal(0, getLlmCalls());
    }

    [Fact]
    public async Task RouteAsync_MovieComparisonWordForWord_RoutesToLookupFact()
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest
        {
            UserMessage = "Can you tell me if the new live-action How to Train Your Dragon is word for word like the original movies?"
        });

        Assert.Equal(Intents.LookupFact, route.Intent);
        Assert.True(route.NeedsWeb);
        Assert.True(route.NeedsSearch);
        Assert.Equal(0, getLlmCalls());
    }

    [Fact]
    public async Task RouteAsync_ExplicitFileReadInvocation_RoutesToFileTask()
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest
        {
            UserMessage = @"Use file_read on C:\Users\Public\Documents\readme.txt"
        });

        Assert.Equal(Intents.FileTask, route.Intent);
        Assert.True(route.NeedsFileAccess);
        Assert.Equal(0, getLlmCalls());
    }

    [Fact]
    public async Task RouteAsync_NaturalFolderQuestion_RoutesToFileTask()
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest
        {
            UserMessage = "can you see what is in my personal folder?"
        });

        Assert.Equal(Intents.FileTask, route.Intent);
        Assert.True(route.NeedsFileAccess);
        Assert.Equal(0, getLlmCalls());
    }

    [Fact]
    public async Task RouteAsync_ReadMyPersonalFolder_RoutesToFileTask()
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest
        {
            UserMessage = "can you read my personal folder and tell me whats in there?"
        });

        Assert.Equal(Intents.FileTask, route.Intent);
        Assert.True(route.NeedsFileAccess);
        Assert.Equal(0, getLlmCalls());
    }

    [Fact]
    public void RoutingFeatures_IncludeWebEvidenceScoreAndReason()
    {
        var features = RoutingFeatures.Extract("search for weather updates today");
        var summary = features.ToPromptSummary();

        Assert.Contains("web_score=", summary);
        Assert.Contains("web_reason=", summary);
    }

    private static (RouterV2 Router, Func<int> GetLlmCalls) CreateRouterWithCallCounter()
    {
        var llmCalls = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCalls++;
            return new LlmResponse { IsComplete = true, Content = "chat", FinishReason = "stop" };
        });

        var router = new RouterV2(llm, new DeterministicUtilityEngineAdapter());
        return (router, () => llmCalls);
    }
}
