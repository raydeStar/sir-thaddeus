using SirThaddeus.Agent;
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

    [Fact]
    public void WebLookupHeuristicEvidence_LatestCurrentEventDetails_RequestsLookup()
    {
        var lower = "bring me up some latest details on the iran war"
            .ToLowerInvariant();

        var evidence = IntentFeatureExtractor.GetWebLookupHeuristicEvidence(lower);

        Assert.True(evidence.ShouldLookup);
        Assert.True(evidence.Score >= 2.0);
        Assert.Equal("freshness_update_combo", evidence.ReasonCode);
    }

    [Fact]
    public void LooksLikeExplicitNewsLookup_LatestCurrentEventDetails_ReturnsTrue()
    {
        var lower = "bring me up some latest details on the iran war"
            .ToLowerInvariant();

        Assert.True(IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lower));
    }

    [Fact]
    public void LooksLikeExplicitNewsLookup_PublicFigureMessageFollowup_ReturnsTrue()
    {
        var lower = "give me more info on putins message"
            .ToLowerInvariant();

        Assert.True(IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lower));
    }

    [Theory]
    [InlineData("ok i want to run a few tests -- can you tell me what is on mys creen right now?")]
    [InlineData("tell me what is on my screen right now")]
    public void LooksLikeScreenRequest_ToleratesConversationalOrSplitWording(string input)
    {
        Assert.True(IntentFeatureExtractor.LooksLikeScreenRequest(input.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("can you see what is in my personal folder?")]
    [InlineData("what is in my folder")]
    [InlineData("can you read my personal folder and tell me whats in there?")]
    public void LooksLikeFileRequest_DetectsNaturalFolderQuestions(string input)
    {
        Assert.True(IntentFeatureExtractor.LooksLikeFileRequest(input.ToLowerInvariant()));
    }

    [Fact]
    public void LooksLikeFileRequest_DoesNotTreatDetailsAsLsCommand()
    {
        var lower = "bring me up some latest details on the iran war"
            .ToLowerInvariant();

        Assert.False(IntentFeatureExtractor.LooksLikeFileRequest(lower));
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

    [Theory]
    [InlineData("use document_read on C:/docs/sample.pdf", Intents.FileTask)]
    [InlineData("call document read for my report", Intents.FileTask)]
    [InlineData("use clipboard_read", Intents.SystemTask)]
    [InlineData("run clipboard write with this text", Intents.SystemTask)]
    public void TryGetExplicitToolInvocationIntent_NewDocumentAndClipboardTools_RoutesToExpectedIntent(
        string input,
        string expectedIntent)
    {
        var result = IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(input.ToLowerInvariant());

        Assert.Equal(expectedIntent, result);
    }

    [Fact]
    public void LooksLikeSelfContainedKnowledgeOrReasoningPrompt_TcpExplanation_ReturnsTrue()
    {
        var lower = "Explain how TCP three-way handshake works and why it matters for reliability."
            .ToLowerInvariant();

        var result = IntentFeatureExtractor.LooksLikeSelfContainedKnowledgeOrReasoningPrompt(lower);

        Assert.True(result);
    }

    [Fact]
    public void LooksLikeSelfContainedKnowledgeOrReasoningPrompt_ExplicitNewsRequest_ReturnsFalse()
    {
        var lower = "Give me the top 5 technology news stories right now."
            .ToLowerInvariant();

        var result = IntentFeatureExtractor.LooksLikeSelfContainedKnowledgeOrReasoningPrompt(lower);

        Assert.False(result);
    }

    [Fact]
    public void LooksLikeSelfContainedKnowledgeOrReasoningPrompt_ArchitectureComparison_ReturnsTrue()
    {
        var lower = "Compare microservices vs monolithic architecture. Cover scalability, deployment complexity, team structure, and debugging difficulty. Give your recommendation for a startup with 5 developers."
            .ToLowerInvariant();

        var result = IntentFeatureExtractor.LooksLikeSelfContainedKnowledgeOrReasoningPrompt(lower);

        Assert.True(result);
    }
}
