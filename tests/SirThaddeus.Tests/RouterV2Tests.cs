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
    public async Task RouteAsync_LocalBusinessHeuristics_RouteWithoutLlm(string message)
    {
        var (router, getLlmCalls) = CreateRouterWithCallCounter();
        var route = await router.RouteAsync(new RouterRequest { UserMessage = message });

        Assert.Equal(Intents.LookupFact, route.Intent);
        Assert.Equal(0, getLlmCalls());
    }

    [Theory]
    [InlineData("what's on my screen right now?")]
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
