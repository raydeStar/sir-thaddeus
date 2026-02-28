using SirThaddeus.Agent.Orchestration;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class RouterV2Tests
{
    private class DummyEmbedder : ITextEmbedder
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            return Task.FromResult(new float[128]);
        }
    }

    private class DummyLlm : ILlmClient
    {
        public Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlmResponse { Content = "{ \"Intent\": \"GeneralTool\", \"Confidence\": 0.1 }", IsComplete = true });
        }

        public Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools, int maxTokensOverride, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlmResponse { Content = "{ \"Intent\": \"GeneralTool\", \"Confidence\": 0.1 }", IsComplete = true });
        }

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>("dummy-model");
        }
    }

    private readonly RouterV2 _router;

    public RouterV2Tests()
    {
        var nn = new NnIntentClassifier(new DummyEmbedder());
        var llm = new LlmIntentClassifier(new DummyLlm());
        _router = new RouterV2(nn, llm);
    }

    [Theory]
    [InlineData("florists nearby")]
    [InlineData("restaurants near me")]
    [InlineData("any coffee shop around me")]
    [InlineData("dentist close by")]
    [InlineData("pharmacy in my area")]
    [InlineData("bakery around here")]
    [InlineData("bakeries nearby")]
    [InlineData("pharmacies near me")]
    [InlineData("groceries near me")]
    [InlineData("find me some bakeries nearby")]
    public async Task RouterV2_LocalBusinessProximityQueries_RouteToLookupFactViaTier1(string query)
    {
        var result = await _router.RouteAsync(query, default);
        Assert.Equal("LookupFact", result.Intent);
        Assert.Contains("tier1_heuristic", result.RouteReasonCodes);
    }

    [Theory]
    [InlineData("is left bank pastry open")]
    [InlineData("what time does walmart close")]
    [InlineData("hours and reviews for target")]
    public async Task RouterV2_SpecificPlaceQueries_RouteToLookupDeepDiveViaTier1(string query)
    {
        var result = await _router.RouteAsync(query, default);
        Assert.Equal("LookupDeepDive", result.Intent);
        Assert.Contains("tier1_heuristic", result.RouteReasonCodes);
    }

    [Theory]
    [InlineData("give me the news about AI")]
    [InlineData("tell me the latest news")]
    [InlineData("any recent articles on space exploration")]
    public async Task RouterV2_NewsQueries_RouteToLookupNewsViaTier1(string query)
    {
        var result = await _router.RouteAsync(query, default);
        Assert.Equal("LookupNews", result.Intent);
        Assert.Contains("tier1_heuristic", result.RouteReasonCodes);
    }

    [Theory]
    [InlineData("what's on my screen")]
    [InlineData("look at the active window")]
    [InlineData("what am I looking at")]
    public async Task RouterV2_ScreenQueries_RouteToScreenObserveViaTier1(string query)
    {
        var result = await _router.RouteAsync(query, default);
        Assert.Equal("ScreenObserve", result.Intent);
        Assert.Contains("tier1_heuristic", result.RouteReasonCodes);
    }
}
