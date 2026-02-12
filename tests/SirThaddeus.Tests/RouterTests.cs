using SirThaddeus.Agent;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class RouterTests
{
    [Fact]
    public async Task RouteAsync_DeterministicConversion_BypassesLlmAndDisablesWeb()
    {
        var llmCalls = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCalls++;
            return new LlmResponse { IsComplete = true, Content = "search", FinishReason = "stop" };
        });

        var router = new DefaultRouter(llm, new DeterministicUtilityEngineAdapter());
        var route = await router.RouteAsync(new RouterRequest
        {
            UserMessage = "350F in C",
            HasRecentFirstPrinciplesRationale = false,
            HasRecentSearchResults = false
        });

        Assert.Equal(Intents.UtilityDeterministic, route.Intent);
        Assert.False(route.NeedsWeb);
        Assert.False(route.NeedsSearch);
        Assert.Contains(ToolCapability.DeterministicUtility, route.RequiredCapabilities);
        Assert.Equal(0, llmCalls);
    }

    [Fact]
    public async Task RouteAsync_ToolingPrompt_ProducesCapabilitiesOnlyContract()
    {
        var llm = new FakeLlmClient((messages, tools) =>
            new LlmResponse { IsComplete = true, Content = "tool", FinishReason = "stop" });

        var router = new DefaultRouter(llm, new DeterministicUtilityEngineAdapter());
        var route = await router.RouteAsync(new RouterRequest
        {
            UserMessage = "run this command in terminal",
            HasRecentFirstPrinciplesRationale = false,
            HasRecentSearchResults = false
        });

        Assert.Equal(Intents.SystemTask, route.Intent);
        Assert.Contains(ToolCapability.SystemExecute, route.RequiredCapabilities);

        // Router contract stays capability-first: no tool-name output fields.
        var propertyNames = typeof(RouterOutput).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("AllowedTools", propertyNames);
        Assert.DoesNotContain("ToolNames", propertyNames);
    }

    [Fact]
    public async Task RouteAsync_WebPrompt_RoutesToLookupSearch()
    {
        var llm = new FakeLlmClient((messages, tools) =>
            new LlmResponse { IsComplete = true, Content = "chat", FinishReason = "stop" });

        var router = new DefaultRouter(llm, new DeterministicUtilityEngineAdapter());
        var route = await router.RouteAsync(new RouterRequest
        {
            UserMessage = "how is the dow jones doing today?",
            HasRecentFirstPrinciplesRationale = false,
            HasRecentSearchResults = false
        });

        Assert.Equal(Intents.LookupSearch, route.Intent);
        Assert.True(route.NeedsWeb);
        Assert.True(route.NeedsSearch);
        Assert.Contains(ToolCapability.WebSearch, route.RequiredCapabilities);
    }
}

