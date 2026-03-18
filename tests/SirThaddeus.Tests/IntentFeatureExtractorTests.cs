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

    [Fact]
    public void LooksLikeFactLookup_ReleasedProductExistencePrompt_ReturnsTrue()
    {
        var lower = "Does iPhone 15 exist as a released product?"
            .ToLowerInvariant();

        var result = IntentFeatureExtractor.LooksLikeFactLookup(lower);

        Assert.True(result);
    }

    [Fact]
    public void WebLookupHeuristicEvidence_ReleasedProductExistencePrompt_RequestsLookup()
    {
        var lower = "Does iPhone 99 exist as a released product?"
            .ToLowerInvariant();

        var evidence = IntentFeatureExtractor.GetWebLookupHeuristicEvidence(lower);

        Assert.True(evidence.ShouldLookup);
        Assert.True(evidence.Score >= 2.8);
        Assert.Equal("released_product_existence", evidence.ReasonCode);
    }

    // ── HasLocalBusinessProximitySignals — proximity detection ────────

    [Theory]
    [InlineData("find me the closest starbucks")]
    [InlineData("nearest starbucks to me")]
    [InlineData("closest walmart")]
    [InlineData("where's the nearest target")]
    public void HasLocalBusinessProximitySignals_ClosestNearestWithBrand_ReturnsTrue(string input)
    {
        Assert.True(IntentFeatureExtractor.HasLocalBusinessProximitySignals(input.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("closest bakery")]
    [InlineData("nearest restaurant")]
    [InlineData("nearest pharmacy")]
    [InlineData("nearest gas station")]
    public void HasLocalBusinessProximitySignals_ClosestNearestWithGeneric_ReturnsTrue(string input)
    {
        Assert.True(IntentFeatureExtractor.HasLocalBusinessProximitySignals(input.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("bakery near me")]
    [InlineData("coffee shop nearby")]
    [InlineData("restaurant close by")]
    [InlineData("local grocery")]
    public void HasLocalBusinessProximitySignals_OriginalPatterns_StillWork(string input)
    {
        Assert.True(IntentFeatureExtractor.HasLocalBusinessProximitySignals(input.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("closest planet to the sun")]
    [InlineData("nearest star system")]
    [InlineData("what is the nearest prime to 100")]
    public void HasLocalBusinessProximitySignals_NonBusinessClosest_ReturnsFalse(string input)
    {
        Assert.False(IntentFeatureExtractor.HasLocalBusinessProximitySignals(input.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("starbucks hours")]
    [InlineData("walmart address")]
    public void HasLocalBusinessProximitySignals_BrandWithoutProximity_ReturnsFalse(string input)
    {
        Assert.False(IntentFeatureExtractor.HasLocalBusinessProximitySignals(input.ToLowerInvariant()));
    }
}
