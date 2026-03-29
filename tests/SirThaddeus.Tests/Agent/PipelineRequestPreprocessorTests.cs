using SirThaddeus.Agent.Pipeline;

namespace SirThaddeus.Tests;

public sealed class PipelineRequestPreprocessorTests
{
    private readonly IRequestPreprocessor _preprocessor = new RequestPreprocessor();

    [Fact]
    public void SingleIntent_PassesThrough()
    {
        var result = _preprocessor.Decompose("What's the weather in Denver?");

        Assert.Single(result.Intents);
        Assert.False(result.IsMultiIntent);
        Assert.Contains("weather", result.Intents[0].NormalizedRequest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MultiIntent_SplitsCorrectly()
    {
        var result = _preprocessor.Decompose("Hey how are you? Can you get me the local news?");

        Assert.Equal(2, result.Intents.Count);
        Assert.True(result.IsMultiIntent);
        Assert.DoesNotContain("how are you", result.Intents[1].NormalizedRequest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("local news", result.Intents[1].NormalizedRequest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThreePartRequest_AllSplit()
    {
        var result = _preprocessor.Decompose(
            "Summarize my meeting notes, then draft a follow-up email, and remind me at 3pm");

        Assert.Equal(3, result.Intents.Count);
        Assert.Contains(result.Intents, i => i.NormalizedRequest.Contains("Summarize", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Intents, i => i.NormalizedRequest.Contains("draft", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Intents, i => i.NormalizedRequest.Contains("remind", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PureGreeting_SingleIntent()
    {
        var result = _preprocessor.Decompose("Hey there! How's it going?");

        Assert.Single(result.Intents);
        Assert.False(result.IsMultiIntent);
    }

    [Fact]
    public void EmptyInput_ReturnsNoIntents()
    {
        var result = _preprocessor.Decompose("   ");

        Assert.Empty(result.Intents);
        Assert.False(result.IsMultiIntent);
    }
}
