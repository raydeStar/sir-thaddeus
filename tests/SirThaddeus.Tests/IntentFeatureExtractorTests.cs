using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Tests;

public class IntentFeatureExtractorTests
{
    [Fact]
    public void LooksLikeFactLookup_SeasonEpisodePlotQuery_ReturnsTrue()
    {
        var lower = "what would be the plot of episode 1 of season 3 of stargate universe about?"
            .ToLowerInvariant();

        var result = IntentFeatureExtractor.LooksLikeFactLookup(lower);

        Assert.True(result);
    }

    [Fact]
    public void LooksLikeFactLookup_CreativeWritingSeasonEpisodePrompt_ReturnsFalse()
    {
        var lower = "write episode 1 of season 3 of my fanfic series"
            .ToLowerInvariant();

        var result = IntentFeatureExtractor.LooksLikeFactLookup(lower);

        Assert.False(result);
    }
}
