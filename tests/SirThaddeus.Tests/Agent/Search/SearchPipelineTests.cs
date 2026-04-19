using SirThaddeus.Agent;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using System.Text.RegularExpressions;

namespace SirThaddeus.Tests;

// ─────────────────────────────────────────────────────────────────────────
// Search Pipeline Tests
//
// Unit + integration tests for the new modular search pipeline:
//   - SearchModeRouter (deterministic classification)
//   - UtilityRouter (weather, time, calc, conversion bypass)
//   - StoryClustering (Jaccard-based title grouping)
//   - SearchSession (state management)
//   - QueryBuilder (fallback templates)
//   - SearchOrchestrator (full pipeline flows)
// ─────────────────────────────────────────────────────────────────────────

#region ── Search Mode Router ─────────────────────────────────────────────

public class SearchModeRouterTests
{
    private static SearchSession EmptySession() => new();

    private static SearchSession SessionWithResults()
    {
        var session = new SearchSession();
        session.RecordSearchResults(
            SearchMode.NewsAggregate, "test query", "day",
            [new SourceItem { Url = "https://example.com", Title = "Test" }],
            DateTimeOffset.UtcNow);
        return session;
    }

    [Theory]
    [InlineData("pull up the news", SearchMode.NewsAggregate)]
    [InlineData("top headlines today", SearchMode.NewsAggregate)]
    [InlineData("whats happening", SearchMode.NewsAggregate)]
    [InlineData("breaking news", SearchMode.NewsAggregate)]
    [InlineData("daily briefing", SearchMode.NewsAggregate)]
    public void NewsQueries_ClassifyAsNewsAggregate(string message, SearchMode expected)
    {
        var mode = SearchModeRouter.Classify(message, EmptySession(), DateTimeOffset.UtcNow);
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("who is Elon Musk", SearchMode.WebFactFind)]
    [InlineData("explain quantum computing", SearchMode.WebFactFind)]
    [InlineData("stock price of AAPL", SearchMode.WebFactFind)]
    public void FactQueries_ClassifyAsWebFactFind(string message, SearchMode expected)
    {
        var mode = SearchModeRouter.Classify(message, EmptySession(), DateTimeOffset.UtcNow);
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("tell me more")]
    [InlineData("more details on that")]
    [InlineData("elaborate on this")]
    [InlineData("go deeper")]
    public void FollowUpWithSession_ClassifiesAsFollowUp(string message)
    {
        var session = SessionWithResults();
        var mode = SearchModeRouter.Classify(message, session, DateTimeOffset.UtcNow);
        Assert.Equal(SearchMode.FollowUp, mode);
    }

    [Theory]
    [InlineData("tell me more")]
    [InlineData("elaborate on this")]
    public void FollowUpWithoutSession_FallsBackToFactFind(string message)
    {
        var mode = SearchModeRouter.Classify(message, EmptySession(), DateTimeOffset.UtcNow);
        // No session results → can't follow up → falls through to fact find
        Assert.Equal(SearchMode.WebFactFind, mode);
    }

    [Fact]
    public void FollowUpWithLocalBusinessEntity_StillClassifiesAsFollowUp_WhenExplicitPhrasePresent()
    {
        var session = SessionWithResults();
        var mode = SearchModeRouter.Classify(
            "bring me up more info on the godairyfree restaurant",
            session,
            DateTimeOffset.UtcNow);

        Assert.Equal(SearchMode.FollowUp, mode);
    }

    [Fact]
    public void LocalBusinessShowMeQuery_IsNotForcedIntoFollowUp()
    {
        var session = SessionWithResults();
        var mode = SearchModeRouter.Classify(
            "show me bakeries near me",
            session,
            DateTimeOffset.UtcNow);

        Assert.NotEqual(SearchMode.FollowUp, mode);
    }

    [Fact]
    public void FollowUp_MoreOnPhrase_ClassifiesAsFollowUp()
    {
        var session = SessionWithResults();
        var mode = SearchModeRouter.Classify(
            "can you bring me up more on The West Olympia Woman",
            session,
            DateTimeOffset.UtcNow);

        Assert.Equal(SearchMode.FollowUp, mode);
    }

    [Fact]
    public void FollowUp_BringMeUp_ClassifiesAsFollowUp()
    {
        var session = SessionWithResults();
        var mode = SearchModeRouter.Classify(
            "bring me up more on the moonrise bakery",
            session,
            DateTimeOffset.UtcNow);

        Assert.Equal(SearchMode.FollowUp, mode);
    }

    [Fact]
    public void FollowUpBranch_MoreSources_DetectedCorrectly()
    {
        Assert.Equal(FollowUpBranch.MoreSources,
            SearchModeRouter.ClassifyFollowUpBranch("find more sources on this"));
        Assert.Equal(FollowUpBranch.MoreSources,
            SearchModeRouter.ClassifyFollowUpBranch("other coverage please"));
    }

    [Fact]
    public void FollowUpBranch_DeepDive_IsDefault()
    {
        Assert.Equal(FollowUpBranch.DeepDive,
            SearchModeRouter.ClassifyFollowUpBranch("tell me more about this"));
        Assert.Equal(FollowUpBranch.DeepDive,
            SearchModeRouter.ClassifyFollowUpBranch("go deeper"));
    }
}

#endregion

#region ── Utility Router ─────────────────────────────────────────────────

public class UtilityRouterTests
{
    [Theory]
    [InlineData("what's 15% of 230", "calculator", "15% of 230 = **34.50**")]
    [InlineData("what is 15 percent of 230", "calculator", "15% of 230 = **34.50**")]
    [InlineData("what's 10*45?", "calculator", "10*45 = **450**")]
    [InlineData("what is 6 plus 7?", "calculator", "6 + 7 = **13**")]
    [InlineData("Hey, Thaddeus, what's 6x7?", "calculator", "6 * 7 = **42**")]
    [InlineData("100 + 50", "calculator", "100 + 50 = **150**")]
    public void Calculator_ReturnsInlineAnswer(string input, string category, string expected)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.NotNull(result);
        Assert.Equal(category, result!.Category);
        Assert.Equal(expected, result.Answer);
        Assert.Null(result.McpToolName); // Inline — no MCP call needed
    }

    [Theory]
    [InlineData("convert 10 miles to km", "conversion")]
    [InlineData("convert 1 mile to feet", "conversion")]
    [InlineData("convert 100 fahrenheit to celsius", "conversion")]
    [InlineData("convert 5 lbs to kg", "conversion")]
    public void Conversion_ReturnsInlineAnswer(string input, string category)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.NotNull(result);
        Assert.Equal(category, result!.Category);
        Assert.Null(result.McpToolName);
    }

    [Fact]
    public void Conversion_RecipeTemperaturePrompt_ReturnsDeterministicCelsiusSetting()
    {
        var result = UtilityRouter.TryHandle(
            "A recipe says \"bake at 350 for 25 minutes.\" You're in Europe and your oven is set to Celsius. What temperature do you set?");
        Assert.NotNull(result);
        Assert.Equal("conversion", result!.Category);
        Assert.Contains("177 C", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("350 F", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.McpToolName);
    }

    [Fact]
    public void Conversion_HowManyFeetInMile_ReturnsDeterministicAnswer()
    {
        var result = UtilityRouter.TryHandle("how many feet in a mile?");
        Assert.NotNull(result);
        Assert.Equal("conversion", result!.Category);
        Assert.Contains("equals", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5,280", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.McpToolName);
    }

    [Theory]
    [InlineData("time in Tokyo", "time")]
    [InlineData("what's the time in London", "time")]
    public void TimeZone_RoutesToGeocodeTool(string input, string category)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.NotNull(result);
        Assert.Equal(category, result!.Category);
        Assert.Equal("weather_geocode", result.McpToolName);
        Assert.NotNull(result.McpToolArgs);
        Assert.Contains("\"maxResults\":3", result.McpToolArgs, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("is today a holiday in Canada?", "holidays_is_today", "\"countryCode\":\"CA\"")]
    [InlineData("next holiday in US", "holidays_next", "\"countryCode\":\"US\"")]
    [InlineData("holidays in japan this year", "holidays_get", "\"countryCode\":\"JP\"")]
    public void Holiday_RoutesToHolidayTools(string input, string toolName, string expectedArgSnippet)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.NotNull(result);
        Assert.Equal("holiday", result!.Category);
        Assert.Equal(toolName, result.McpToolName);
        Assert.NotNull(result.McpToolArgs);
        Assert.Contains(expectedArgSnippet, result.McpToolArgs, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("is github.com up?")]
    [InlineData("check if https://api.github.com is online")]
    public void Status_RoutesToStatusTool(string input)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.NotNull(result);
        Assert.Equal("status", result!.Category);
        Assert.Equal("status_check_url", result.McpToolName);
        Assert.NotNull(result.McpToolArgs);
        Assert.Contains("\"url\":\"https://", result.McpToolArgs, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"Call file_read exactly once on C:\Users\Public\nonexistent.txt. Do not call status_check_url.")]
    [InlineData(@"Call file_read exactly once on C:\Users\Public\Documents\readme.txt and summarize it.")]
    public void Status_DoesNotRouteExplicitFilePrompts(string input)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("read this feed https://example.com/rss.xml")]
    [InlineData("fetch rss from docs.github.com/feed.xml")]
    public void Feed_RoutesToFeedTool(string input)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.NotNull(result);
        Assert.Equal("feed", result!.Category);
        Assert.Equal("feed_fetch", result.McpToolName);
        Assert.NotNull(result.McpToolArgs);
        Assert.Contains("\"url\":\"https://", result.McpToolArgs, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("weather in Seattle")]
    [InlineData("forecast for New York")]
    [InlineData("what is the weather like in Portland, OR?")]
    [InlineData("can you tell me what the weather is in portland,or?")]
    public void Weather_RoutesToGeocodeTool(string input)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.NotNull(result);
        Assert.Equal("weather", result!.Category);
        Assert.Equal("weather_geocode", result.McpToolName);
    }

    [Fact]
    public void Weather_GeocodeArgs_ContainLocation()
    {
        var result = UtilityRouter.TryHandle("what is the weather like in Portland, OR? please");
        Assert.NotNull(result);
        Assert.Equal("weather_geocode", result!.McpToolName);
        Assert.NotNull(result.McpToolArgs);
        Assert.Contains("\"place\":\"Portland, OR\"", result.McpToolArgs);
        Assert.Contains("\"maxResults\":3", result.McpToolArgs);
    }

    [Fact]
    public void Weather_GeocodeArgs_StripsTemporalTailFromPlace()
    {
        var result = UtilityRouter.TryHandle("What's the forecast for Portland today?");
        Assert.NotNull(result);
        Assert.Equal("weather_geocode", result!.McpToolName);
        Assert.NotNull(result.McpToolArgs);
        Assert.Contains("\"place\":\"Portland\"", result.McpToolArgs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("today", result.McpToolArgs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Weather_TemporalOnlyLocation_ReturnsNull()
    {
        var result = UtilityRouter.TryHandle("can you get the forecast for today?");
        Assert.Null(result);
    }

    [Fact]
    public void LetterCount_ReturnsDeterministicAnswer()
    {
        var result = UtilityRouter.TryHandle("how many R's are in strawberry?");
        Assert.NotNull(result);
        Assert.Equal("text", result!.Category);
        Assert.Equal("The word \"strawberry\" contains **3** 'r' characters.", result.Answer);
        Assert.Null(result.McpToolName);
    }

    [Fact]
    public void MoonDistance_ReturnsDeterministicFactAnswer()
    {
        var result = UtilityRouter.TryHandle("how many meters is it to the moon?");
        Assert.NotNull(result);
        Assert.Equal("fact", result!.Category);
        Assert.Contains("384,400,000 meters", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.McpToolName);
    }

    [Theory]
    [InlineData("what is the speed of light?", "299,792,458 meters per second")]
    [InlineData("what is the boiling point of water?", "100C")]
    [InlineData("what is the freezing point of water?", "0C")]
    [InlineData("how many days are in a year?", "365 days")]
    public void SimpleFacts_ReturnDeterministicAnswer(string input, string expectedSnippet)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.NotNull(result);
        Assert.Equal("fact", result!.Category);
        Assert.Contains(expectedSnippet, result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.McpToolName);
    }

    [Fact]
    public void MontyHall_ReturnsDeterministicSwitchAnswer()
    {
        var result = UtilityRouter.TryHandle(
            "I'm on a game show with three doors. Behind one door is a car, behind the other two are goats. I pick door 1. The host opens door 3, showing a goat. Should I switch to door 2 or stick with door 1?");

        Assert.NotNull(result);
        Assert.Equal("fact", result!.Category);
        Assert.Contains("switch", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1/3", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2/3", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.McpToolName);
    }

    [Theory]
    [InlineData("political climate in Washington")]
    [InlineData("how to weather the storm")]
    public void WeatherFalsePositives_ReturnNull(string input)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("what is quantum computing")]
    [InlineData("tell me about spacex")]
    [InlineData("hey there, how are you")]
    [InlineData("how many days are in a year on mars?")]
    public void NonUtilityQueries_ReturnNull(string input)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("Use tool_ping and report whether MCP tool execution is healthy.")]
    [InlineData("Run tool_ping and confirm whether the MCP server is responding.")]
    public void MetaHealth_RoutesToToolPing(string input)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.NotNull(result);
        Assert.Equal("meta_health", result!.Category);
        Assert.Equal("tool_ping", result.McpToolName);
        Assert.Equal("{}", result.McpToolArgs);
    }
}

public class DeterministicUtilityEngineTests
{
    [Theory]
    [InlineData("350F in C", DeterministicMatchConfidence.High)]
    [InlineData("If I set it to 350 F what is that in C?", DeterministicMatchConfidence.Medium)]
    [InlineData("I'm baking - if I set it to 350F what is that in C?", DeterministicMatchConfidence.Medium)]
    public void TemperatureVariants_RouteDeterministically(
        string input,
        DeterministicMatchConfidence expectedConfidence)
    {
        var result = DeterministicPreRouter.TryRoute(input);
        Assert.NotNull(result);
        Assert.Equal(expectedConfidence, result!.Confidence);
        Assert.Equal("conversion", result.Result.Category);
        Assert.Contains("176.7", result.Result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C", result.Result.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("100 C in K", "373.2K")]
    [InlineData("300 K in C", "26.9°C")]
    public void KelvinConversions_UseOneDecimal(string input, string expected)
    {
        var result = DeterministicPreRouter.TryRoute(input);
        Assert.NotNull(result);
        Assert.Equal("conversion", result!.Result.Category);
        Assert.Contains(expected, result.Result.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EthanolBoilingPoint_IsNotDeterministicConversion()
    {
        var result = DeterministicPreRouter.TryRoute("what's the boiling point of ethanol?");
        Assert.Null(result);
    }
}

#endregion

#region ── Story Clustering ───────────────────────────────────────────────

public class StoryClusteringTests
{
    [Fact]
    public void EmptyList_ReturnsNoClusters()
    {
        var clusters = StoryClustering.Cluster([]);
        Assert.Empty(clusters);
    }

    [Fact]
    public void SingleItem_ReturnsSingleCluster()
    {
        var items = new List<SourceItem>
        {
            new() { Url = "https://a.com", Title = "Japan earthquake kills 5" }
        };

        var clusters = StoryClustering.Cluster(items);
        Assert.Single(clusters);
        Assert.Single(clusters[0].Sources);
    }

    [Fact]
    public void SimilarTitles_ClusteredTogether()
    {
        // Titles sharing significant keywords should cluster together.
        // Using more overlapping terms to ensure Jaccard similarity > 0.3.
        var items = new List<SourceItem>
        {
            new() { Url = "https://a.com", Title = "Massive earthquake hits Japan, kills 5 people in Tokyo" },
            new() { Url = "https://b.com", Title = "Japan earthquake kills dozens, Tokyo shaken" },
            new() { Url = "https://c.com", Title = "Stock market drops 500 points on Wall Street" },
            new() { Url = "https://d.com", Title = "Wall Street stock market tumbles 400 points" }
        };

        var clusters = StoryClustering.Cluster(items);

        // Should produce exactly 2 clusters (earthquake vs market)
        Assert.True(clusters.Count >= 2, $"Expected 2+ clusters, got {clusters.Count}");

        // The earthquake cluster should have 2 items
        var quakeCluster = clusters.FirstOrDefault(c =>
            c.RepresentativeTitle.Contains("earthquake", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(quakeCluster);
        Assert.Equal(2, quakeCluster!.Sources.Count);
    }

    [Fact]
    public void DissimilarTitles_SeparateClusters()
    {
        var items = new List<SourceItem>
        {
            new() { Url = "https://a.com", Title = "SpaceX launches Starship" },
            new() { Url = "https://b.com", Title = "New vaccine approved by FDA" },
            new() { Url = "https://c.com", Title = "Olympics 2028 preparations underway" }
        };

        var clusters = StoryClustering.Cluster(items);

        // Each should be its own cluster
        Assert.Equal(3, clusters.Count);
    }

    [Fact]
    public void JaccardSimilarity_IdenticalSets_Returns1()
    {
        var a = new HashSet<string>(["earthquake", "japan", "kills"]);
        var b = new HashSet<string>(["earthquake", "japan", "kills"]);

        Assert.Equal(1.0, StoryClustering.JaccardSimilarity(a, b));
    }

    [Fact]
    public void JaccardSimilarity_DisjointSets_Returns0()
    {
        var a = new HashSet<string>(["earthquake", "japan"]);
        var b = new HashSet<string>(["stock", "market"]);

        Assert.Equal(0.0, StoryClustering.JaccardSimilarity(a, b));
    }
}

#endregion

#region ── Search Session ─────────────────────────────────────────────────

public class SearchSessionTests
{
    [Fact]
    public void RecordSearchResults_UpdatesSession()
    {
        var session = new SearchSession();
        var sources = new List<SourceItem>
        {
            new() { Url = "https://a.com", Title = "Test", SourceId = "abc123" }
        };

        session.RecordSearchResults(
            SearchMode.NewsAggregate, "test query", "day",
            sources, DateTimeOffset.UtcNow);

        Assert.Equal(SearchMode.NewsAggregate, session.LastMode);
        Assert.Equal("test query", session.LastQuery);
        Assert.Equal("day", session.LastRecency);
        Assert.Single(session.LastResults);
        Assert.Equal("abc123", session.PrimarySourceId);
    }

    [Fact]
    public void HasRecentResults_ReturnsFalse_WhenExpired()
    {
        var session = new SearchSession();
        session.RecordSearchResults(
            SearchMode.NewsAggregate, "test", "any",
            [new SourceItem { Url = "https://a.com", Title = "Test" }],
            DateTimeOffset.UtcNow.AddMinutes(-20)); // Older than TTL

        Assert.False(session.HasRecentResults(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AppendResults_DoesNotDuplicate()
    {
        var session = new SearchSession();
        var source = new SourceItem
        {
            Url = "https://a.com",
            Title = "Test",
            SourceId = SourceItem.ComputeSourceId("https://a.com")
        };
        session.RecordSearchResults(
            SearchMode.NewsAggregate, "test", "any",
            [source], DateTimeOffset.UtcNow);

        session.AppendResults([source], DateTimeOffset.UtcNow);

        // Should not duplicate
        Assert.Single(session.LastResults);
    }

    [Fact]
    public void Clear_ResetsAllState()
    {
        var session = new SearchSession();
        session.RecordSearchResults(
            SearchMode.NewsAggregate, "test", "day",
            [new SourceItem { Url = "https://a.com", Title = "Test" }],
            DateTimeOffset.UtcNow);
        session.LastEntityCanonical = "Test Entity";

        session.Clear();

        Assert.Null(session.LastMode);
        Assert.Null(session.LastQuery);
        Assert.Null(session.LastEntityCanonical);
        Assert.Empty(session.LastResults);
        Assert.Null(session.PrimarySourceId);
    }

    [Fact]
    public void SourceId_IsStable()
    {
        var id1 = SourceItem.ComputeSourceId("https://example.com/article");
        var id2 = SourceItem.ComputeSourceId("https://example.com/article");
        var id3 = SourceItem.ComputeSourceId("https://example.com/article/");
        var id4 = SourceItem.ComputeSourceId("HTTPS://EXAMPLE.COM/article");

        // Same URL → same ID
        Assert.Equal(id1, id2);
        // Trailing slash normalized
        Assert.Equal(id1, id3);
        // Case normalized
        Assert.Equal(id1, id4);
    }
}

#endregion

#region ── Query Builder (Fallback Templates) ─────────────────────────────

public class QueryBuilderFallbackTests
{
    [Fact]
    public void NewsFallback_IncludesNewsKeyword()
    {
        var query = QueryBuilder.BuildFallbackQuery(
            SearchMode.NewsAggregate,
            "what's happening",
            entity: null,
            new SearchSession());

        Assert.True(
            query.Query.Contains("news", StringComparison.OrdinalIgnoreCase) ||
            query.Query.Contains("headline", StringComparison.OrdinalIgnoreCase));
        Assert.True(query.UsedFallback);
    }

    [Fact]
    public void FactFindFallback_UsesEntityName()
    {
        var entity = new EntityResolver.ResolvedEntity
        {
            CanonicalName = "Elon Musk",
            Type = "Person",
            Disambiguation = "CEO of SpaceX"
        };

        var query = QueryBuilder.BuildFallbackQuery(
            SearchMode.WebFactFind,
            "who is that guy",
            entity,
            new SearchSession());

        Assert.Contains("Elon Musk", query.Query);
        Assert.Equal("any", query.Recency);
        Assert.True(query.UsedFallback);
    }

    [Fact]
    public void DetectRecency_FindsTemporalMarkers()
    {
        Assert.Equal("day", QueryBuilder.DetectRecencyFromMessage("news today"));
        Assert.Equal("week", QueryBuilder.DetectRecencyFromMessage("events this week"));
        Assert.Equal("week", QueryBuilder.DetectRecencyFromMessage("recent headlines from last week"));
        Assert.Equal("month", QueryBuilder.DetectRecencyFromMessage("updates past month"));
        Assert.Equal("day", QueryBuilder.DetectRecencyFromMessage("breaking news"));
        Assert.Equal("day", QueryBuilder.DetectRecencyFromMessage("what's the dow jones at most recently?"));
    }

    [Fact]
    public void ExtractTopic_StripsFiller()
    {
        Assert.Equal("quantum computing",
            QueryBuilder.ExtractTopicFromMessage("can you search for quantum computing?"));
        Assert.Equal("latest stock market data",
            QueryBuilder.ExtractTopicFromMessage("hey please find latest stock market data."));
        Assert.Equal("breaking headlines this last week",
            QueryBuilder.ExtractTopicFromMessage("wassup home diggy? can you bring up breaking headlines this last week for me please?"));
        Assert.Equal("the stock market today",
            QueryBuilder.ExtractTopicFromMessage("Well. I wanted to check the stock market today. can you check on the news there?"));
    }

    [Fact]
    public async Task FactFind_DirectQuery_RewritesMediaComparisonPrompt()
    {
        var builder = new QueryBuilder(
            new FakeLlmClient((_, _) => new LlmResponse
            {
                IsComplete = true,
                Content = "unused",
                FinishReason = "stop"
            }),
            new TestAuditLogger());

        var entity = new EntityResolver.ResolvedEntity
        {
            CanonicalName = "How to Train Your Dragon",
            Type = "Media"
        };

        var result = await builder.BuildAsync(
            SearchMode.WebFactFind,
            "Can you tell me if the new live-action How to Train Your Dragon is word for word like the original movies?",
            entity,
            new SearchSession(),
            recentHistory: [],
            ct: CancellationToken.None);

        Assert.Contains("How to Train Your Dragon", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("difference", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("word for word", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public async Task FactFind_DirectQuery_StripsMediaComparisonLeadModifiers_FromResolvedEntity()
    {
        var builder = new QueryBuilder(
            new FakeLlmClient((_, _) => new LlmResponse
            {
                IsComplete = true,
                Content = "unused",
                FinishReason = "stop"
            }),
            new TestAuditLogger());

        var entity = new EntityResolver.ResolvedEntity
        {
            CanonicalName = "live-action How to Train Your Dragon",
            Type = "Media"
        };

        var result = await builder.BuildAsync(
            SearchMode.WebFactFind,
            "Can you tell me if the new live-action How to Train Your Dragon is word for word like the original movies?",
            entity,
            new SearchSession(),
            recentHistory: [],
            ct: CancellationToken.None);

        Assert.Contains("\"How to Train Your Dragon\"", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"live-action How to Train Your Dragon\"", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("difference", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public async Task FactFind_DirectQuery_RewritesMediaComparisonPrompt_WithoutResolvedEntity()
    {
        var builder = new QueryBuilder(
            new FakeLlmClient((_, _) => new LlmResponse
            {
                IsComplete = true,
                Content = "unused",
                FinishReason = "stop"
            }),
            new TestAuditLogger());

        var result = await builder.BuildAsync(
            SearchMode.WebFactFind,
            "Can you tell me if the new live-action How to Train Your Dragon is word for word like the original movies?",
            entity: null,
            new SearchSession(),
            recentHistory: [],
            ct: CancellationToken.None);

        Assert.Contains("\"How to Train Your Dragon\"", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tell me if", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("word for word", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public async Task FactFind_DirectQuery_BroadensMarketplaceRecommendationPrompt()
    {
        var builder = new QueryBuilder(
            new FakeLlmClient((_, _) => new LlmResponse
            {
                IsComplete = true,
                Content = "unused",
                FinishReason = "stop"
            }),
            new TestAuditLogger());

        var entity = new EntityResolver.ResolvedEntity
        {
            CanonicalName = "Ashwagandha",
            Type = "Product"
        };

        var result = await builder.BuildAsync(
            SearchMode.WebFactFind,
            "Can you recommend a good Ashwagandha on Amazon.com?",
            entity,
            new SearchSession(),
            recentHistory: [],
            ct: CancellationToken.None);

        Assert.Contains("Ashwagandha", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("review", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Amazon", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public async Task FactFind_DirectQuery_DoesNotRewriteNonMediaStructuredComparisonPrompt()
    {
        var builder = new QueryBuilder(
            new FakeLlmClient((_, _) => new LlmResponse
            {
                IsComplete = true,
                Content = "unused",
                FinishReason = "stop"
            }),
            new TestAuditLogger());

        var entity = new EntityResolver.ResolvedEntity
        {
            CanonicalName = ".NET Aspire",
            Type = "Technology"
        };

        var result = await builder.BuildAsync(
            SearchMode.WebFactFind,
            "Search for recent updates and developments in .NET Aspire from the last year. " +
            "Synthesize information from multiple sources, compare what overlaps and what differs. " +
            "Provide a structured response with: Overview, Common Points, Differences, Practical Takeaway.",
            entity,
            new SearchSession(),
            recentHistory: [],
            ct: CancellationToken.None);

        Assert.Contains(".NET Aspire", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("update", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("original adaptation", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("live action", result.Query, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.UsedFallback);
    }
}

#endregion

#region ── Dialogue Location Carry-Forward Guards ──────────────────────────

public class DialogueLocationCarryForwardTests
{
    [Fact]
    public void ValidateSlots_DropsMarketIndex_AsLocation()
    {
        var current = new DialogueState
        {
            Topic = "news",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        var merged = new MergedSlots
        {
            Intent = "news",
            Topic = "news",
            LocationText = "Dow Jones",
            LocationInferredFromState = false,
            RawMessage = "how is the dow jones doing?"
        };

        var validator = new ValidateSlots(new ValidationOptions());
        var validated = validator.Run(current, merged);

        Assert.Null(validated.LocationText);
        Assert.False(validated.LocationInferred);
        Assert.Equal("how is the dow jones doing?", validated.NormalizedMessage);
    }

    [Fact]
    public void MergeSlots_DoesNotCarryNonPlacePriorLocation_IntoNewsTurn()
    {
        var current = new DialogueState
        {
            Topic = "news",
            LocationName = "Dow Jones",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        var extracted = new ExtractedSlots
        {
            Intent = "news",
            Topic = "news",
            TimeScope = "this week",
            RawMessage = "get me us headline news this week"
        };

        var merge = new MergeSlots();
        var merged = merge.Run(current, extracted, DateTimeOffset.UtcNow);

        Assert.Null(merged.LocationText);
        Assert.False(merged.LocationInferredFromState);
    }

    [Fact]
    public void MergeSlots_CarriesRealPlacePriorLocation_ForNewsTurn()
    {
        var current = new DialogueState
        {
            Topic = "news",
            LocationName = "Seattle, Washington",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        var extracted = new ExtractedSlots
        {
            Intent = "news",
            Topic = "news",
            TimeScope = "this week",
            RawMessage = "get me local headlines this week"
        };

        var merge = new MergeSlots();
        var merged = merge.Run(current, extracted, DateTimeOffset.UtcNow);

        Assert.Equal("Seattle, Washington", merged.LocationText);
        Assert.True(merged.LocationInferredFromState);
    }

    [Theory]
    [InlineData("you. Why is Dante so chunky?")]       // sentence fragment with ? — the exact bug
    [InlineData("Hello sir Thaddeus I have a question")] // too many words, starts with "hello"
    [InlineData("tell me about the weather")]            // starts with "tell"
    [InlineData("is it going to rain tomorrow")]         // starts with "is"
    [InlineData("What time is it in New York")]          // starts with "what"
    [InlineData("I was wondering why is Dante so chunky")] // starts with "I", too many words
    [InlineData("latest")]                               // temporal freshness token, not a place
    [InlineData("recent")]                               // temporal freshness token, not a place
    [InlineData("current")]                              // temporal freshness token, not a place
    public void ValidateSlots_DropsGarbageLocationValues(string garbage)
    {
        var merged = new MergedSlots
        {
            Intent = "chat",
            Topic = "chat",
            LocationText = garbage,
            LocationInferredFromState = false,
            RawMessage = "test message"
        };

        var validator = new ValidateSlots(new ValidationOptions());
        var validated = validator.Run(
            new DialogueState { UpdatedAtUtc = DateTimeOffset.UtcNow },
            merged);

        Assert.Null(validated.LocationText);
    }

    [Theory]
    [InlineData("New York")]
    [InlineData("Seattle, Washington")]
    [InlineData("San Luis Obispo")]
    [InlineData("St. Louis")]
    [InlineData("Washington, D.C.")]
    public void ValidateSlots_KeepsLegitimateLocationValues(string place)
    {
        var merged = new MergedSlots
        {
            Intent = "weather",
            Topic = "weather",
            LocationText = place,
            LocationInferredFromState = false,
            RawMessage = $"weather in {place}"
        };

        var validator = new ValidateSlots(new ValidationOptions());
        var validated = validator.Run(
            new DialogueState { UpdatedAtUtc = DateTimeOffset.UtcNow },
            merged);

        Assert.Equal(place, validated.LocationText);
    }

    [Fact]
    public void MergeSlots_DropsGarbagePriorLocation_FromLlmEcho()
    {
        // Prior turn: LLM echoed message content into LocationName.
        var current = new DialogueState
        {
            Topic = "weather",
            LocationName = "you. Why is Dante so chunky?",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        var extracted = new ExtractedSlots
        {
            Intent = "news",
            Topic = "news",
            RawMessage = "how is the dow jones today?"
        };

        var merge = new MergeSlots();
        var merged = merge.Run(current, extracted, DateTimeOffset.UtcNow);

        Assert.Null(merged.LocationText);
        Assert.False(merged.LocationInferredFromState);
    }
}

#endregion

#region ── Search Orchestrator (Source Parsing) ───────────────────────────

public class SearchOrchestratorParsingTests
{
    [Fact]
    public void ParseSourcesFromToolResult_ExtractsJsonSources()
    {
        var toolResult =
            "1. \"Headline One\" — source1.com\n" +
            "   Excerpt...\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[\n" +
            "  {\"url\":\"https://source1.com/article\",\"title\":\"Headline One\",\"domain\":\"source1.com\",\"publishedAt\":\"2026-02-12T14:00:00Z\"},\n" +
            "  {\"url\":\"https://source2.com/article\",\"title\":\"Headline Two\",\"domain\":\"source2.com\"}\n" +
            "]";

        var sources = SearchOrchestrator.ParseSourcesFromToolResult(toolResult);

        Assert.Equal(2, sources.Count);
        Assert.Equal("Headline One", sources[0].Title);
        Assert.Equal("https://source1.com/article", sources[0].Url);
        Assert.False(string.IsNullOrWhiteSpace(sources[0].SourceId));
        Assert.Equal(
            DateTimeOffset.Parse("2026-02-12T14:00:00Z"),
            sources[0].PublishedAt);
    }

    [Fact]
    public void ParseSourcesFromToolResult_ReturnsEmpty_WhenNoDelimiter()
    {
        var sources = SearchOrchestrator.ParseSourcesFromToolResult("Just some text, no JSON.");
        Assert.Empty(sources);
    }

    [Fact]
    public void ParseSourcesFromToolResult_ReturnsEmpty_WhenMalformedJson()
    {
        var toolResult = "text\n<!-- SOURCES_JSON -->\nnot valid json";
        var sources = SearchOrchestrator.ParseSourcesFromToolResult(toolResult);
        Assert.Empty(sources);
    }
}

#endregion

#region ── Search Orchestrator (Mode Hints + Contracts) ────────────────────

public class SearchOrchestratorModeHintTests
{
    [Fact]
    public async Task ExecuteAsync_FactHint_EnforcesPlainAnswerContract()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"name":"Nvidia","type":"org","hint":"chipmaker"}""", FinishReason = "stop" };
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"Nvidia stock price","recency":"day"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "Nvidia is up today.", FinishReason = "stop" };
        });

        var searchResult =
            "1. Nvidia update\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/nvda\",\"title\":\"Nvidia update\"}]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" => searchResult,
            "WebSearch" => searchResult,
            "browser_navigate" => "Article content.",
            "BrowserNavigate" => "Article content.",
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.");

        var result = await orchestrator.ExecuteAsync(
            "latest news on Nvidia today",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.SuppressSourceCardsUi);
        Assert.True(result.SuppressToolActivityUi);
    }

    [Fact]
    public async Task ExecuteAsync_NewsHint_LeavesCardsVisible()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"name":"Nvidia","type":"org","hint":"chipmaker"}""", FinishReason = "stop" };
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"Nvidia latest news today","recency":"day"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "Here are today's Nvidia headlines.", FinishReason = "stop" };
        });

        var searchResult =
            "1. Nvidia story\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/nvda\",\"title\":\"Nvidia story\"}]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" => searchResult,
            "WebSearch" => searchResult,
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.");

        var result = await orchestrator.ExecuteAsync(
            "what's the Paris Agreement",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.News,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.SuppressSourceCardsUi);
        Assert.False(result.SuppressToolActivityUi);
    }

    [Fact]
    public async Task ExecuteAsync_FactHint_DoesNotOverrideFollowUpWithRecentLocalDiscovery()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"olympia florists","recency":"any"}""", FinishReason = "stop" };

            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var searchResult =
            "Top local results\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://example.com/olympia-flower-farms\",\"title\":\"These Olympia Flower Farms\",\"domain\":\"example.com\",\"snippet\":\"Directory of Olympia flower farms and florists.\"}" +
            "]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" => searchResult,
            "WebSearch" => searchResult,
            "places_lookup" => "{\"name\":\"These Olympia Flower Farms\",\"address\":\"Olympia, WA\"}",
            "PlacesLookup" => "{\"name\":\"These Olympia Flower Farms\",\"address\":\"Olympia, WA\"}",
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var discovery = await orchestrator.ExecuteAsync(
            "show me local florists nearby",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(discovery.Success);
        Assert.True(orchestrator.Session.LastWasLocalBusinessDiscovery);

        var followUp = await orchestrator.ExecuteAsync(
            "can you pull more info up on these olympia flower farms?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(followUp.Success);
        Assert.NotNull(followUp.DeepDiveBriefing);
    }

    [Theory]
    [InlineData(LookupModeHint.Auto)]
    [InlineData(LookupModeHint.Fact)]
    public async Task ExecuteAsync_ExplicitDeepDivePrompt_UsesDeepDiveBriefing_ForAutoAndFactHints(LookupModeHint modeHint)
    {
        var llm = new FakeLlmClient((messages, _) => new LlmResponse
        {
            IsComplete = true,
            Content = "chat",
            FinishReason = "stop"
        });

        var placesPayload = """
        {
            "provider": "google_places",
            "query": "Seattle Flowers",
            "fetchedAt": "2026-04-01T00:00:00.0000000Z",
            "error": null,
            "place": {
                "placeId": "seattle-flowers",
                "name": "Seattle Flowers",
                "address": "100 Pike St, Seattle, WA 98101",
                "phone": "(206) 555-0100",
                "website": "https://example.test/seattle-flowers",
                "directionsUrl": "https://maps.google.com/?q=Seattle+Flowers",
                "rating": 4.3,
                "userRatingsTotal": 96,
                "openNow": true,
                "weekdayText": ["Tuesday: 9:00 AM - 6:00 PM"],
                "reviews": [
                    {
                        "author": "A",
                        "rating": 5,
                        "text": "Beautiful arrangements.",
                        "relativeTimeDescription": "2 days ago"
                    }
                ],
                "geometry": {
                    "lat": 47.6097,
                    "lng": -122.3331
                }
            },
            "sources": [
                {
                    "name": "Google Places",
                    "url": "https://maps.google.com/?q=Seattle+Flowers",
                    "fetchedIso": "2026-04-01T00:00:00.0000000Z"
                }
            ]
        }
        """;

        var webSearchPayload =
            "1. Seattle Flowers reviews\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.test/seattle-flowers/reviews\",\"title\":\"Seattle Flowers reviews\",\"domain\":\"example.test\",\"excerpt\":\"Recent customer reviews for Seattle Flowers.\"}]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "places_lookup" or "PlacesLookup" => placesPayload,
            "web_search" or "WebSearch" => webSearchPayload,
            "browser_navigate" or "BrowserNavigate" => "Seattle Flowers\n100 Pike St, Seattle, WA 98101\nOpen now",
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.");

        var result = await orchestrator.ExecuteAsync(
            "Deep dive Seattle Flowers with hours + reviews and what to expect.",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: modeHint,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.DeepDiveBriefing);

        var firstCall = mcp.Calls.FirstOrDefault();
        Assert.False(string.IsNullOrWhiteSpace(firstCall.Tool));
        Assert.Equal("places_lookup", firstCall.Tool, ignoreCase: true);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("places_lookup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_DeepDiveHint_RoutesGenericLocalBusinessDiscovery_ToFactFind()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Here are a few deli options in Hillsboro, OR.",
                FinishReason = "stop"
            };
        });

        var searchResult =
            "1. \"Bernie's Deli\" — example.com\n" +
            "   Classic deli sandwiches in Hillsboro, OR.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/bernies-deli\",\"title\":\"Bernie's Deli\",\"domain\":\"example.com\",\"excerpt\":\"Classic deli sandwiches in Hillsboro, OR.\"}]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" => searchResult,
            "WebSearch" => searchResult,
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.");

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good deli in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant."), ChatMessage.User("Can you find me a good deli in Hillsboro, OR?")],
            toolCallsMade: [],
            modeHint: LookupModeHint.DeepDive,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.DeepDiveBriefing);

        var webSearchCall = mcp.Calls.FirstOrDefault(c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));

        Assert.False(string.IsNullOrWhiteSpace(webSearchCall.Tool));
        Assert.Contains("deli", webSearchCall.Args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_DeepDiveHint_WithProfileGates_SkipsAdvancedPlaceTools()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"query":"best deli hillsboro oregon","recency":"any"}""",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Bernie's Deli looks like a solid option based on the web results.",
                FinishReason = "stop"
            };
        });

        var searchResult =
            "1. Bernie's Deli - example.com\n" +
            "   Classic deli sandwiches in Hillsboro, OR.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/bernies-deli\",\"title\":\"Bernie's Deli\",\"domain\":\"example.com\",\"excerpt\":\"Classic deli sandwiches in Hillsboro, OR.\"}]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" => searchResult,
            "WebSearch" => searchResult,
            "browser_navigate" => "Bernie's Deli serves sandwiches in Hillsboro, OR.",
            "BrowserNavigate" => "Bernie's Deli serves sandwiches in Hillsboro, OR.",
            "places_lookup" => throw new InvalidOperationException("places_lookup should be gated off when advanced place discovery is disabled."),
            "PlacesLookup" => throw new InvalidOperationException("places_lookup should be gated off when advanced place discovery is disabled."),
            "places_discover" => throw new InvalidOperationException("places_discover should be gated off when advanced place discovery is disabled."),
            "PlacesDiscover" => throw new InvalidOperationException("places_discover should be gated off when advanced place discovery is disabled."),
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            DeepDiveEnabled = false,
            AdvancedPlaceDiscoveryEnabled = false,
            UserLocationHint = "Hillsboro, OR"
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good deli in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.DeepDive,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.DeepDiveBriefing);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
                                        c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Contains("places", StringComparison.OrdinalIgnoreCase));
    }
}

#endregion

#region ── Multi-Turn Golden Tests ────────────────────────────────────────

public class SearchPipelineGoldenTests
{
    /// <summary>
    /// Creates a FakeLlmClient that responds to the new pipeline's LLM calls
    /// based on system prompt content:
    ///   - "Classify" → classification
    ///   - "entity extractor" → entity extraction JSON
    ///   - "search query builder" → query construction JSON
    ///   - Everything else → summary text
    /// </summary>
    private static FakeLlmClient MakePipelineLlm(
        string entityJson = """{"name":"","type":"none","hint":""}""",
        string queryJson = """{"query":"test query","recency":"any"}""",
        string summaryText = "Here is a summary of the results.")
    {
        return new FakeLlmClient((messages, tools) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify"))
                return new LlmResponse { IsComplete = true, Content = "search", FinishReason = "stop" };

            if (sysMsg.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = entityJson, FinishReason = "stop" };

            if (sysMsg.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = queryJson, FinishReason = "stop" };

            return new LlmResponse { IsComplete = true, Content = summaryText, FinishReason = "stop" };
        });
    }

    [Fact]
    public async Task UtilityBypass_Calculator_NoWebSearch()
    {
        var llm = new FakeLlmClient("Should not be called for calculator");
        var mcp = new FakeMcpClient(returnValue: "should not be called");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("what's 25% of 400");

        Assert.True(result.Success);
        Assert.Contains("100", result.Text);
        Assert.Contains("Need another quick one", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.SuppressSourceCardsUi);
        Assert.True(result.SuppressToolActivityUi);

        // No web_search calls
        var searchCalls = mcp.Calls.Where(c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(searchCalls);
    }

    [Fact]
    public async Task UtilityBypass_CalculatorWordOperator_NoWebSearch()
    {
        var llm = new FakeLlmClient("Should not be called for calculator");
        var mcp = new FakeMcpClient(returnValue: "should not be called");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("what is 6 plus 7?");

        Assert.True(result.Success);
        Assert.Contains("6 + 7 = **13**", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Need another quick one", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.SuppressSourceCardsUi);
        Assert.True(result.SuppressToolActivityUi);

        var searchCalls = mcp.Calls.Where(c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(searchCalls);
    }

    [Fact]
    public async Task UtilityBypass_CalculatorWithAssistantPreamble_NoWebSearch()
    {
        var llm = new FakeLlmClient("Should not be called for calculator");
        var mcp = new FakeMcpClient(returnValue: "should not be called");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("Hey, Thaddeus, what's 6x7?");

        Assert.True(result.Success);
        Assert.Contains("6 * 7 = **42**", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Need another quick one", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.SuppressSourceCardsUi);
        Assert.True(result.SuppressToolActivityUi);

        var searchCalls = mcp.Calls.Where(c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(searchCalls);
    }

    [Fact]
    public async Task UtilityBypass_UnitConversion_NoWebSearch()
    {
        var llm = new FakeLlmClient("Should not be called for conversion");
        var mcp = new FakeMcpClient(returnValue: "should not be called");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("convert 10 miles to km");

        Assert.True(result.Success);
        Assert.Contains("16", result.Text); // 10 miles ≈ 16.09 km
        Assert.Contains("equals", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.SuppressSourceCardsUi);
        Assert.True(result.SuppressToolActivityUi);

        var searchCalls = mcp.Calls.Where(c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(searchCalls);
    }

    [Fact]
    public async Task UtilityBypass_RecipeTemperatureConversion_NoWebSearch()
    {
        var llm = new FakeLlmClient("Should not be called for recipe temperature conversion");
        var mcp = new FakeMcpClient(returnValue: "should not be called");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync(
            "A recipe says \"bake at 350 for 25 minutes.\" You're in Europe and your oven is set to Celsius. What temperature do you set?");

        Assert.True(result.Success);
        Assert.Equal(0, result.LlmRoundTrips);
        Assert.Contains("177 C", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("350 F", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.SuppressSourceCardsUi);
        Assert.True(result.SuppressToolActivityUi);

        var searchCalls = mcp.Calls.Where(c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(searchCalls);
    }

    [Theory]
    [InlineData("350F in C")]
    [InlineData("If I set it to 350 F what is that in C?")]
    [InlineData("I'm baking - if I set it to 350F what is that in C?")]
    public async Task UtilityBypass_DeterministicTemperatureVariants_NoWebSearch(string input)
    {
        var llm = new FakeLlmClient("LLM classify should be bypassed for deterministic temperature conversion");
        var mcp = new FakeMcpClient(returnValue: "should not be called");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync(input);

        Assert.True(result.Success);
        Assert.Equal(0, result.LlmRoundTrips);
        Assert.Contains("176.7", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.SuppressSourceCardsUi);
        Assert.True(result.SuppressToolActivityUi);

        Assert.DoesNotContain(mcp.Calls, c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UtilityBypass_MoonDistance_NoWebSearch()
    {
        var llm = new FakeLlmClient("Should not be called for moon fact");
        var mcp = new FakeMcpClient(returnValue: "should not be called");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("how many meters is it to the moon?");

        Assert.True(result.Success);
        Assert.Contains("384,400,000 meters", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("benchmark", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.SuppressSourceCardsUi);
        Assert.True(result.SuppressToolActivityUi);

        var searchCalls = mcp.Calls.Where(c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(searchCalls);
    }

    [Fact]
    public async Task UtilityBypass_MoonDistance_PrecisionFollowUp_StaysDeterministic()
    {
        var llm = new FakeLlmClient("LLM should not be called");
        var mcp = new FakeMcpClient(returnValue: "MCP should not be called");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var first = await agent.ProcessAsync("How many miles is it from the earth to the moon?");
        Assert.True(first.Success);
        Assert.Contains("Earth-Moon distance", first.Text, StringComparison.OrdinalIgnoreCase);

        var second = await agent.ProcessAsync("I need a more precise figure!");
        Assert.True(second.Success);
        Assert.Contains("384,400.0 km", second.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("238,855", second.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("benchmark", second.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(second.SuppressSourceCardsUi);
        Assert.True(second.SuppressToolActivityUi);

        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Contains("weather_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Contains("resolve_timezone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UtilityBypass_MoonDistance_UnitFollowUp_Feet_StaysDeterministic()
    {
        var llm = new FakeLlmClient("LLM should not be called");
        var mcp = new FakeMcpClient(returnValue: "MCP should not be called");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var first = await agent.ProcessAsync("How many miles is it from the earth to the moon?");
        Assert.True(first.Success);
        Assert.Contains("Earth-Moon distance", first.Text, StringComparison.OrdinalIgnoreCase);

        var second = await agent.ProcessAsync("What is that in feet?");
        Assert.True(second.Success);
        Assert.Contains("5,280", second.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1,261,154,400 feet", second.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("converted locally", second.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("benchmark", second.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(second.SuppressSourceCardsUi);
        Assert.True(second.SuppressToolActivityUi);

        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Contains("weather_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Contains("resolve_timezone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UtilityBypass_SpeedOfLight_NoWebSearch()
    {
        var llm = new FakeLlmClient("Should not be called for speed-of-light fact");
        var mcp = new FakeMcpClient(returnValue: "should not be called");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("what is the speed of light?");

        Assert.True(result.Success);
        Assert.Contains("299,792,458", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("benchmark", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.SuppressSourceCardsUi);
        Assert.True(result.SuppressToolActivityUi);

        var searchCalls = mcp.Calls.Where(c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(searchCalls);
    }

    [Fact]
    public async Task UtilityBypass_Time_UsesGeocodeThenTimezone()
    {
        var llm = new FakeLlmClient("LLM should not be called");
        var geocodeResult =
            """{"query":"Tokyo","source":"photon","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Tokyo, JP","countryCode":"JP","isUs":false,"latitude":35.6762,"longitude":139.6503,"confidence":0.95}]}""";
        var timezoneResult =
            """{"latitude":35.6762,"longitude":139.6503,"timezone":"Asia/Tokyo","source":"open-meteo","cache":{"hit":false,"ageSeconds":0}}""";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "weather_geocode" => geocodeResult,
            "resolve_timezone" => timezoneResult,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("what time is it in Tokyo?");

        Assert.True(result.Success);
        Assert.Contains("Tokyo", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Asia/Tokyo", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("weather_geocode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("resolve_timezone", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UtilityBypass_HolidaysIsToday_NoWebSearch()
    {
        var llm = new FakeLlmClient("LLM should not be called");
        var holidayResult =
            """{"countryCode":"CA","regionCode":null,"date":"2026-02-10","isPublicHoliday":true,"source":"nager-date","cache":{"hit":false,"ageSeconds":0},"holidaysToday":[{"date":"2026-02-10","localName":"Family Day","name":"Family Day","countryCode":"CA","global":false,"launchYear":1990,"counties":[],"types":["Public"]}],"nextHoliday":{"date":"2026-04-10","localName":"Good Friday","name":"Good Friday","countryCode":"CA","global":true,"launchYear":null,"counties":[],"types":["Public"]}}""";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "holidays_is_today" => holidayResult,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("is today a holiday in Canada?");

        Assert.True(result.Success);
        Assert.Contains("public holiday", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CA", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("holidays_is_today", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UtilityBypass_Status_NoWebSearch()
    {
        var llm = new FakeLlmClient("LLM should not be called");
        var statusResult =
            """{"url":"https://github.com/","reachable":true,"httpStatus":200,"method":"HEAD","latencyMs":83,"error":null,"checkedAt":"2026-02-10T18:00:00Z","source":"direct","cache":{"hit":false,"ageSeconds":0}}""";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "status_check_url" => statusResult,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("is github.com up?");

        Assert.True(result.Success);
        Assert.Contains("github.com", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reachable", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("status_check_url", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UtilityBypass_Feed_NoWebSearch()
    {
        var llm = new FakeLlmClient("LLM should not be called");
        var feedResult =
            """{"url":"https://example.com/rss.xml","feedTitle":"Engineering Blog","description":"Latest posts","sourceHost":"example.com","source":"rss","truncated":false,"cache":{"hit":false,"ageSeconds":0},"items":[{"title":"Post One","link":"https://example.com/1","summary":"Summary 1","author":"Team","publishedAt":"2026-02-10T10:00:00Z"},{"title":"Post Two","link":"https://example.com/2","summary":"Summary 2","author":"Team","publishedAt":"2026-02-10T09:00:00Z"}]}""";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "feed_fetch" => feedResult,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("read this feed https://example.com/rss.xml");

        Assert.True(result.Success);
        Assert.Contains("Engineering Blog", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("feed item", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("feed_fetch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UtilityBypass_Weather_UsesGeocodeThenForecast()
    {
        var llmCalls = 0;
        var llm = new FakeLlmClient((_, _) =>
        {
            llmCalls++;
            return new LlmResponse { IsComplete = true, Content = "LLM should not be needed here.", FinishReason = "stop" };
        });
        var geocodeResult =
            """{"query":"Portland, OR","source":"open-meteo","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Portland, Oregon, US","countryCode":"US","isUs":true,"latitude":45.5231,"longitude":-122.6765,"confidence":0.95}]}""";
        var forecastResult =
            """{"provider":"nws","providerReason":"us_primary","cache":{"hit":false,"ageSeconds":0},"location":{"name":"Portland, Oregon, US","countryCode":"US","isUs":true,"latitude":45.5231,"longitude":-122.6765},"current":{"temperature":39,"unit":"F","condition":"windy","wind":"12 mph","humidityPercent":71},"daily":[{"date":"2026-02-10","tempHigh":42,"tempLow":30,"avgTemp":36,"unit":"F","condition":"windy"}],"alerts":[]}""";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "weather_geocode" => geocodeResult,
            "weather_forecast" => forecastResult,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("What is the weather like in Portland, OR?");

        Assert.True(result.Success);
        Assert.Contains("Portland", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("39F", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wind", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Avg temp", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\n1.", result.Text, StringComparison.Ordinal);
        Assert.Equal(0, result.LlmRoundTrips);
        Assert.Equal(0, llmCalls);

        Assert.Contains(mcp.Calls, c => c.Tool.Equals("weather_geocode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("weather_forecast", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UtilityBypass_Weather_DayPlanPrompt_UsesActivityAdvice()
    {
        var llm = new FakeLlmClient("LLM should not be needed here.");
        var geocodeResult =
            """{"query":"Denver","source":"open-meteo","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Denver, Colorado, US","countryCode":"US","isUs":true,"latitude":39.7392,"longitude":-104.9849,"confidence":0.95}]}""";
        var forecastResult =
            """{"provider":"open-meteo","cache":{"hit":false,"ageSeconds":0},"location":{"name":"Denver, Colorado, US","countryCode":"US","isUs":true,"latitude":39.7392,"longitude":-104.9849},"current":{"temperature":39,"unit":"F","condition":"clear","wind":"4 mph","humidityPercent":45},"daily":[{"date":"2026-02-10","avgTemp":44,"unit":"F","condition":"clear"}],"alerts":[]}""";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "weather_geocode" => geocodeResult,
            "weather_forecast" => forecastResult,
            _ => "unexpected tool call"
        });

        var agent = new AgentOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.");

        var result = await agent.ProcessAsync(
            "Use weather tools for Denver and provide a concise, useful plan for the day.");

        Assert.True(result.Success);
        Assert.Contains("Today in Denver", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("39F", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clear", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            result.Text.Contains("Good options", StringComparison.OrdinalIgnoreCase) ||
            result.Text.Contains("Best fit right now", StringComparison.OrdinalIgnoreCase),
            $"Expected an activity-plan phrase, got: {result.Text}");
        Assert.True(
            result.Text.Contains("Bring a layer", StringComparison.OrdinalIgnoreCase) ||
            result.Text.Contains("waterproof", StringComparison.OrdinalIgnoreCase),
            $"Expected a practical caution, got: {result.Text}");
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("weather_geocode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("weather_forecast", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UtilityBypass_Weather_LlmRoute_HandlesFlexiblePhrasing()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify the user message", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse { IsComplete = true, Content = "search", FinishReason = "stop" };
            }

            if (sysMsg.Contains("utility-intent extractor", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"category":"weather","canonicalMessage":"weather in Portland, OR","confidence":0.92}""",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Portland will be cool tomorrow with a chance of wind.",
                FinishReason = "stop"
            };
        });

        var geocodeResult =
            """{"query":"Portland, OR","source":"open-meteo","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Portland, Oregon, US","countryCode":"US","isUs":true,"latitude":45.5231,"longitude":-122.6765,"confidence":0.95}]}""";
        var forecastResult =
            """{"provider":"nws","providerReason":"us_primary","cache":{"hit":false,"ageSeconds":0},"location":{"name":"Portland, Oregon, US","countryCode":"US","isUs":true,"latitude":45.5231,"longitude":-122.6765},"current":{"temperature":39,"unit":"F","condition":"partly cloudy","wind":"7 mph","humidityPercent":54},"daily":[{"date":"2026-02-10","tempHigh":44,"tempLow":31,"avgTemp":38,"unit":"F","condition":"partly cloudy"}],"alerts":[]}""";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "weather_geocode" => geocodeResult,
            "weather_forecast" => forecastResult,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync(
            "I'm going on a trip to Portland tomorrow and want to check conditions there.");

        Assert.True(result.Success);
        Assert.Contains("39F", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Avg temp", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\n1.", result.Text, StringComparison.Ordinal);

        var geocodeCalls = mcp.Calls.Where(c =>
            c.Tool.Equals("weather_geocode", StringComparison.OrdinalIgnoreCase)).ToList();
        var forecastCalls = mcp.Calls.Where(c =>
            c.Tool.Equals("weather_forecast", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.Single(geocodeCalls);
        Assert.Single(forecastCalls);
        Assert.Contains("portland", geocodeCalls[0].Args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UtilityBypass_Weather_IgnoresTemporalTail_AndUsesBestGeocodeCandidate()
    {
        var llm = new FakeLlmClient("LLM should not be called");
        var geocodeResult =
            """{"query":"Portland","source":"photon","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Day-Today, Scotland, GB","countryCode":"GB","isUs":false,"latitude":55.9551009,"longitude":-2.9878669,"confidence":0.10},{"name":"Portland, Oregon, US","countryCode":"US","isUs":true,"latitude":45.5231,"longitude":-122.6765,"confidence":0.95}]}""";
        var forecastResult =
            """{"provider":"nws","providerReason":"us_primary","cache":{"hit":false,"ageSeconds":0},"location":{"name":"Portland, Oregon, US","countryCode":"US","isUs":true,"latitude":45.5231,"longitude":-122.6765},"current":{"temperature":25,"unit":"F","condition":"partly sunny","wind":"3 mph","humidityPercent":34},"daily":[{"date":"2026-02-10","tempHigh":31,"tempLow":19,"avgTemp":25,"unit":"F","condition":"partly sunny"}],"alerts":[]}""";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "weather_geocode" => geocodeResult,
            "weather_forecast" => forecastResult,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("What's the forecast for Portland today?");

        Assert.True(result.Success);
        Assert.Contains("Portland", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Scotland", result.Text, StringComparison.OrdinalIgnoreCase);

        var geocodeCall = mcp.Calls.First(c =>
            c.Tool.Equals("weather_geocode", StringComparison.OrdinalIgnoreCase));
        var forecastCall = mcp.Calls.First(c =>
            c.Tool.Equals("weather_forecast", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("\"place\":\"Portland\"", geocodeCall.Args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("today", geocodeCall.Args, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"latitude\":45.5231", forecastCall.Args, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"longitude\":-122.6765", forecastCall.Args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UtilityBypass_Weather_FollowUp_ReusesPreviousPlaceContext()
    {
        var llm = new FakeLlmClient("LLM should not be called");
        var seattleGeocode =
            """{"query":"Seattle, WA","source":"photon","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Seattle, Washington, US","countryCode":"US","isUs":true,"latitude":47.6062,"longitude":-122.3321,"confidence":0.95}]}""";
        var todayMuseumGeocode =
            """{"query":"today","source":"photon","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Today Art Museum, CN","countryCode":"CN","isUs":false,"latitude":39.896836,"longitude":116.461529,"confidence":0.70}]}""";
        var seattleForecast =
            """{"provider":"nws","providerReason":"us_primary","cache":{"hit":false,"ageSeconds":0},"location":{"name":"Seattle, Washington, US","countryCode":"US","isUs":true,"latitude":47.6062,"longitude":-122.3321},"current":{"temperature":32,"unit":"F","condition":"partly sunny","wind":"3 mph","humidityPercent":34},"daily":[{"date":"2026-02-10","tempHigh":48,"tempLow":38,"avgTemp":43,"unit":"F","condition":"partly sunny"}],"alerts":[]}""";

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "weather_geocode" => args.Contains("seattle", StringComparison.OrdinalIgnoreCase)
                ? seattleGeocode
                : todayMuseumGeocode,
            "weather_forecast" => seattleForecast,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var first = await agent.ProcessAsync("Whats the weather like in Seattle, WA?");
        Assert.True(first.Success);
        Assert.Contains("Seattle", first.Text, StringComparison.OrdinalIgnoreCase);

        var second = await agent.ProcessAsync("Thats great! can you get the forecast for today?");
        Assert.True(second.Success);
        Assert.Contains("Seattle", second.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Today Art Museum", second.Text, StringComparison.OrdinalIgnoreCase);

        var geocodeCalls = mcp.Calls.Where(c =>
            c.Tool.Equals("weather_geocode", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(geocodeCalls.Count >= 2);
        Assert.Contains("\"place\":\"Seattle", geocodeCalls[1].Args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"place\":\"today", geocodeCalls[1].Args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UtilityBypass_WeatherActivityFollowUps_StayOnWeatherPipeline()
    {
        var llm = new FakeLlmClient("LLM should not be called");
        var portlandGeocode =
            """{"query":"Portland, Oregon","source":"open-meteo","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Portland, Oregon, US","countryCode":"US","isUs":true,"latitude":45.5231,"longitude":-122.6765,"confidence":0.95}]}""";
        var portlandForecast =
            """{"provider":"nws","providerReason":"us_primary","cache":{"hit":false,"ageSeconds":0},"location":{"name":"Portland, Oregon, US","countryCode":"US","isUs":true,"latitude":45.5231,"longitude":-122.6765},"current":{"temperature":39,"unit":"F","condition":"chance rain and snow","wind":"7 mph","humidityPercent":85},"daily":[{"date":"2026-02-10","tempHigh":42,"tempLow":30,"avgTemp":35,"unit":"F","condition":"chance rain and snow"}],"alerts":[]}""";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "weather_geocode" => portlandGeocode,
            "weather_forecast" => portlandForecast,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var first = await agent.ProcessAsync("What is the weather in Portland, Oregon today?");
        Assert.True(first.Success);

        var second = await agent.ProcessAsync("What can I do in that kind of weather?");
        Assert.True(second.Success);
        Assert.Contains("Best fit right now", second.Text, StringComparison.OrdinalIgnoreCase);

        var third = await agent.ProcessAsync("That's great, but what kind of things could I do in that weather?");
        Assert.True(third.Success);
        Assert.Contains("Best fit right now", third.Text, StringComparison.OrdinalIgnoreCase);

        var fourth = await agent.ProcessAsync("Dang, what kinds of things can I do in that weather?");
        Assert.True(fourth.Success);
        Assert.Contains("Best fit right now", fourth.Text, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(mcp.Calls,
            c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UtilityBypass_WeatherContextualReference_UsesLocationHintInsteadOfLiteralPhrase()
    {
        var llm = new FakeLlmClient("LLM should not be called");
        var olympiaGeocode =
            """{"query":"Olympia, WA","source":"photon","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Olympia, Washington, US","countryCode":"US","isUs":true,"latitude":47.0379,"longitude":-122.9007,"confidence":0.95}]}""";
        var olympiaForecast =
            """{"provider":"nws","providerReason":"us_primary","cache":{"hit":false,"ageSeconds":0},"location":{"name":"Olympia, Washington, US","countryCode":"US","isUs":true,"latitude":47.0379,"longitude":-122.9007},"current":{"temperature":30,"unit":"F","condition":"foggy","wind":"2 mph","humidityPercent":92},"daily":[{"date":"2026-02-10","tempHigh":36,"tempLow":26,"avgTemp":31,"unit":"F","condition":"foggy"}],"alerts":[]}""";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "weather_geocode" => olympiaGeocode,
            "weather_forecast" => olympiaForecast,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var response = await agent.ProcessAsync("Dang, what kinds of things can I do in that weather?");
        Assert.True(response.Success);
        Assert.Contains("Best fit right now", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Olympia", response.Text, StringComparison.OrdinalIgnoreCase);

        var geocodeCall = Assert.Single(mcp.Calls.Where(c =>
            c.Tool.Equals("weather_geocode", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("\"place\":\"Olympia, WA\"", geocodeCall.Args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("that weather", geocodeCall.Args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task News_FollowUp_ReusesPreviousWeatherPlaceContext()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sysMsg.Contains("Classify the user message", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = "search", FinishReason = "stop" };

            if (sysMsg.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"name":"","type":"none","hint":""}""",
                    FinishReason = "stop"
                };

            if (sysMsg.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
            {
                var userInput = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
                var query = userInput.Contains("seattle", StringComparison.OrdinalIgnoreCase)
                    ? """{"query":"seattle latest news","recency":"day"}"""
                    : """{"query":"top headlines","recency":"day"}""";

                return new LlmResponse
                {
                    IsComplete = true,
                    Content = query,
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Here is a Seattle-focused news summary.",
                FinishReason = "stop"
            };
        });

        var seattleGeocode =
            """{"query":"Seattle, WA","source":"photon","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Seattle, Washington, US","countryCode":"US","isUs":true,"latitude":47.6062,"longitude":-122.3321,"confidence":0.95}]}""";
        var seattleForecast =
            """{"provider":"nws","providerReason":"us_primary","cache":{"hit":false,"ageSeconds":0},"location":{"name":"Seattle, Washington, US","countryCode":"US","isUs":true,"latitude":47.6062,"longitude":-122.3321},"current":{"temperature":32,"unit":"F","condition":"partly sunny","wind":"3 mph","humidityPercent":34},"daily":[{"date":"2026-02-10","tempHigh":48,"tempLow":38,"avgTemp":43,"unit":"F","condition":"partly sunny"}],"alerts":[]}""";
        var newsSearchResult =
            "1. Seattle city update — example.com\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/seattle-news\",\"title\":\"Seattle city update\"}]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "weather_geocode" => seattleGeocode,
            "weather_forecast" => seattleForecast,
            "web_search" => newsSearchResult,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var weather = await agent.ProcessAsync("Whats the weather like in Seattle, WA?");
        Assert.True(weather.Success);

        var news = await agent.ProcessAsync("oh cool! can i get the news?");
        Assert.True(news.Success);

        var webSearchCall = mcp.Calls.Last(c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("seattle", webSearchCall.Args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task News_LocalQuery_InjectsProfileLocationHint()
    {
        var llm = MakePipelineLlm(
            entityJson: """{"name":"","type":"none","hint":""}""",
            queryJson: """{"query":"the local news latest","recency":"day"}""",
            summaryText: "Here are local headlines.");

        var searchResult =
            "1. Local update\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/local\",\"title\":\"Local update\"}]";

        var mcp = new FakeMcpClient(returnValue: searchResult);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            UserLocationHint = "Rexburg, ID"
        };

        var result = await agent.ProcessAsync("Can you look up the local news for me too?");

        Assert.True(result.Success);
        var webSearchCall = mcp.Calls.Last(c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));

        // Query should be restructured to location-first format for
        // better search engine locality (e.g. "Rexburg, ID news today")
        Assert.Contains("rexburg", webSearchCall.Args, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("news today", webSearchCall.Args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task News_LocalQuery_WithExplicitLocation_DoesNotOverrideWithProfileLocationHint()
    {
        var llm = MakePipelineLlm(
            entityJson: """{"name":"","type":"none","hint":""}""",
            queryJson: """{"query":"local news in Boise, ID","recency":"day"}""",
            summaryText: "Here are Boise headlines.");

        var searchResult =
            "1. Boise update\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/boise\",\"title\":\"Boise update\"}]";

        var mcp = new FakeMcpClient(returnValue: searchResult);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            UserLocationHint = "Rexburg, ID"
        };

        var result = await agent.ProcessAsync("Can you get local news in Boise?");

        Assert.True(result.Success);
        var webSearchCall = mcp.Calls.Last(c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("boise", webSearchCall.Args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rexburg", webSearchCall.Args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task News_LocalQuery_NoResults_RetriesBroaderLocationQuery()
    {
        var llm = MakePipelineLlm(
            entityJson: """{"name":"","type":"none","hint":""}""",
            queryJson: """{"query":"recent local news","recency":"day"}""",
            summaryText: "Here are local headlines.");

        var searchResult =
            "1. Rexburg, ID budget update\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/local\",\"title\":\"Rexburg, ID budget update\",\"excerpt\":\"City officials in Rexburg, ID approved the latest budget after public comment.\"}]";

        var mcp = new FakeMcpClient((tool, args) =>
        {
            if (!tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
                !tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            if (args.Contains("rexburg, id local news", StringComparison.OrdinalIgnoreCase))
                return searchResult;

            if (args.Contains("rexburg, id news today", StringComparison.OrdinalIgnoreCase))
                return "No results found for Rexburg, ID news today";

            return searchResult;
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Rexburg, ID"
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you pull up the recent local news?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.News,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Rexburg, ID budget update", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
            call.Args.Contains("rexburg, id local news", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task News_LocalQuery_NoResults_ReturnsDeterministicNoResultsMessage()
    {
        var llm = MakePipelineLlm(
            entityJson: """{"name":"","type":"none","hint":""}""",
            queryJson: """{"query":"recent local news","recency":"day"}""",
            summaryText: "This should not be used.");

        var mcp = new FakeMcpClient((tool, args) =>
        {
            if (tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                return $"No results found for {args}";
            }

            return "";
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Rexburg, ID"
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you pull up the recent local news?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.News,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("I couldn't find usable live local news results", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("I cannot access real-time data", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("built-in reasoning", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task News_SummaryCapabilityClaim_IsRewrittenFromLiveSources()
    {
        var llm = MakePipelineLlm(
            entityJson: """{"name":"","type":"none","hint":""}""",
            queryJson: """{"query":"local news in Olympia, WA","recency":"day"}""",
            summaryText:
                "I cant access your local news feed or browse the web for real-time events. " +
                "I only have access to the documents and snippets provided in our conversation history, " +
                "and I can use my internal knowledge base instead.");

        var payload =
            "1. Olympia school board approves budget after heated meeting\n" +
            "2. Port of Olympia cleanup project enters next phase\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/olympia-budget\",\"title\":\"Olympia school board approves budget after heated meeting\",\"domain\":\"example.com\",\"excerpt\":\"Officials approved the next district budget after a lengthy public comment period.\"}," +
            "{\"url\":\"https://example.com/port-cleanup\",\"title\":\"Port of Olympia cleanup project enters next phase\",\"domain\":\"example.com\",\"excerpt\":\"Crews are moving into the next stage of the waterfront cleanup effort this week.\"}]";

        var audit = new TestAuditLogger();
        var orchestrator = new SearchOrchestrator(
            llm,
            new FakeMcpClient(payload),
            audit,
            "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you get the local news for me?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.News,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain("browse the web", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("internal knowledge base", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Olympia school board approves budget", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(audit.GetByAction("SEARCH_RESPONSE_SANITIZED"), evt =>
            evt.Result == "unsupported_capability_claim");
    }

    [Fact]
    public async Task News_LowValueListPruning_RebuildsGroundedFallback()
    {
        var llm = MakePipelineLlm(
            entityJson: """{"name":"","type":"none","hint":""}""",
            queryJson: """{"query":"local news in Boise, ID","recency":"day"}""",
            summaryText:
                "Thanks for the message. Here are the main stories I found:\n" +
                "1. Top stories\n" +
                "2. Live updates");

        var payload =
            "1. Boise school board approves budget after heated meeting\n" +
            "2. Ada County opens new shelter beds ahead of cold snap\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/boise-budget\",\"title\":\"Boise school board approves budget after heated meeting\",\"domain\":\"example.com\",\"excerpt\":\"Trustees approved the next district budget after a lengthy public comment period in Boise.\"}," +
            "{\"url\":\"https://example.com/ada-shelter\",\"title\":\"Ada County opens new shelter beds ahead of cold snap\",\"domain\":\"example.com\",\"excerpt\":\"County officials opened additional shelter beds this week as temperatures drop in Boise.\"}]";

        var audit = new TestAuditLogger();
        var orchestrator = new SearchOrchestrator(
            llm,
            new FakeMcpClient(payload),
            audit,
            "Test assistant.")
        {
            UserLocationHint = "Boise, ID"
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you pull up the local news in Boise, ID?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.News,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Boise school board approves budget", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1. Top stories", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(audit.GetByAction("SEARCH_RESPONSE_SANITIZED"), evt =>
            evt.Result == "grounded_news_fallback_after_prune");
    }

    [Fact]
    public async Task News_EmptyIntroWithoutStructuredSources_UsesExtractiveFallback()
    {
        var llm = MakePipelineLlm(
            entityJson: """{"name":"","type":"none","hint":""}""",
            queryJson: """{"query":"Boise, Idaho news","recency":"day"}""",
            summaryText: "Thanks for the message. Here are the main stories I found:");

        var payload =
            "1. Boise school board approves budget after heated meeting\n" +
            "2. Ada County opens new shelter beds ahead of cold snap\n";

        var audit = new TestAuditLogger();
        var orchestrator = new SearchOrchestrator(
            llm,
            new FakeMcpClient(payload),
            audit,
            "Test assistant.")
        {
            UserLocationHint = "Boise, ID"
        };

        var result = await orchestrator.ExecuteAsync(
            "Hey whats up, how are you today? Can you pull up the local news in Boise, ID? Anyway, gotta go, bye!",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.News,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Boise", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Boise school board approves budget", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(audit.GetByAction("SEARCH_RESPONSE_SANITIZED"), evt =>
            evt.Result == "grounded_news_fallback_after_prune");
    }

    [Fact]
    public async Task News_LocalQuery_FiltersOutNonLocalHeadlines_WhenLocalMatchesExist()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sysMsg.Contains("Classify"))
                return new LlmResponse { IsComplete = true, Content = "search", FinishReason = "stop" };
            if (sysMsg.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"name":"","type":"none","hint":""}""", FinishReason = "stop" };
            if (sysMsg.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"local news in Olympia, WA","recency":"day"}""", FinishReason = "stop" };

            return new LlmResponse
            {
                IsComplete = true,
                Content = messages.Last().Content,
                FinishReason = "stop"
            };
        });

        var payload =
            "1. Suspect killed after synagogue attack in Detroit\n" +
            "2. Iran says Strait of Hormuz will remain closed\n" +
            "3. Olympia school board approves budget after heated meeting\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://example.com/detroit\",\"title\":\"Suspect killed after synagogue attack in Detroit\",\"domain\":\"example.com\",\"excerpt\":\"CBS reports a suspect was killed after a Detroit-area synagogue attack.\"}," +
            "{\"url\":\"https://example.com/hormuz\",\"title\":\"Iran says Strait of Hormuz will remain closed\",\"domain\":\"example.com\",\"excerpt\":\"CNN reports the new Iranian leader said the strait would stay closed.\"}," +
            "{\"url\":\"https://example.com/olympia-budget\",\"title\":\"Olympia school board approves budget after heated meeting\",\"domain\":\"example.com\",\"excerpt\":\"Officials approved the next district budget after a lengthy public comment period in Olympia.\"}" +
            "]";

        var audit = new TestAuditLogger();
        var orchestrator = new SearchOrchestrator(
            llm,
            new FakeMcpClient(payload),
            audit,
            "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you get the local news for me?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.News,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Olympia school board approves budget", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Detroit", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hormuz", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(audit.GetByAction("LOCAL_NEWS_LOCALITY_FILTER"), evt =>
            evt.Result == "city");
    }

    [Fact]
    public async Task News_LocalQuery_NonLocalHeadlinesOnly_ReturnsNoResultsMessage()
    {
        var llm = MakePipelineLlm(
            entityJson: """{"name":"","type":"none","hint":""}""",
            queryJson: """{"query":"local news in Olympia, WA","recency":"day"}""",
            summaryText: "This should not be used.");

        var payload =
            "1. Suspect killed after synagogue attack in Detroit\n" +
            "2. Iran says Strait of Hormuz will remain closed\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://example.com/detroit\",\"title\":\"Suspect killed after synagogue attack in Detroit\",\"domain\":\"example.com\",\"excerpt\":\"CBS reports a suspect was killed after a Detroit-area synagogue attack.\"}," +
            "{\"url\":\"https://example.com/hormuz\",\"title\":\"Iran says Strait of Hormuz will remain closed\",\"domain\":\"example.com\",\"excerpt\":\"CNN reports the new Iranian leader said the strait would stay closed.\"}" +
            "]";

        var audit = new TestAuditLogger();
        var orchestrator = new SearchOrchestrator(
            llm,
            new FakeMcpClient(payload),
            audit,
            "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you get the local news for me?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.News,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("I couldn't find usable live local news results for Olympia, WA", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Detroit", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(audit.GetByAction("LOCAL_NEWS_LOCALITY_FILTER"), evt =>
            evt.Result == "none");
    }

    [Fact]
    public async Task News_LocalQuery_RemoteHeadlineFromLocalOutlet_ReturnsNoResultsMessage()
    {
        var llm = MakePipelineLlm(
            entityJson: """{"name":"","type":"none","hint":""}""",
            queryJson: """{"query":"local news in Olympia, WA","recency":"day"}""",
            summaryText: "This should not be used.");

        var payload =
            "1. Suspect killed after synagogue attack in Detroit\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://www.theolympian.com/news/nation-world/article123.html\",\"title\":\"Suspect killed after synagogue attack in Detroit\",\"domain\":\"theolympian.com\",\"excerpt\":\"CBS reports a suspect was killed after a Detroit-area synagogue attack.\"}" +
            "]";

        var audit = new TestAuditLogger();
        var orchestrator = new SearchOrchestrator(
            llm,
            new FakeMcpClient(payload),
            audit,
            "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you get the local news for me?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.News,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("I couldn't find usable live local news results for Olympia, WA", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Detroit", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(audit.GetByAction("LOCAL_NEWS_LOCALITY_FILTER"), evt =>
            evt.Result == "none");
    }

    [Fact]
    public async Task News_LocalQuery_RetryBudgetExceeded_ReturnsNoResultsMessage()
    {
        var llm = MakePipelineLlm(
            entityJson: """{"name":"","type":"none","hint":""}""",
            queryJson: """{"query":"recent local news","recency":"day"}""",
            summaryText: "This should not be used.");

        var audit = new TestAuditLogger();
        var mcp = new FakeMcpClient((tool, args) =>
        {
            if (!tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
                !tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            if (args.Contains("rexburg, id news today", StringComparison.OrdinalIgnoreCase))
                return "No results found for Rexburg, ID news today";

            if (args.Contains("rexburg, id local news", StringComparison.OrdinalIgnoreCase))
                return """{"error":"tool_budget_exceeded","budget":"max_web_pulls_per_turn","limit":3,"tool":"web_search"}""";

            return "";
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            UserLocationHint = "Rexburg, ID"
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you pull up the recent local news?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.News,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("I couldn't find usable live local news results", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Web search failed before returning results", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(audit.GetByAction("LOCAL_NEWS_QUERY_RETRY_ABORTED"), evt =>
            evt.Result == "tool_budget_exceeded");
    }

    [Fact]
    public async Task News_WebSearchProviderTrace_RecordsSearxngFallbackDetails()
    {
        var llm = MakePipelineLlm(
            entityJson: """{"name":"","type":"none","hint":""}""",
            queryJson: """{"query":"recent local news","recency":"day"}""",
            summaryText: "Here are local headlines.");

        var payload =
            "1. Local update\n" +
            "<!-- SOURCES_JSON -->\n" +
            """{"sources":[{"url":"https://example.com/local","title":"Local update","domain":"example.com"}],"searchDiagnostics":{"query":"Rexburg, ID news today","recency":"day","provider":"GoogleNews","bundles":[{"query":"Rexburg, ID news today","provider":"GoogleNews","resultCount":1,"errors":[],"diagnostics":[{"provider":"SearxNG","phase":"probe","outcome":"unavailable","message":"probe returned unavailable","resultCount":0},{"provider":"SearchApi","phase":"probe","outcome":"unavailable","message":"probe returned unavailable","resultCount":0},{"provider":"GoogleNews","phase":"fallback","outcome":"results","message":"returned 1 result(s)","resultCount":1}]}]}}""";

        var audit = new TestAuditLogger();
        var orchestrator = new SearchOrchestrator(
            llm,
            new FakeMcpClient(payload),
            audit,
            "Test assistant.")
        {
            UserLocationHint = "Rexburg, ID"
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you pull up the recent local news?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.News,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        var trace = audit.GetByAction("WEB_SEARCH_PROVIDER_TRACE").First();
        var pathSummary = Assert.IsType<string>(trace.Details!["path_summary"]);
        Assert.Contains("SearxNG:probe=unavailable", pathSummary, StringComparison.Ordinal);
        Assert.Equal("GoogleNews", Assert.IsType<string>(trace.Details["provider"]));
    }

    [Theory]
    [InlineData("hello! can you get me the local news?",
                """{"query":"hello","recency":"any"}""")]
    [InlineData("Hey whats up, how are you today? Can you pull up the local news? Anyway, gotta go, bye!",
                """{"query":"hey pull up","recency":"any"}""")]
    [InlineData("morning! local news please",
                """{"query":"morning","recency":"day"}""")]
    public async Task News_ConversationalGreeting_DoesNotPollutSearchQuery(
        string userMessage, string queryJson)
    {
        // When a user wraps a news request in conversational English,
        // the query builder should NOT extract the greeting as the
        // search query. The noise tokens should be filtered, and the
        // injection chain should detect "local news" in the original
        // message even when the query itself is normalized.
        var llm = MakePipelineLlm(
            entityJson: """{"name":"","type":"none","hint":""}""",
            queryJson: queryJson,
            summaryText: "Here are local headlines.");

        var searchResult =
            "1. Local update\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/local\",\"title\":\"Local update\"}]";

        var mcp = new FakeMcpClient(returnValue: searchResult);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            UserLocationHint = "Rexburg, ID"
        };

        var result = await agent.ProcessAsync(userMessage);

        Assert.True(result.Success);
        var webSearchCalls = mcp.Calls.Where(c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(webSearchCalls);

        // The search query should contain location, NOT the greeting
        var lastSearch = webSearchCalls.Last();
        Assert.Contains("rexburg", lastSearch.Args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"query\":\"hello\"", lastSearch.Args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"query\":\"hey", lastSearch.Args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"query\":\"morning\"", lastSearch.Args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task News_QueryBuilder_FiltersGreetingsAsNoise()
    {
        // Direct QueryBuilder test: greeting tokens should be treated
        // as noise, causing the query to normalize to "top headlines"
        // (or similar) instead of keeping the greeting as the query.
        var fakeLlm = new FakeLlmClient((messages, _) =>
            new LlmResponse
            {
                IsComplete = true,
                Content = """{"query":"hello","recency":"any"}""",
                FinishReason = "stop"
            });
        var audit = new TestAuditLogger();
        var builder = new QueryBuilder(fakeLlm, audit);

        var result = await builder.BuildAsync(
            SearchMode.NewsAggregate,
            "hello! can you get me the local news?",
            entity: null,
            session: new SearchSession(),
            recentHistory: [],
            ct: CancellationToken.None);

        // "hello" should be stripped as noise — the query should NOT
        // be "hello". It should be normalized to something generic
        // like "top headlines" or the fallback topic.
        Assert.DoesNotContain("hello", result.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task News_RecencyDefaultsToDay_WhenLlmReturnsAny()
    {
        // Test the QueryBuilder directly: when the LLM returns "any"
        // for a news query, the resolver should override to "day" since
        // news is inherently time-sensitive.
        var fakeLlm = new FakeLlmClient((messages, _) =>
            new LlmResponse
            {
                IsComplete = true,
                Content = """{"query":"latest headlines","recency":"any"}""",
                FinishReason = "stop"
            });
        var audit = new TestAuditLogger();
        var builder = new QueryBuilder(fakeLlm, audit);

        var result = await builder.BuildAsync(
            SearchMode.NewsAggregate,
            "What's going on in the news?",
            entity: null,
            session: new SearchSession(),
            recentHistory: [],
            ct: CancellationToken.None);

        // Recency should be "day", not the LLM's "any"
        Assert.Equal("day", result.Recency);
    }

    [Fact]
    public async Task NewsSearch_EntityResolution_ProducesGoodQuery()
    {
        var llm = MakePipelineLlm(
            entityJson: """{"name":"SpaceX","type":"Org","hint":"Elon Musk's space company"}""",
            queryJson: """{"query":"SpaceX latest news","recency":"week"}""",
            summaryText: "SpaceX recently launched another Starship prototype.");

        var searchResult =
            "1. SpaceX launch update\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://spacex.com/news\",\"title\":\"SpaceX Launch Update\"}]";

        var mcp = new FakeMcpClient(returnValue: searchResult);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("what's the latest with SpaceX?");

        Assert.True(result.Success);
        Assert.Contains("SpaceX", result.Text, StringComparison.OrdinalIgnoreCase);

        // web_search should have been called with entity-aware query
        var webSearchCalls = mcp.Calls.Where(c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(webSearchCalls);
    }

    // ExistenceGuard test removed — feature was intentionally removed for latency reasons.

    [Fact]
    public async Task NewsToFollowUp_DeepDive_BrowsesPriorSource()
    {
        // Two-turn: news → follow-up deep dive
        const string sourceUrl = "https://example.com/spacex-article";
        var searchResult =
            "SpaceX news...\n" +
            "<!-- SOURCES_JSON -->\n" +
            $"[{{\"url\":\"{sourceUrl}\",\"title\":\"SpaceX Starship Test Flight\"}}]";

        var llm = MakePipelineLlm(
            entityJson: """{"name":"SpaceX","type":"Org","hint":"space company"}""",
            queryJson: """{"query":"SpaceX news","recency":"week"}""",
            summaryText: "SpaceX conducted a successful test flight.");

        var mcp = new FakeMcpClient((tool, args) =>
        {
            if (tool.Contains("search", StringComparison.OrdinalIgnoreCase))
                return searchResult;
            if (tool.Contains("browse", StringComparison.OrdinalIgnoreCase) ||
                tool.Contains("navigate", StringComparison.OrdinalIgnoreCase))
                return "Full article: SpaceX's Starship completed its test flight...";
            return "";
        });

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        // Turn 1: news search
        var first = await agent.ProcessAsync("SpaceX news this week");
        Assert.True(first.Success);

        // Turn 2: follow-up deep dive
        var second = await agent.ProcessAsync("tell me more about this");
        Assert.True(second.Success);

        // browser_navigate should have been called for the deep dive
        var browseCalls = mcp.Calls.Where(c =>
            c.Tool.Contains("browse", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Contains("navigate", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(browseCalls);
    }

    [Fact]
    public async Task FollowUp_MoreSources_SearchesRelatedCoverage()
    {
        const string sourceUrl = "https://example.com/article";
        var searchResult =
            "News...\n" +
            "<!-- SOURCES_JSON -->\n" +
            $"[{{\"url\":\"{sourceUrl}\",\"title\":\"Major Tech Layoffs 2026\"}}]";

        var llm = MakePipelineLlm(
            entityJson: """{"name":"","type":"none","hint":""}""",
            queryJson: """{"query":"tech layoffs 2026","recency":"week"}""",
            summaryText: "Multiple tech companies have announced layoffs.");

        var searchCount = 0;
        var mcp = new FakeMcpClient((tool, args) =>
        {
            if (tool.Contains("search", StringComparison.OrdinalIgnoreCase))
            {
                searchCount++;
                return searchResult;
            }
            if (tool.Contains("browse", StringComparison.OrdinalIgnoreCase) ||
                tool.Contains("navigate", StringComparison.OrdinalIgnoreCase))
                return "Full article content about tech layoffs...";
            return "";
        });

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        // Turn 1: news search
        var first = await agent.ProcessAsync("tech layoffs news this week");
        Assert.True(first.Success);

        var firstSearchCount = searchCount;

        // Turn 2: "more sources" follow-up
        var second = await agent.ProcessAsync("find more sources on this");
        Assert.True(second.Success);

        // More web_search calls should have happened on the follow-up
        Assert.True(searchCount > firstSearchCount,
            "Follow-up 'more sources' should trigger additional web searches");
    }

    [Fact]
    public async Task ModeRouting_CasualChat_SkipsSearchPipeline()
    {
        var llm = new FakeLlmClient(messages =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sysMsg.Contains("Classify")) return "chat";
            return "Hey there! How can I help you today?";
        });

        var mcp = new FakeMcpClient(returnValue: "");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("hey, how are you?");

        Assert.True(result.Success);

        // No web_search calls for casual chat
        var searchCalls = mcp.Calls.Where(c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase) &&
            c.Tool != "MemoryRetrieve").ToList();
        Assert.Empty(searchCalls);
    }

    [Fact]
    public async Task GuardrailsOff_WebSearchRouting_RemainsUnchanged()
    {
        var llm = MakePipelineLlm(
            entityJson: """{"name":"airport tsa id requirements","type":"topic","hint":"travel docs"}""",
            queryJson: """{"query":"airport tsa id requirements","recency":"any"}""",
            summaryText: "Bring an acceptable ID to airport security.");

        var searchResult =
            "1. TSA acceptable documents\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/tsa-id\",\"title\":\"TSA ID Requirements\"}]";

        var mcp = new FakeMcpClient(returnValue: searchResult);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {

        };

        var result = await agent.ProcessAsync("What are TSA ID requirements at the airport?");

        Assert.True(result.Success);
        Assert.False(result.GuardrailsUsed);
        Assert.Contains(mcp.Calls, c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NonDeterministicFact_EthanolBoilingPoint_CanUseWebSearch()
    {
        var llm = MakePipelineLlm(
            entityJson: """{"name":"boiling point of ethanol","type":"topic","hint":"chemistry"}""",
            queryJson: """{"query":"boiling point of ethanol","recency":"any"}""",
            summaryText: "At standard pressure, ethanol boils near 78.37 C.");

        var searchResult =
            "1. Ethanol boiling point reference\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/ethanol-boiling-point\",\"title\":\"Ethanol Boiling Point\"}]";

        var mcp = new FakeMcpClient(returnValue: searchResult);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("what's the boiling point of ethanol?");

        Assert.True(result.Success);
        Assert.Contains(mcp.Calls, c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MarketQuoteRequest_StaleSources_ReturnsFreshnessWarning_AndSkipsBrowse()
    {
        var llm = MakePipelineLlm(
            entityJson: """{"name":"Dow Jones","type":"Topic","hint":"US stock index"}""",
            queryJson: """{"query":"Dow Jones live quote","recency":"any"}""",
            summaryText: "The Dow Jones is up today.");

        var stalePublishedAt = DateTimeOffset.UtcNow.AddHours(-18).ToString("o");
        var searchResult =
            "1. Dow Jones market update — example.com\n" +
            "<!-- SOURCES_JSON -->\n" +
            $"[{{\"url\":\"https://example.com/dow\",\"title\":\"Dow Jones market update\",\"domain\":\"example.com\",\"publishedAt\":\"{stalePublishedAt}\"}}]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" => searchResult,
            "WebSearch" => searchResult,
            _ => "unexpected tool call"
        });

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("whats the dow jones at most recently?");

        Assert.True(result.Success);
        Assert.Contains("cannot safely report a current market quote", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase) &&
            c.Args.Contains("\"recency\":\"day\"", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c =>
            c.Tool.Contains("navigate", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Contains("browse", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GuardrailsAuto_UtilityBypassCalculator_StaysDeterministic()
    {
        var llm = new FakeLlmClient("LLM should not be called for calculator utility.");
        var mcp = new FakeMcpClient(returnValue: "should not be called");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {

        };

        var result = await agent.ProcessAsync("what is 9 plus 4?");

        Assert.True(result.Success);
        Assert.False(result.GuardrailsUsed);
        Assert.Contains("9 + 4 = **13**", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mcp.Calls, c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase));
    }
}

#endregion

#region ── Local Business Detection + Briefing Signals ───────────────────

public class LocalBusinessDetectionTests
{
    [Fact]
    public async Task DemoSequence_LogicPuzzle_To_Discovery_To_Briefing()
    {
        var llm = new FakeLlmClient((messages, tools) =>
        {
            var userText = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";

            if (userText.Contains("walk or drive", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { Content = "Drive, because you need the car at the destination.", IsComplete = true };

            if (userText.Contains("bakery nearby", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { Content = "Here are some bakeries nearby: Left Bank Pastry.", IsComplete = true };

            if (userText.Contains("Left Bank Pastry", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { Content = "Here is a briefing on Left Bank Pastry.", IsComplete = true };

            return new LlmResponse { Content = "Generic response.", IsComplete = true };
        });

        var callCount = 0;
        var mcp = new FakeMcpClient((tool, args) =>
        {
            callCount++;
            if (tool.Contains("search"))
            {
                if (args.Contains("bakery"))
                {
                    return "Here is some raw search data about bakeries.\n" +
                           "<!-- SOURCES_JSON -->\n" +
                           "[{\"url\":\"https://example.com/bakeries\",\"title\":\"Best Bakeries in Olympia\",\"domain\":\"example.com\"}]";
                }
            }
                        if (tool.Contains("browser_navigate") || tool.Contains("BrowserNavigate"))
                        {
                                return "1. Left Bank Pastry\n2. Wagner's European Bakery and Cafe";
                        }
            if (tool.Contains("places_lookup") || tool.Contains("PlacesLookup"))
            {
                                if (args.Contains("Wagner", StringComparison.OrdinalIgnoreCase))
                                {
                                        return """
                                                {
                                                    "place": {
                                                        "name": "Wagner's European Bakery and Cafe",
                                                        "address": "1013 Capitol Way S, Olympia, WA",
                                                        "rating": 4.5,
                                                        "userRatingsTotal": 850,
                                                        "openNow": true
                                                    }
                                                }
                                                """;
                                }

                                return """
                                        {
                                            "place": {
                                                "name": "Left Bank Pastry",
                                                "address": "1008 4th Ave E, Olympia, WA",
                                                "rating": 4.7,
                                                "userRatingsTotal": 640,
                                                "openNow": true
                                            }
                                        }
                                        """;
            }
            return "dummy content";
        });

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        // Step 1: Logic Question
        var result1 = await agent.ProcessAsync("The car wash is 50m away. Do I walk or drive?");
        Assert.True(result1.Success);
        // Expect 1 call (memory_retrieve). Logic puzzle should NOT trigger search.
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Drive", result1.Text);

        // Step 2: Discovery
        var result2 = await agent.ProcessAsync("Show me a bakery nearby");
        Assert.True(result2.Success);
        Assert.Contains(mcp.Calls, c => c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("bakeries", result2.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nearby", result2.Text, StringComparison.OrdinalIgnoreCase);

        // Ensure the session recorded that this was a local business discovery
        var sessionFlagFound = false;
        var orchestratorField = typeof(AgentOrchestrator).GetField("_searchOrchestrator", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (orchestratorField?.GetValue(agent) is SirThaddeus.Agent.Search.SearchOrchestrator searchOrch)
        {
            sessionFlagFound = searchOrch.Session.LastWasLocalBusinessDiscovery;
        }
        Assert.True(sessionFlagFound, "Session should flag the search as a local business discovery.");

        var priorCallCount = mcp.Calls.Count;

        // Step 3: Follow-up Briefing
        var result3 = await agent.ProcessAsync("Show me Left Bank Pastry");
        Assert.True(result3.Success);

        // It should have routed to the briefing pipeline and called places_lookup (or search again)
        Assert.True(mcp.Calls.Count > priorCallCount, "Should have called tools for the briefing.");
        Assert.NotNull(result3.DeepDiveBriefing);
    }

    [Theory]
    [InlineData("florists nearby", true)]
    [InlineData("restaurants near me", true)]
    [InlineData("any coffee shop around me", true)]
    [InlineData("dentist close by", true)]
    [InlineData("pharmacy in my area", true)]
    [InlineData("bakery around here", true)]
    [InlineData("bakeries nearby", true)]             // -ies plural
    [InlineData("some local delis please", true)]     // deli keyword + local cue
    [InlineData("pharmacies near me", true)]          // -ies plural
    [InlineData("groceries near me", true)]           // -ies plural
    [InlineData("find me some bakeries nearby", true)]
    [InlineData("florists in portland", false)]       // no proximity cue
    [InlineData("tell me about quantum computing", false)] // no business term
    [InlineData("what is a florist", false)]           // no proximity cue
    [InlineData("nearby attractions", false)]          // no business term
    public void HasLocalBusinessProximitySignals_DetectsCorrectly(string input, bool expected)
    {
        var result = IntentFeatureExtractor.HasLocalBusinessProximitySignals(input.ToLowerInvariant());
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task LocalBakeryDiscovery_FiltersIrrelevantGuideResults()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"local bakeries","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var searchResult =
            "Top local results\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://www.theolympian.com/west-olympia-woman\",\"title\":\"The West Olympia Woman (Community-Supported Bread)\",\"domain\":\"theolympian.com\",\"snippet\":\"She has been baking out of her home for years and sells community-supported bread.\"}," +
            "{\"url\":\"https://example.com/dairy-free-guide\",\"title\":\"Washington Dairy-Free Restaurant Guide\",\"domain\":\"example.com\",\"snippet\":\"A statewide guide to dairy-free restaurants and dessert options.\"}" +
            "]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" => searchResult,
            "WebSearch" => searchResult,
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var result = await orchestrator.ExecuteAsync(
            "can you bring up some local bakeries, please?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("bakery nearby", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("West Olympia Woman", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dairy-Free Restaurant Guide", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalBakeryDiscovery_E2E_DropsGenericGuideHeading_AndReturnsRealBakeries()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"bakeries near me olympia wa","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var searchResult =
            "Top local results\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://example.com/olympia-gluten-free-desserts\",\"title\":\"Where to Get Gluten-Free Desserts in Olympia\",\"domain\":\"example.com\",\"snippet\":\"A curated local dessert guide.\"}" +
            "]";

        var articleResult = """
            Where to Get Gluten-Free Desserts in Olympia
            1. Left Bank Pastry
            2. Wagner's European Bakery and Cafe
            """;

        string PlaceJson(string name, string address) =>
            $$"""
            {
              "place": {
                "name": "{{name}}",
                "address": "{{address}}",
                "rating": 4.6,
                "userRatingsTotal": 120,
                "openNow": true
              }
            }
            """;

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" => searchResult,
            "browser_navigate" or "BrowserNavigate" => articleResult,
            "places_lookup" or "PlacesLookup" when args.Contains("Wagner", StringComparison.OrdinalIgnoreCase)
                => PlaceJson("Wagner's European Bakery and Cafe", "1013 Capitol Way S, Olympia, WA"),
            "places_lookup" or "PlacesLookup"
                => PlaceJson("Left Bank Pastry", "108 5th Ave SW, Olympia, WA"),
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var result = await orchestrator.ExecuteAsync(
            "can you pull up some bakeries near me?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Left Bank Pastry", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Wagner's European Bakery", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Where to Get Gluten-Free Desserts in Olympia", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            mcp.Calls.Select(c => c.Tool),
            t => t.Equals("places_lookup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_TargetsTenResults_WithStrictThenBackfill()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"olympia florists","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var sourcesJson =
            "[" +
            "{\"url\":\"https://example.com/florist-1\",\"title\":\"Olympia Florist One\",\"domain\":\"example.com\",\"snippet\":\"Local florist and flower delivery\"}," +
            "{\"url\":\"https://example.com/florist-2\",\"title\":\"Flower House Olympia\",\"domain\":\"example.com\",\"snippet\":\"Family flower shop\"}," +
            "{\"url\":\"https://example.com/place-3\",\"title\":\"Olympia Directory 3\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-4\",\"title\":\"Olympia Directory 4\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-5\",\"title\":\"Olympia Directory 5\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-6\",\"title\":\"Olympia Directory 6\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-7\",\"title\":\"Olympia Directory 7\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-8\",\"title\":\"Olympia Directory 8\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-9\",\"title\":\"Olympia Directory 9\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-10\",\"title\":\"Olympia Directory 10\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-11\",\"title\":\"Olympia Directory 11\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-12\",\"title\":\"Olympia Directory 12\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-13\",\"title\":\"Olympia Directory 13\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-14\",\"title\":\"Olympia Directory 14\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-15\",\"title\":\"Olympia Directory 15\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-16\",\"title\":\"Olympia Directory 16\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-17\",\"title\":\"Olympia Directory 17\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-18\",\"title\":\"Olympia Directory 18\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-19\",\"title\":\"Olympia Directory 19\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-20\",\"title\":\"Olympia Directory 20\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-21\",\"title\":\"Olympia Directory 21\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}," +
            "{\"url\":\"https://example.com/place-22\",\"title\":\"Olympia Directory 22\",\"domain\":\"example.com\",\"snippet\":\"Regional listings\"}" +
            "]";

        var searchResult = "Top local results\n<!-- SOURCES_JSON -->\n" + sourcesJson;

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" => searchResult,
            "WebSearch" => searchResult,
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var result = await orchestrator.ExecuteAsync(
            "show me florists nearby",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("florists nearby", result.Text, StringComparison.OrdinalIgnoreCase);

        var bulletCount = Regex.Matches(result.Text, "^- \\*\\*", RegexOptions.Multiline).Count;
        Assert.Equal(10, bulletCount);

        var webCall = mcp.Calls.FirstOrDefault(c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"maxResults\":10", webCall.Args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalBusinessDiscovery_PrefersOpenPlacesDiscovery_OverWebSearch()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"olympia florists","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var discoverResult =
            "{" +
            "\"provider\":\"osm_overpass\"," +
            "\"query\":\"show me florists nearby\"," +
            "\"userLocationHint\":\"Olympia, WA\"," +
            "\"resolvedLocation\":\"Olympia, Washington, US\"," +
            "\"center\":{\"label\":\"Olympia, Washington, US\",\"latitude\":47.0414,\"longitude\":-122.8931}," +
            "\"options\":{\"maxResults\":10,\"radiusMeters\":4000,\"locale\":\"en-US\"}," +
            "\"results\":[" +
            "{\"id\":\"node:1\",\"name\":\"Fleurae\",\"address\":\"101 Capitol Way S, Olympia, WA\",\"category\":\"florist\",\"latitude\":47.0420,\"longitude\":-122.9000,\"distanceMeters\":420,\"osmUrl\":\"https://www.openstreetmap.org/node/1\",\"tags\":{\"shop\":\"florist\"}}," +
            "{\"id\":\"node:2\",\"name\":\"Buds and Blooms\",\"address\":\"517 Washington St SE, Olympia, WA\",\"category\":\"florist\",\"latitude\":47.0411,\"longitude\":-122.8912,\"distanceMeters\":180,\"osmUrl\":\"https://www.openstreetmap.org/node/2\",\"tags\":{\"shop\":\"florist\"}}" +
            "]," +
            "\"errors\":[]," +
            "\"cache\":{\"hit\":false,\"ageSeconds\":0}" +
            "}";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "places_discover" or "PlacesDiscover" => discoverResult,
            "web_search" or "WebSearch" => throw new InvalidOperationException("web_search should not run when open places discovery succeeds"),
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var result = await orchestrator.ExecuteAsync(
            "show me florists nearby",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Fleurae", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Buds and Blooms", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("places_discover", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_OpenPlacesNoResults_FallsBackToPlacesLookup()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"olympia bakeries","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var noResultsDiscover =
            "{" +
            "\"provider\":\"osm_overpass\"," +
            "\"query\":\"bakeries nearby\"," +
            "\"userLocationHint\":\"Olympia, WA\"," +
            "\"resolvedLocation\":\"Olympia, Washington, US\"," +
            "\"center\":{\"label\":\"Olympia, Washington, US\",\"latitude\":47.0414,\"longitude\":-122.8931}," +
            "\"options\":{\"maxResults\":10,\"radiusMeters\":4000,\"locale\":\"en-US\"}," +
            "\"results\":[]," +
            "\"errors\":[]," +
            "\"cache\":{\"hit\":false,\"ageSeconds\":0}" +
            "}";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "places_discover" or "PlacesDiscover" => noResultsDiscover,
            "places_lookup" or "PlacesLookup" => """
                {
                  "place": {
                    "name": "Left Bank Pastry",
                    "address": "108 5th Ave SW, Olympia, WA",
                    "rating": 4.7,
                    "userRatingsTotal": 640,
                    "openNow": true
                  }
                }
                """,
            "web_search" or "WebSearch" => throw new InvalidOperationException("web_search should not run after an open places no-results response"),
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var result = await orchestrator.ExecuteAsync(
            "show me bakeries nearby",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Left Bank Pastry", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("places_discover", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("places_lookup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_OpenPlacesNoResults_FallsBackToWebSearch_WhenDirectLookupIsEmpty()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"local bakeries olympia","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var noResultsDiscover =
            "{" +
            "\"provider\":\"osm_overpass\"," +
            "\"query\":\"bakeries nearby\"," +
            "\"userLocationHint\":\"Olympia, WA\"," +
            "\"resolvedLocation\":\"Olympia, Washington, US\"," +
            "\"center\":{\"label\":\"Olympia, Washington, US\",\"latitude\":47.0414,\"longitude\":-122.8931}," +
            "\"options\":{\"maxResults\":10,\"radiusMeters\":4000,\"locale\":\"en-US\"}," +
            "\"results\":[]," +
            "\"errors\":[\"unsupported category mapping\"]," +
            "\"cache\":{\"hit\":false,\"ageSeconds\":0}" +
            "}";

        var webSearchResult =
            "Downtown bakery picks\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://leftbankpastry.com\",\"title\":\"Left Bank Pastry - Bakery in Olympia, WA\",\"domain\":\"leftbankpastry.com\",\"snippet\":\"Neighborhood bakery in downtown Olympia.\"}" +
            "]";

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "places_discover" or "PlacesDiscover" => noResultsDiscover,
            "places_lookup" or "PlacesLookup" when args.Contains("Left Bank Pastry Olympia, WA", StringComparison.OrdinalIgnoreCase) =>
                """
                {
                  "place": {
                    "name": "Left Bank Pastry",
                    "address": "108 5th Ave SW, Olympia, WA",
                    "rating": 4.7,
                    "userRatingsTotal": 640,
                    "openNow": true
                  }
                }
                """,
            "places_lookup" or "PlacesLookup" => "{\"place\":null,\"sources\":[]}",
            "web_search" or "WebSearch" => webSearchResult,
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var result = await orchestrator.ExecuteAsync(
            "can you bring me up some local bakeries?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Left Bank Pastry", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("places_discover", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_WebSearchNoResults_ReturnsNoResultsMessage()
    {
        // Regression: when web_search returns no results for a local
        // business query, the pipeline used to fall through to a
        // browser_navigate Google SERP fallback that produced a single
        // fake source titled "Google Search". The fix skips the browser
        // fallback entirely and returns an honest no-results message.
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"local florists olympia","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        // web_search returns the canonical no-results payload
        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" => "No results found for local florists olympia",
            "WebSearch"  => "No results found for local florists olympia",
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var result = await orchestrator.ExecuteAsync(
            "hey thadds -- can you bring me up local florists?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain("Google Search", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);

        // browser_navigate should never have been called
        Assert.DoesNotContain(
            mcp.Calls.Select(c => c.Tool),
            t => t.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_WebSearchNoResults_FallsBackToPlacesLookup()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"local bakeries olympia","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" => "No results found for local bakeries olympia",
            "places_lookup" or "PlacesLookup" => """
                {
                  "place": {
                    "name": "Left Bank Pastry",
                    "address": "108 5th Ave SW, Olympia, WA",
                    "rating": 4.7,
                    "userRatingsTotal": 640,
                    "openNow": true
                  }
                }
                """,
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var result = await orchestrator.ExecuteAsync(
            "can you bring me up some local bakeries?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Left Bank Pastry", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            mcp.Calls.Select(c => c.Tool),
            t => t.Equals("places_lookup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_SparseExplicitLocationResult_RetriesAndUsesDirectoryResponse()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good florist in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var webSearchCalls = 0;
        var sparseResult =
            "1. \"Google Search\" — google.com\n" +
            "   Search results page\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.google.com/search?q=florist+hillsboro\",\"title\":\"Google Search\",\"domain\":\"google.com\",\"excerpt\":\"Search results page\"}]";

        var recoveredResult =
            "1. \"Best Florists in Hillsboro, OR\" — yelp.com\n" +
            "   Local directory of florist options in Hillsboro, OR.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.yelp.com/search?find_desc=Florists&find_loc=Hillsboro%2C+OR\",\"title\":\"Best Florists in Hillsboro, OR\",\"domain\":\"yelp.com\",\"excerpt\":\"Local directory of florist options in Hillsboro, OR.\"}]";

        var mcp = new FakeMcpClient((tool, _) =>
        {
            if (!tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
                !tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            webSearchCalls++;
            return webSearchCalls == 1 ? sparseResult : recoveredResult;
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good florist in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Here are the live florists results I found", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("yelp.com", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(webSearchCalls >= 2, "Expected a retry after the sparse unusable first result.");
    }

    [Fact]
    public async Task LocalBusinessDiscovery_GenericLandingPage_RetriesWithAliasAndUsesLocationDirectoryResponse()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good florist in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var genericLandingResult =
            "1. \"Flower Delivery: Send Flowers Online | FTD\" — ftd.com\n" +
            "   National flower delivery landing page.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.ftd.com/\",\"title\":\"Flower Delivery: Send Flowers Online | FTD\",\"domain\":\"ftd.com\",\"excerpt\":\"National flower delivery landing page.\"}]";

        var recoveredDirectoryResult =
            "1. \"Best Florists in Hillsboro, OR\" — yelp.com\n" +
            "   Local directory of florist options in Hillsboro, OR.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.yelp.com/search?find_desc=Florists&find_loc=Hillsboro%2C+OR\",\"title\":\"Best Florists in Hillsboro, OR\",\"domain\":\"yelp.com\",\"excerpt\":\"Local directory of florist options in Hillsboro, OR.\"}]";

        var mcp = new FakeMcpClient((tool, args) =>
        {
            if (!tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
                !tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return args.Contains("flower shop", StringComparison.OrdinalIgnoreCase)
                ? recoveredDirectoryResult
                : genericLandingResult;
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good florist in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Here are the live florists results I found", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("yelp.com", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ftd.com", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
            call.Args.Contains("flower shop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_ExplicitLocationRejectsOutOfAreaMatches_AndRetriesAlias()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good deli in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var outOfAreaResult =
            "1. \"The Marketplace Deli – San Diego | Sandwiches, Catering, Pizza and More\" — themarketplacesd.com\n" +
            "   The Marketplace Deli in San Diego serves sandwiches, soups, and pizza.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://themarketplacesd.com/\",\"title\":\"The Marketplace Deli – San Diego | Sandwiches, Catering, Pizza and More\",\"domain\":\"themarketplacesd.com\",\"excerpt\":\"The Marketplace Deli in San Diego serves sandwiches, soups, and pizza.\"}]";

        var recoveredDirectoryResult =
            "1. \"Best Delis in Hillsboro, OR\" — yelp.com\n" +
            "   Local deli listings in Hillsboro, OR.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.yelp.com/search?find_desc=Delis&find_loc=Hillsboro%2C+OR\",\"title\":\"Best Delis in Hillsboro, OR\",\"domain\":\"yelp.com\",\"excerpt\":\"Local deli listings in Hillsboro, OR.\"}]";

        var mcp = new FakeMcpClient((tool, args) =>
        {
            if (!tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
                !tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return args.Contains("sandwich shop", StringComparison.OrdinalIgnoreCase)
                ? recoveredDirectoryResult
                : outOfAreaResult;
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good deli in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Here are the live delis results I found", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("yelp.com", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("San Diego", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
            call.Args.Contains("sandwich shop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_ExplicitLocationRejectsSameCityWrongStateMatches_AndRetriesAlias()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good florist in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var wrongStateResult =
            "1. \"Hillsboro, IL Flower Shops\" — loc8nearme.com\n" +
            "   Flower shops in Hillsboro, Illinois.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://loc8nearme.com/illinois/hillsboro/flower-shops/\",\"title\":\"Hillsboro, IL Flower Shops\",\"domain\":\"loc8nearme.com\",\"excerpt\":\"Flower shops in Hillsboro, Illinois.\"}]";

        var recoveredDirectoryResult =
            "1. \"Best Florists in Hillsboro, OR\" — yelp.com\n" +
            "   Local directory of florist options in Hillsboro, OR.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.yelp.com/search?find_desc=Florists&find_loc=Hillsboro%2C+OR\",\"title\":\"Best Florists in Hillsboro, OR\",\"domain\":\"yelp.com\",\"excerpt\":\"Local directory of florist options in Hillsboro, OR.\"}]";

        var mcp = new FakeMcpClient((tool, args) =>
        {
            if (!tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
                !tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return args.Contains("flower shop", StringComparison.OrdinalIgnoreCase)
                ? recoveredDirectoryResult
                : wrongStateResult;
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good florist in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Here are the live florists results I found", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("yelp.com", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hillsboro, IL", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
            call.Args.Contains("flower shop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_ExplicitLocationDeliRetry_UsesSecondaryAliasBeforeGivingUp()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good deli in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var outOfAreaResult =
            "1. \"Best Delis in Seattle, WA\" — restaurantji.com\n" +
            "   Browse Seattle deli listings and local reviews.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.restaurantji.com/wa/seattle/deli/\",\"title\":\"Best Delis in Seattle, WA\",\"domain\":\"restaurantji.com\",\"excerpt\":\"Browse Seattle deli listings and local reviews.\"}]";

        var recoveredDirectoryResult =
            "1. \"Potbelly Sandwich Shop, Hillsboro\" — tripadvisor.com\n" +
            "   Order food online at Potbelly Sandwich Shop, Hillsboro and see local reviews.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.tripadvisor.com/Restaurant_Review-g51730-d8089140-Reviews-Potbelly_Sandwich_Shop-Hillsboro_Oregon.html\",\"title\":\"Potbelly Sandwich Shop, Hillsboro\",\"domain\":\"tripadvisor.com\",\"excerpt\":\"Order food online at Potbelly Sandwich Shop, Hillsboro and see local reviews.\"}]";

        var mcp = new FakeMcpClient((tool, args) =>
        {
            if (!tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
                !tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return args.Contains("delicatessen", StringComparison.OrdinalIgnoreCase)
                ? recoveredDirectoryResult
                : outOfAreaResult;
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good deli in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Potbelly Sandwich Shop", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Seattle", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
            call.Args.Contains("delicatessen in Hillsboro, OR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_ExplicitLocationDeliRetry_UsesDirectDirectoryBrowserFallbackAfterAliasesFail()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good deli in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var outOfAreaResult =
            "1. \"McAlister's Deli Peoria\" — locations.mcalistersdeli.com\n" +
            "   Visit your local deli restaurant and sandwich shop in Peoria, AZ.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://locations.mcalistersdeli.com/az/peoria\",\"title\":\"McAlister's Deli Peoria\",\"domain\":\"locations.mcalistersdeli.com\",\"excerpt\":\"Visit your local deli restaurant and sandwich shop in Peoria, AZ.\"}]";

        var browserResult = """
            Title: Best Delis in Hillsboro, OR - Yelp

            Cheba Hut Toasted Subs
            Neighborhood deli in Hillsboro, OR.
            """;

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" => outOfAreaResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("yelp.com/search", StringComparison.OrdinalIgnoreCase) => browserResult,
            "places_lookup" or "PlacesLookup" =>
                """
                {
                  "provider": "google_places",
                  "query": "delis near Hillsboro, OR",
                  "error": "Google Places API key is not configured.",
                  "place": null,
                  "sources": []
                }
                """,
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good deli in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Cheba Hut Toasted Subs", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Peoria", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.Tool.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
            call.Args.Contains("yelp.com/search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_ExplicitLocationDeliRetry_UsesTrustworthyNoMatchResponse_WhenReturnedPagesDoNotYieldShortlist()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good deli in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var outOfAreaResult =
            "1. \"McAlister's Deli Peoria\" — locations.mcalistersdeli.com\n" +
            "   Visit your local deli restaurant and sandwich shop in Peoria, AZ.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://locations.mcalistersdeli.com/az/peoria\",\"title\":\"McAlister's Deli Peoria\",\"domain\":\"locations.mcalistersdeli.com\",\"excerpt\":\"Visit your local deli restaurant and sandwich shop in Peoria, AZ.\"}]";

        const string cancelledError = "{" +
            "\"error\":{\"code\":\"tool_error\",\"message\":\"Cancelled\",\"retriable\":false}}";

        var yelpBrowserResult = """
            Title: Best Delis in Hillsboro, OR - Yelp

            Find the best delis near Hillsboro from recent reviews and neighborhood listings.
            """;

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" => outOfAreaResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("yelp.com/search", StringComparison.OrdinalIgnoreCase) => yelpBrowserResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("restaurantji.com/or/hillsboro/deli/", StringComparison.OrdinalIgnoreCase) => cancelledError,
            "places_lookup" or "PlacesLookup" => cancelledError,
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good deli in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("trustworthy deli recommendation", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.Tool.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
            call.Args.Contains("restaurantji.com/or/hillsboro/deli/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_NoTrustworthyWebCandidates_UsesDirectPlacesFallback_WhenAdvancedDiscoveryDisabled()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good deli in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var outOfAreaResult =
            "1. \"The Marketplace Deli – San Diego | Sandwiches, Catering, Pizza and More\" — themarketplacesd.com\n" +
            "   The Marketplace Deli in San Diego serves sandwiches, soups, and pizza.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://themarketplacesd.com/\",\"title\":\"The Marketplace Deli – San Diego | Sandwiches, Catering, Pizza and More\",\"domain\":\"themarketplacesd.com\",\"excerpt\":\"The Marketplace Deli in San Diego serves sandwiches, soups, and pizza.\"}]";

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" => outOfAreaResult,
            "places_lookup" or "PlacesLookup" when args.Contains("Hillsboro, OR", StringComparison.OrdinalIgnoreCase) =>
                """
                {
                  "place": {
                    "name": "Biscuit Delicatessen",
                    "address": "171 NE 3rd Ave, Hillsboro, OR",
                    "rating": 4.6,
                    "userRatingsTotal": 412,
                    "openNow": true
                  }
                }
                """,
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good deli in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Biscuit Delicatessen", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.Tool.Equals("places_lookup", StringComparison.OrdinalIgnoreCase) &&
            call.Args.Contains("Hillsboro, OR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_NoTrustworthyWebCandidates_UsesBrowserFallbackNames_WhenPlacesUnavailable()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good deli in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var localDirectoryResult =
            "1. \"Delis\" — restaurantji.com\n" +
            "   Browse Hillsboro deli listings and local reviews.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.restaurantji.com/or/hillsboro/delis/\",\"title\":\"Delis\",\"domain\":\"restaurantji.com\",\"excerpt\":\"Browse Hillsboro deli listings and local reviews.\"}]";

        var browserResult = """
            Best sandwich shops in Hillsboro, OR

            1. Biscuit Delicatessen
            2. Main Street Deli
            3. Hillsboro Sandwich Spot
            """;

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" => localDirectoryResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("restaurantji", StringComparison.OrdinalIgnoreCase) => browserResult,
            "places_lookup" or "PlacesLookup" =>
                """
                {
                  "provider": "google_places",
                  "query": "delis near Hillsboro, OR",
                  "error": "Google Places API key is not configured.",
                  "place": null,
                  "sources": []
                }
                """,
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good deli in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Biscuit Delicatessen", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Main Street Deli", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.Tool.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
            call.Args.Contains("restaurantji", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_BrowserFallbackNames_CarryDirectoryEvidenceYear_WhenAvailable()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good deli in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var localDirectoryResult =
            "1. \"Delis\" — restaurantji.com\n" +
            "   Browse Hillsboro deli listings and local reviews.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.restaurantji.com/or/hillsboro/delis/\",\"title\":\"Delis\",\"domain\":\"restaurantji.com\",\"excerpt\":\"Browse Hillsboro deli listings and local reviews.\"}]";

        var browserResult = """
            Title: Best delis near Hillsboro, OR - 2026 Restaurantji

            1. Biscuit Delicatessen
            7418 W Baseline Rd, Hillsboro
            """;

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" => localDirectoryResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("restaurantji", StringComparison.OrdinalIgnoreCase) => browserResult,
            "places_lookup" or "PlacesLookup" =>
                """
                {
                  "provider": "google_places",
                  "query": "delis near Hillsboro, OR",
                  "error": "Google Places API key is not configured.",
                  "place": null,
                  "sources": []
                }
                """,
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good deli in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Biscuit Delicatessen", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalBusinessDiscovery_NoParsedSearchSources_UsesDirectDirectoryBrowserFallback_ForExplicitLocation()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good deli in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var textOnlySearchResult =
            "1. \"Best Delis in Hillsboro, OR\" — yelp.com\n" +
            "   Local deli listings in Hillsboro, OR.";

        var browserResult = """
            Title: Best Delis in Hillsboro, OR - Yelp

            Cheba Hut Toasted Subs
            Neighborhood deli in Hillsboro, OR.
            """;

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" => textOnlySearchResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("yelp.com/search", StringComparison.OrdinalIgnoreCase) => browserResult,
            "places_lookup" or "PlacesLookup" =>
                """
                {
                  "provider": "google_places",
                  "query": "delis near Hillsboro, OR",
                  "error": "Google Places API key is not configured.",
                  "place": null,
                  "sources": []
                }
                """,
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good deli in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Cheba Hut Toasted Subs", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.Tool.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
            call.Args.Contains("yelp.com/search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_FloristFallback_UsesStaticDirectoryBrowserFallback_WhenYelpIsEmpty()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good florist in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var textOnlySearchResult =
            "1. \"Best Florists in Hillsboro, OR\" — yelp.com\n" +
            "   Local florist listings in Hillsboro, OR.";

        var floristDirectoryBrowserResult = """
            Florists in Hillsboro, OR

            1. Flowers By Burkhardt's
            6318 SE Virginia St, Hillsboro, OR 97123

            2. Flowers By Zsuzsana
            928 NE Orenco Station Loop, Hillsboro, OR 97124
            """;

        var yelpBrowserResult = """
            One last step
            Please solve the challenge below to continue.
            cf-turnstile
            """;

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" when
                args.Contains("site:chamberofcommerce.com", StringComparison.OrdinalIgnoreCase) ||
                args.Contains("site:loc8nearme.com", StringComparison.OrdinalIgnoreCase) ||
                args.Contains("\"query\":\"local florist Hillsboro OR", StringComparison.OrdinalIgnoreCase) ||
                args.Contains("\"query\":\"flower shop Hillsboro OR", StringComparison.OrdinalIgnoreCase)
                => string.Empty,
            "web_search" or "WebSearch" => textOnlySearchResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("superpages.com/hillsboro-or/florists", StringComparison.OrdinalIgnoreCase) => floristDirectoryBrowserResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("yellowpages.com/hillsboro-or/florists", StringComparison.OrdinalIgnoreCase) => floristDirectoryBrowserResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("yelp.com/search", StringComparison.OrdinalIgnoreCase) => yelpBrowserResult,
            "places_lookup" or "PlacesLookup" =>
                """
                {
                  "provider": "google_places",
                  "query": "florists near Hillsboro, OR",
                  "error": "Google Places API key is not configured.",
                  "place": null,
                  "sources": []
                }
                """,
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good florist in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Flowers By Burkhardt's", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.Tool.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
            call.Args.Contains("superpages.com/hillsboro-or/florists", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_FloristFallback_PrefersRecoverySearchBeforeStaticDirectories()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good florist in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var initialSearchResult =
            "1. \"Best Florists in Hillsboro, OR\" — yelp.com\n" +
            "   Local florist listings in Hillsboro, OR.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.yelp.com/search?cflt=florists&find_loc=Hillsboro%2C%20OR\",\"title\":\"Best Florists in Hillsboro, OR\",\"domain\":\"yelp.com\",\"excerpt\":\"Local florist listings in Hillsboro, OR.\"}]";

        var recoveryTextOnlyResult =
            "1. \"Florists - Hillsboro, OR | City of Hillsboro, OR | Chamber of Commerce\" — chamberofcommerce.com\n" +
            "   Find local florists in Hillsboro, OR.\n\n" +
            "2. \"Florist Hillsboro OR | Terry's Florist\" — terrysflorist.com\n" +
            "   Flower delivery catalog for Hillsboro, OR.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.chamberofcommerce.com/business-directory/oregon/hillsboro/florist/2033-hill-florist-and-gifts\",\"title\":\"Florists - Hillsboro, OR | City of Hillsboro, OR | Chamber of Commerce\",\"domain\":\"chamberofcommerce.com\",\"excerpt\":\"Find local florists in Hillsboro, OR.\"}," +
            "{\"url\":\"https://terrysflorist.com/florists/oregon/hillsboro/\",\"title\":\"Florist Hillsboro OR | Terry's Florist\",\"domain\":\"terrysflorist.com\",\"excerpt\":\"Flower delivery catalog for Hillsboro, OR.\"}]";

        var chamberBrowserResult = """
            Hill Florist & Gifts
            111 SE 3rd Ave Ste A, Hillsboro, OR 97123

            Flowers by Zsuzsana
            928 NE Orenco Station Loop, Hillsboro, OR 97124
            """;

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" when args.Contains("site:chamberofcommerce.com", StringComparison.OrdinalIgnoreCase) => recoveryTextOnlyResult,
            "web_search" or "WebSearch" => initialSearchResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("chamberofcommerce.com", StringComparison.OrdinalIgnoreCase) => chamberBrowserResult,
            "browser_navigate" or "BrowserNavigate" => throw new InvalidOperationException("static-directory browser fallback should not run when florist recovery search has a usable chamber source"),
            "places_lookup" or "PlacesLookup" =>
                """
                {
                  "provider": "google_places",
                  "query": "florists near Hillsboro, OR",
                  "error": "Google Places API key is not configured.",
                  "place": null,
                  "sources": []
                }
                """,
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good florist in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("florist", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("City of Hillsboro", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Terry's Florist", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mcp.Calls, call =>
            call.Tool.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
            (call.Args.Contains("superpages.com", StringComparison.OrdinalIgnoreCase) ||
             call.Args.Contains("yellowpages.com", StringComparison.OrdinalIgnoreCase) ||
             call.Args.Contains("yelp.com/search", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(mcp.Calls, call =>
            call.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
            call.Args.Contains("site:chamberofcommerce.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_FloristFallback_RejectsProductCatalogNames()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good florist in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var directoryResult =
            "1. \"Best Florists in Hillsboro, OR\" — yelp.com\n" +
            "   Local florist listings in Hillsboro, OR.\n\n" +
            "2. \"The Flower Shop - Emissary Blooms\" — hillsborochamber.example\n" +
            "   Florist and gifts in Hillsboro, OR.\n\n" +
            "3. \"Florist Hillsboro OR | Terry's Florist\" — terrysflorist.com\n" +
            "   Flower delivery catalog for Hillsboro, OR.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.yelp.com/search?find_desc=Florists&find_loc=Hillsboro%2C+OR\",\"title\":\"Best Florists in Hillsboro, OR\",\"domain\":\"yelp.com\",\"excerpt\":\"Local florist listings in Hillsboro, OR.\"}," +
            "{\"url\":\"https://business.hillsborochamber.example/the-flower-shop-emissary-blooms.htm\",\"title\":\"The Flower Shop - Emissary Blooms\",\"domain\":\"hillsborochamber.example\",\"excerpt\":\"Florist and gifts in Hillsboro, OR.\"}," +
            "{\"url\":\"https://terrysflorist.com/florists/oregon/hillsboro/\",\"title\":\"Florist Hillsboro OR | Terry's Florist\",\"domain\":\"terrysflorist.com\",\"excerpt\":\"Flower delivery catalog for Hillsboro, OR.\"}]";

        var chamberBrowserResult = """
            ## [Emissary Blooms](https://business.hillsborochamber.example/the-flower-shop-emissary-blooms.htm)
            450 E Main St, Hillsboro, OR 97123
            """;

        var terryBrowserResult = """
            Happy Blooms Bouquet
            Spring Fling
            Comforting Standing Spray
            Terry's Florist
            """;

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" => directoryResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("hillsborochamber.example", StringComparison.OrdinalIgnoreCase) => chamberBrowserResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("terrysflorist.com", StringComparison.OrdinalIgnoreCase) => terryBrowserResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("yelp.com/search", StringComparison.OrdinalIgnoreCase) => "One last step\ncf-turnstile",
            "places_lookup" or "PlacesLookup" =>
                """
                {
                  "provider": "google_places",
                  "query": "florists near Hillsboro, OR",
                  "error": "Google Places API key is not configured.",
                  "place": null,
                  "sources": []
                }
                """,
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good florist in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Emissary Blooms", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Happy Blooms Bouquet", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Comforting Standing Spray", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Terry's Florist", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalBusinessDiscovery_FloristFallback_UsesScopedRecoverySearch_WhenStaticDirectoriesAreBlocked()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good florist in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var initialSearchResult =
            "1. \"Best Florists in Hillsboro, OR\" — yelp.com\n" +
            "   Local florist listings in Hillsboro, OR.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.yelp.com/search?cflt=florists&find_loc=Hillsboro%2C%20OR\",\"title\":\"Best Florists in Hillsboro, OR\",\"domain\":\"yelp.com\",\"excerpt\":\"Local florist listings in Hillsboro, OR.\"}]";

        var scopedRecoveryResult =
            "1. \"Flowers By Zsuzsana - Hillsboro OR - Hours, Directions, Reviews - Loc8NearMe\" — loc8nearme.com\n" +
            "   Florist in Hillsboro, OR with current address details.\n\n" +
            "2. \"Hill Florist & Gifts | Florists - Chamber of Commerce\" — chamberofcommerce.com\n" +
            "   Family-owned florist in Hillsboro, OR.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.loc8nearme.com/oregon/hillsboro/flowers-by-zsuzsana/\",\"title\":\"Flowers By Zsuzsana - Hillsboro OR - Hours, Directions, Reviews - Loc8NearMe\",\"domain\":\"loc8nearme.com\",\"excerpt\":\"Florist in Hillsboro, OR with current address details.\"}," +
            "{\"url\":\"https://www.chamberofcommerce.com/business-directory/oregon/hillsboro/florist/2033-hill-florist-and-gifts\",\"title\":\"Hill Florist & Gifts | Florists - Chamber of Commerce\",\"domain\":\"chamberofcommerce.com\",\"excerpt\":\"Family-owned florist in Hillsboro, OR.\"}]";

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" when args.Contains("site:loc8nearme.com", StringComparison.OrdinalIgnoreCase) => scopedRecoveryResult,
            "web_search" or "WebSearch" when args.Contains("site:chamberofcommerce.com", StringComparison.OrdinalIgnoreCase) => scopedRecoveryResult,
            "web_search" or "WebSearch" => initialSearchResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("superpages.com/hillsboro-or/florists", StringComparison.OrdinalIgnoreCase) => "Attention Required! | Cloudflare",
            "browser_navigate" or "BrowserNavigate" when args.Contains("yellowpages.com/hillsboro-or/florists", StringComparison.OrdinalIgnoreCase) => "Attention Required! | Cloudflare",
            "browser_navigate" or "BrowserNavigate" when args.Contains("yelp.com/search", StringComparison.OrdinalIgnoreCase) => "One last step\ncf-turnstile",
            "browser_navigate" or "BrowserNavigate" when args.Contains("loc8nearme.com", StringComparison.OrdinalIgnoreCase) => "Flowers By Zsuzsana\n928 NE Orenco Station Loop, Hillsboro, OR 97124\nOpen now",
            "browser_navigate" or "BrowserNavigate" when args.Contains("chamberofcommerce.com", StringComparison.OrdinalIgnoreCase) => "Hill Florist & Gifts\n111 SE 3rd Ave Ste A, Hillsboro, OR 97123\nFamily owned florist",
            "places_lookup" or "PlacesLookup" =>
                """
                {
                  "provider": "google_places",
                  "query": "florists near Hillsboro, OR",
                  "error": "Google Places API key is not configured.",
                  "place": null,
                  "sources": []
                }
                """,
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good florist in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("florist", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
            (call.Args.Contains("site:chamberofcommerce.com", StringComparison.OrdinalIgnoreCase) ||
             call.Args.Contains("local florist Hillsboro", StringComparison.OrdinalIgnoreCase) ||
             call.Args.Contains("flower shop Hillsboro OR", StringComparison.OrdinalIgnoreCase) ||
             call.Args.Contains("site:loc8nearme.com", StringComparison.OrdinalIgnoreCase)));
    }

        [Fact]
        public async Task LocalBusinessDiscovery_FloristFallback_UsesBingRssRecovery_WhenSearchResultsAreGarbage()
        {
                var llm = new FakeLlmClient((messages, _) =>
                {
                        var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
                        if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                                return new LlmResponse { IsComplete = true, Content = """{"query":"a good florist in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
                        return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
                });

                var initialSearchResult =
                        "1. \"Best Florists in Hillsboro, OR\" — yelp.com\n" +
                        "   Local florist listings in Hillsboro, OR.\n\n" +
                        "<!-- SOURCES_JSON -->\n" +
                        "[{\"url\":\"https://www.yelp.com/search?cflt=florists&find_loc=Hillsboro%2C%20OR\",\"title\":\"Best Florists in Hillsboro, OR\",\"domain\":\"yelp.com\",\"excerpt\":\"Local florist listings in Hillsboro, OR.\"}]";

                var bingRssResult = """
                        <rss>
                            <channel>
                                <title>Bing: hillsboro or florist gifts</title>
                                <item>
                                    <title>About Hill Florist &amp; Gifts - Hillsboro, OR Florist</title>
                                    <link>https://www.hillflorist.com/about_us.php</link>
                                </item>
                                <item>
                                    <title>Shop by Flowers Delivery Hillsboro OR - Hill Florist &amp; Gifts</title>
                                    <link>https://www.hillflorist.com/</link>
                                </item>
                                <item>
                                    <title>Hill Florist &amp; Gifts, Hillsboro, OR | Find a Florist</title>
                                    <link>https://www.findaflorist.com/oregon/hillsboro/hill-florist-and-gifts</link>
                                </item>
                            </channel>
                        </rss>
                        """;

                var mcp = new FakeMcpClient((tool, args) => tool switch
                {
                        "web_search" or "WebSearch" when args.Contains("site:chamberofcommerce.com", StringComparison.OrdinalIgnoreCase) => string.Empty,
                        "web_search" or "WebSearch" when args.Contains("site:loc8nearme.com", StringComparison.OrdinalIgnoreCase) => string.Empty,
                        "web_search" or "WebSearch" => initialSearchResult,
                        "browser_navigate" or "BrowserNavigate" when args.Contains("bing.com/search?format=rss", StringComparison.OrdinalIgnoreCase) => bingRssResult,
                        "browser_navigate" or "BrowserNavigate" => throw new InvalidOperationException("static directory fallback should not run when Bing RSS recovery returns florist names"),
                        "places_lookup" or "PlacesLookup" =>
                                """
                                {
                                    "provider": "google_places",
                                    "query": "florists near Hillsboro, OR",
                                    "error": "Google Places API key is not configured.",
                                    "place": null,
                                    "sources": []
                                }
                                """,
                        _ => string.Empty
                });

                var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
                {
                        UserLocationHint = "Hillsboro, OR",
                        AdvancedPlaceDiscoveryEnabled = false
                };

                var result = await orchestrator.ExecuteAsync(
                        "Can you find me a good florist in Hillsboro, OR?",
                        memoryPackText: "",
                        history: [ChatMessage.System("Test assistant.")],
                        toolCallsMade: [],
                        modeHint: LookupModeHint.Fact,
                        ct: CancellationToken.None);

                Assert.True(result.Success);
                Assert.Contains("Hill Florist & Gifts", result.Text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(mcp.Calls, call =>
                        call.Tool.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
                        call.Args.Contains("bing.com/search?format=rss", StringComparison.OrdinalIgnoreCase));
        }

                [Fact]
                public async Task LocalBusinessDiscovery_FloristFallback_UsesBingRssRecovery_WhenBrowserReturnsPlainText()
                {
                    var llm = new FakeLlmClient((messages, _) =>
                    {
                        var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
                        if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                            return new LlmResponse { IsComplete = true, Content = """{"query":"a good florist in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
                        return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
                    });

                    var initialSearchResult =
                        "1. \"Best Florists in Hillsboro, OR\" — yelp.com\n" +
                        "   Local florist listings in Hillsboro, OR.\n\n" +
                        "<!-- SOURCES_JSON -->\n" +
                        "[{\"url\":\"https://www.yelp.com/search?cflt=florists&find_loc=Hillsboro%2C%20OR\",\"title\":\"Best Florists in Hillsboro, OR\",\"domain\":\"yelp.com\",\"excerpt\":\"Local florist listings in Hillsboro, OR.\"}]";

                    var bingRssPlainText =
                        "bing: hillsboro or florist gifts\n" +
                        "Hillsboro Florist. Hillsboro OR Flower Delivery. Avas Flowers Shop\n" +
                        "About Hill Florist & Gifts - Hillsboro, OR Florist\n" +
                        "Shop by Flowers Delivery Hillsboro OR - Hill Florist & Gifts\n" +
                        "Hill Florist & Gifts, Hillsboro, OR | Find a Florist\n";

                    var mcp = new FakeMcpClient((tool, args) => tool switch
                    {
                        "web_search" or "WebSearch" when args.Contains("site:chamberofcommerce.com", StringComparison.OrdinalIgnoreCase) => string.Empty,
                        "web_search" or "WebSearch" when args.Contains("site:loc8nearme.com", StringComparison.OrdinalIgnoreCase) => string.Empty,
                        "web_search" or "WebSearch" => initialSearchResult,
                        "browser_navigate" or "BrowserNavigate" when args.Contains("bing.com/search?format=rss", StringComparison.OrdinalIgnoreCase) => bingRssPlainText,
                        "browser_navigate" or "BrowserNavigate" => throw new InvalidOperationException("static directory fallback should not run when Bing RSS plain text yields florist names"),
                        "places_lookup" or "PlacesLookup" =>
                            """
                            {
                                "provider": "google_places",
                                "query": "florists near Hillsboro, OR",
                                "error": "Google Places API key is not configured.",
                                "place": null,
                                "sources": []
                            }
                            """,
                        _ => string.Empty
                    });

                    var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
                    {
                        UserLocationHint = "Hillsboro, OR",
                        AdvancedPlaceDiscoveryEnabled = false
                    };

                    var result = await orchestrator.ExecuteAsync(
                        "Can you find me a good florist in Hillsboro, OR?",
                        memoryPackText: "",
                        history: [ChatMessage.System("Test assistant.")],
                        toolCallsMade: [],
                        modeHint: LookupModeHint.Fact,
                        ct: CancellationToken.None);

                    Assert.True(result.Success);
                    Assert.Contains("Hill Florist & Gifts", result.Text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("Avas", result.Text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
                }

                [Fact]
                public async Task LocalBusinessDiscovery_FloristFallback_ReusesEarlierSearchEvidence_WhenLaterToolsHitBudgetStops()
                {
                    var llm = new FakeLlmClient((messages, _) =>
                    {
                        var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
                        if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                            return new LlmResponse { IsComplete = true, Content = """{"query":"a good florist in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
                        return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
                    });

                    var initialSearchResult =
                        "1. \"Flowers by Zsuzsana\" — flowersbyzsuzsana.com\n" +
                        "   Hillsboro florist with same-day delivery in Hillsboro, OR.\n" +
                        "2. \"FLOWERS BY BURKHARDT'S\" — flowersbyburkhardts.com\n" +
                        "   Florist in Hillsboro, OR with current shop details.\n\n" +
                        "<!-- SOURCES_JSON -->\n" +
                        "[{\"url\":\"https://www.yelp.com/search?cflt=florists&find_loc=Hillsboro%2C%20OR\",\"title\":\"Best Florists in Hillsboro, OR\",\"domain\":\"yelp.com\",\"excerpt\":\"Local florist listings in Hillsboro, OR.\"}]";

                    const string cancelledError = "{" +
                        "\"error\":{\"code\":\"tool_error\",\"message\":\"Cancelled\",\"retriable\":false}}";
                    const string budgetError = "{" +
                        "\"error\":{\"code\":\"tool_budget_exceeded\",\"message\":\"Tool budget exceeded\",\"retriable\":false}}";

                    var mcp = new FakeMcpClient((tool, args) => tool switch
                    {
                        "web_search" or "WebSearch" when args.Contains("site:chamberofcommerce.com", StringComparison.OrdinalIgnoreCase) => cancelledError,
                        "web_search" or "WebSearch" when args.Contains("site:loc8nearme.com", StringComparison.OrdinalIgnoreCase) => cancelledError,
                        "web_search" or "WebSearch" => initialSearchResult,
                        "browser_navigate" or "BrowserNavigate" => budgetError,
                        "places_lookup" or "PlacesLookup" => budgetError,
                        _ => string.Empty
                    });

                    var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
                    {
                        UserLocationHint = "Hillsboro, OR",
                        AdvancedPlaceDiscoveryEnabled = false
                    };

                    var result = await orchestrator.ExecuteAsync(
                        "Can you find me a good florist in Hillsboro, OR?",
                        memoryPackText: "",
                        history: [ChatMessage.System("Test assistant.")],
                        toolCallsMade: [],
                        modeHint: LookupModeHint.Fact,
                        ct: CancellationToken.None);

                    Assert.True(result.Success);
                    Assert.Contains("Flowers by Zsuzsana", result.Text, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("FLOWERS BY BURKHARDT'S", result.Text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
                }

                [Fact]
                public async Task LocalBusinessDiscovery_FloristFallback_UsesStaticDirectories_WhenScopedRecoveryBrowseWasCancelled()
                {
                    var llm = new FakeLlmClient((messages, _) =>
                    {
                        var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
                        if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                            return new LlmResponse { IsComplete = true, Content = """{"query":"a good florist in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
                        return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
                    });

                    var initialSearchResult =
                        "1. \"Google Search\" — google.com\n" +
                        "   Search results page.\n\n" +
                        "2. \"Florist Hillsboro OR | Terry's Florist\" — terrysflorist.com\n" +
                        "   Flower delivery catalog for Hillsboro, OR.\n\n" +
                        "<!-- SOURCES_JSON -->\n" +
                        "[{\"url\":\"https://www.google.com/search?q=florist+hillsboro\",\"title\":\"Google Search\",\"domain\":\"google.com\",\"excerpt\":\"Search results page.\"}," +
                        "{\"url\":\"https://terrysflorist.com/florists/oregon/hillsboro/\",\"title\":\"Florist Hillsboro OR | Terry's Florist\",\"domain\":\"terrysflorist.com\",\"excerpt\":\"Flower delivery catalog for Hillsboro, OR.\"}]";

                    const string cancelledError = "{" +
                        "\"error\":{\"code\":\"tool_error\",\"message\":\"Cancelled\",\"retriable\":false}}";

                    var floristDirectoryBrowserResult = """
                        Florists in Hillsboro, OR

                        1. Hill Florist & Gifts
                        111 SE 3rd Ave Ste A, Hillsboro, OR 97123

                        2. Flowers by Zsuzsana
                        928 NE Orenco Station Loop, Hillsboro, OR 97124
                        """;

                    var mcp = new FakeMcpClient((tool, args) => tool switch
                    {
                        "web_search" or "WebSearch" when args.Contains("site:chamberofcommerce.com", StringComparison.OrdinalIgnoreCase) => string.Empty,
                        "web_search" or "WebSearch" when args.Contains("site:loc8nearme.com", StringComparison.OrdinalIgnoreCase) => string.Empty,
                        "web_search" or "WebSearch" => initialSearchResult,
                        "browser_navigate" or "BrowserNavigate" when args.Contains("bing.com/search?format=rss", StringComparison.OrdinalIgnoreCase) => cancelledError,
                        "browser_navigate" or "BrowserNavigate" when args.Contains("superpages.com/hillsboro-or/florists", StringComparison.OrdinalIgnoreCase) => floristDirectoryBrowserResult,
                        "browser_navigate" or "BrowserNavigate" when args.Contains("yellowpages.com/hillsboro-or/florists", StringComparison.OrdinalIgnoreCase) => floristDirectoryBrowserResult,
                        "places_lookup" or "PlacesLookup" => cancelledError,
                        _ => string.Empty
                    });

                    var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
                    {
                        UserLocationHint = "Hillsboro, OR",
                        AdvancedPlaceDiscoveryEnabled = false
                    };

                    var result = await orchestrator.ExecuteAsync(
                        "Can you find me a good florist in Hillsboro, OR?",
                        memoryPackText: "",
                        history: [ChatMessage.System("Test assistant.")],
                        toolCallsMade: [],
                        modeHint: LookupModeHint.Fact,
                        ct: CancellationToken.None);

                    Assert.True(result.Success);
                    Assert.Contains("Hill Florist & Gifts", result.Text, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("Flowers by Zsuzsana", result.Text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains(mcp.Calls, call =>
                        call.Tool.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
                        call.Args.Contains("superpages.com/hillsboro-or/florists", StringComparison.OrdinalIgnoreCase));
                }

    [Fact]
    public async Task LocalBusinessDiscovery_BrowserFallback_DoesNotBrowseWrongAreaSources_ForExplicitLocation()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good deli in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var wrongAreaDirectoryResult =
            "1. \"Best Delis in Seattle, WA\" — restaurantji.com\n" +
            "   Browse Seattle deli listings and local reviews.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.restaurantji.com/wa/seattle/deli/\",\"title\":\"Best Delis in Seattle, WA\",\"domain\":\"restaurantji.com\",\"excerpt\":\"Browse Seattle deli listings and local reviews.\"}]";

        var browserResult = "Best delis in Seattle, WA\n1. Market House Meats\n2. George's Polish Deli";

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" => wrongAreaDirectoryResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("restaurantji", StringComparison.OrdinalIgnoreCase) => browserResult,
            "places_lookup" or "PlacesLookup" =>
                """
                {
                  "provider": "google_places",
                  "query": "delis near Hillsboro, OR",
                  "error": "Google Places API key is not configured.",
                  "place": null,
                  "sources": []
                }
                """,
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good deli in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain("Market House Meats", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("George's Polish Deli", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mcp.Calls, call =>
            call.Tool.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
            call.Args.Contains("restaurantji.com/wa/seattle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_BrowserFallbackFailure_DoesNotLeakPlacesConfigWhenSearchReturnedSources()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good deli in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var localDirectoryResult =
            "1. \"Delis\" — restaurantji.com\n" +
            "   Browse Hillsboro deli listings and local reviews.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.restaurantji.com/or/hillsboro/delis/\",\"title\":\"Delis\",\"domain\":\"restaurantji.com\",\"excerpt\":\"Browse Hillsboro deli listings and local reviews.\"}]";

        var browserChallenge = """
            One last step
            Please solve the challenge below to continue.
            cf-turnstile
            """;

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" => localDirectoryResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("restaurantji", StringComparison.OrdinalIgnoreCase) => browserChallenge,
            "places_lookup" or "PlacesLookup" =>
                """
                {
                  "provider": "google_places",
                  "query": "delis near Hillsboro, OR",
                  "error": "Google Places API key is not configured.",
                  "place": null,
                  "sources": []
                }
                """,
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good deli in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain("Google Places provider is missing an API key", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trustworthy deli recommendation", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalBusinessDiscovery_RedditResult_IsRejectedAndAliasRetryFindsBrowsableDirectory()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good deli in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var redditResult =
            "1. \"r/Hillsboro - Need deli recs\" — reddit.com\n" +
            "   Looking for a good deli in Hillsboro, OR.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.reddit.com/r/Hillsboro/comments/abc123/need_deli_recs/\",\"title\":\"r/Hillsboro - Need deli recs\",\"domain\":\"reddit.com\",\"excerpt\":\"Looking for a good deli in Hillsboro, OR.\"}]";

        var directoryResult =
            "1. \"Delis\" — restaurantji.com\n" +
            "   Browse Hillsboro deli listings and local reviews.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.restaurantji.com/or/hillsboro/delis/\",\"title\":\"Delis\",\"domain\":\"restaurantji.com\",\"excerpt\":\"Browse Hillsboro deli listings and local reviews.\"}]";

        var browserResult = """
            Best sandwich shops in Hillsboro, OR

            1. Biscuit Delicatessen
            2. Main Street Deli
            """;

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" when args.Contains("a good deli in Hillsboro, OR", StringComparison.OrdinalIgnoreCase) => redditResult,
            "web_search" or "WebSearch" => directoryResult,
            "browser_navigate" or "BrowserNavigate" when args.Contains("restaurantji", StringComparison.OrdinalIgnoreCase) => browserResult,
            "places_lookup" or "PlacesLookup" =>
                """
                {
                  "provider": "google_places",
                  "query": "delis near Hillsboro, OR",
                  "error": "Google Places API key is not configured.",
                  "place": null,
                  "sources": []
                }
                """,
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good deli in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Biscuit Delicatessen", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
            !call.Args.Contains("a good deli in Hillsboro, OR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_GenericDirectorySnippet_ExtractsNamedBusinesses()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"a good deli in Hillsboro, OR","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var directoryResult =
            "1. \"THE BEST 10 Delis in Hillsboro, OR\" — yelp.com\n" +
            "   Monkey's Subs, Lu's Etta's Deli & Market, Progress Grocery & Deli, Phil's 1500 Subs, Sunshine Market & Deli.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://www.yelp.com/search?find_desc=Delis&find_loc=Hillsboro%2C+OR\",\"title\":\"THE BEST 10 Delis in Hillsboro, OR\",\"domain\":\"yelp.com\",\"excerpt\":\"Monkey's Subs, Lu's Etta's Deli & Market, Progress Grocery & Deli, Phil's 1500 Subs, Sunshine Market & Deli.\"}]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" or "WebSearch" => directoryResult,
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Can you find me a good deli in Hillsboro, OR?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("delis nearby in Hillsboro, OR", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("THE BEST 10 Delis in Hillsboro, OR", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalBusinessDiscovery_SearchResultsWithoutCandidateNames_FallsBackToDirectPlacesLookup()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"best delis hillsboro","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var webSearchResult =
            "1. \"Best Delis in Hillsboro, OR\" — yelp.com\n" +
            "   Roundup of deli options in Hillsboro.\n\n" +
            "2. \"Delis in Hillsboro, OR\" — tripadvisor.com\n" +
            "   Reviews and photos for delis in Hillsboro.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://www.yelp.com/search?find_desc=Delis&find_loc=Hillsboro%2C+OR\",\"title\":\"Best Delis in Hillsboro, OR\",\"domain\":\"yelp.com\",\"excerpt\":\"Roundup of deli options in Hillsboro.\"}," +
            "{\"url\":\"https://www.tripadvisor.com/Restaurants-g51730-c38-Hillsboro_Oregon.html\",\"title\":\"Delis in Hillsboro, OR\",\"domain\":\"tripadvisor.com\",\"excerpt\":\"Reviews and photos for delis in Hillsboro.\"}" +
            "]";

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" => webSearchResult,
            "places_lookup" or "PlacesLookup" when args.Contains("delis near Hillsboro, OR", StringComparison.OrdinalIgnoreCase) =>
                """
                {
                  "place": {
                    "name": "Biscuit Delicatessen",
                    "address": "171 NE 3rd Ave, Hillsboro, OR",
                    "rating": 4.6,
                    "userRatingsTotal": 412,
                    "openNow": true
                  }
                }
                """,
            "places_lookup" or "PlacesLookup" => "{\"place\":null,\"sources\":[]}",
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR"
        };

        var result = await orchestrator.ExecuteAsync(
            "find me a good deli in Hillsboro, OR",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Biscuit Delicatessen", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, call =>
            call.Tool.Equals("places_lookup", StringComparison.OrdinalIgnoreCase) &&
            call.Args.Contains("delis near Hillsboro, OR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalBusinessDiscovery_SearchResultsWithoutCandidateNames_UsesDirectoryResults_WhenPlacesConfigMissing()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"best delis hillsboro","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var webSearchResult =
            "1. \"Best Delis in Hillsboro, OR\" — yelp.com\n" +
            "   Roundup of deli options in Hillsboro.\n\n" +
            "2. \"Delis in Hillsboro, OR\" — tripadvisor.com\n" +
            "   Reviews and photos for delis in Hillsboro.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://www.yelp.com/search?find_desc=Delis&find_loc=Hillsboro%2C+OR\",\"title\":\"Best Delis in Hillsboro, OR\",\"domain\":\"yelp.com\",\"excerpt\":\"Roundup of deli options in Hillsboro.\"}," +
            "{\"url\":\"https://www.tripadvisor.com/Restaurants-g51730-c38-Hillsboro_Oregon.html\",\"title\":\"Delis in Hillsboro, OR\",\"domain\":\"tripadvisor.com\",\"excerpt\":\"Reviews and photos for delis in Hillsboro.\"}" +
            "]";

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "web_search" or "WebSearch" => webSearchResult,
            "places_lookup" or "PlacesLookup" => """
                {
                  "provider": "google_places",
                  "query": "delis near Hillsboro, OR",
                  "error": "Google Places API key is not configured.",
                  "place": null,
                  "sources": []
                }
                """,
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Hillsboro, OR"
        };

        var result = await orchestrator.ExecuteAsync(
            "find me a good deli in Hillsboro, OR",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Here are the live", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("results I found", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("yelp.com", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("API key", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalBusinessDiscovery_MissingPlacesKey_ReturnsActionableConfigurationMessage()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"local bakeries olympia","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" or "WebSearch" => "No results found for local bakeries olympia",
            "places_lookup" or "PlacesLookup" => """
                {
                  "provider": "google_places",
                  "query": "bakeries near Olympia, WA",
                  "error": "Google Places API key is not configured.",
                  "place": null,
                  "sources": []
                }
                """,
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var result = await orchestrator.ExecuteAsync(
            "can you bring me up some local bakeries?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Google Places provider is missing an API key", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ST_DEEPDIVE_PLACES_API_KEY", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalBusinessDiscovery_JunkSourcesFiltered_ReturnsNoResultsMessage()
    {
        // Even if sources somehow include synthetic entries like
        // "Google Search", the junk filter in
        // SelectLocalBusinessDiscoverySources should strip them,
        // leaving zero real sources and triggering the no-results path.
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"local florists olympia","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        // Only junk sources in the payload
        var searchResult =
            "Top local results\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://www.google.com/search?q=florists\",\"title\":\"Google Search\",\"domain\":\"google.com\",\"snippet\":\"\"}," +
            "{\"url\":\"https://www.bing.com/search?q=florists\",\"title\":\"Bing\",\"domain\":\"bing.com\",\"snippet\":\"\"}" +
            "]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" => searchResult,
            "WebSearch"  => searchResult,
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var result = await orchestrator.ExecuteAsync(
            "can you bring me up local florists?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain("Google Search", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bing", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalDeliDiscovery_UsesInlineLocationContext_WhenNoManualLocationHint()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"local delis in Essex County, New Jersey","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var searchResult =
            "Top local results\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://example.com/nj-deli\",\"title\":\"Town Hall Deli - South Orange, NJ\",\"domain\":\"example.com\",\"snippet\":\"Popular Essex County deli with sandwiches and breakfast.\"}" +
            "]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" => searchResult,
            "WebSearch" => searchResult,
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.");

        var result = await orchestrator.ExecuteAsync(
            "can you pull up some local delis in Essex County, New Jersey please?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("deli nearby in Essex County, New Jersey", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("I need a location", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalBusinessDiscovery_FiltersOutOutOfAreaResults_WhenLocationIsKnown()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"local delis","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var searchResult =
            "Top local results\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://example.com/nj-deli\",\"title\":\"Millburn Deli - Millburn, NJ\",\"domain\":\"example.com\",\"snippet\":\"Beloved Essex County deli known for giant sandwiches.\"}," +
            "{\"url\":\"https://example.com/dc-deli\",\"title\":\"Best Delis in Washington, D.C.\",\"domain\":\"example.com\",\"snippet\":\"A roundup of delis in the nation's capital.\"}," +
            "{\"url\":\"https://example.com/va-deli\",\"title\":\"Sterling Deli Update - Sterling, Virginia\",\"domain\":\"example.com\",\"snippet\":\"Regional deli news in Northern Virginia.\"}" +
            "]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" => searchResult,
            "WebSearch" => searchResult,
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Essex County, New Jersey"
        };

        var result = await orchestrator.ExecuteAsync(
            "can you pull up some local delis please?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Millburn Deli", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Washington, D.C.", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sterling, Virginia", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalBusinessVerificationFallback_DoesNotClaimZeroResults_WhenSearchReturnedCandidates()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"McDonalds in Portland OR hours","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var searchResult =
            "1. \"McDonald's - Seattle, WA\" — example.com\n" +
            "   Seattle location hours listing.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://example.com/seattle-mcdonalds\",\"title\":\"McDonald's - Seattle, WA\",\"domain\":\"example.com\",\"excerpt\":\"Seattle location hours listing.\"}" +
            "]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" or "WebSearch" => searchResult,
            "places_lookup" or "PlacesLookup" =>
                """
                {
                  "provider": "google_places",
                  "query": "McDonalds in Portland OR",
                  "error": "Google Places API key is not configured.",
                  "place": null,
                  "sources": []
                }
                """,
            "browser_navigate" or "BrowserNavigate" => throw new InvalidOperationException("browser fallback should skip out-of-area candidates"),
            _ => string.Empty
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Portland, OR",
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await orchestrator.ExecuteAsync(
            "Is McDonalds in Portland OR open right now?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Verification recommended", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fallback search came back with 0 results", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalBusinessDiscovery_KeepsAmbiguousRelevantResults_WhenLocationIsKnown()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                return new LlmResponse { IsComplete = true, Content = """{"query":"local delis near Olympia, WA","recency":"any"}""", FinishReason = "stop" };
            return new LlmResponse { IsComplete = true, Content = "unused", FinishReason = "stop" };
        });

        var searchResult =
            "Top local results\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[" +
            "{\"url\":\"https://example.com/main-street-deli\",\"title\":\"Main Street Deli\",\"domain\":\"example.com\",\"snippet\":\"Classic sandwiches, soups, and lunch specials.\"}," +
            "{\"url\":\"https://example.com/capitol-lunch\",\"title\":\"Capitol Lunch Counter\",\"domain\":\"example.com\",\"snippet\":\"Fresh bagels, hot pastrami, and daily deli specials.\"}," +
            "{\"url\":\"https://example.com/market-deli\",\"title\":\"Farmhouse Market Deli\",\"domain\":\"example.com\",\"snippet\":\"Local deli counter with grab-and-go sandwiches.\"}" +
            "]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "web_search" => searchResult,
            "WebSearch" => searchResult,
            _ => ""
        });

        var orchestrator = new SearchOrchestrator(llm, mcp, new TestAuditLogger(), "Test assistant.")
        {
            UserLocationHint = "Olympia, WA"
        };

        var result = await orchestrator.ExecuteAsync(
            "can you bring me up local delis?",
            memoryPackText: "",
            history: [ChatMessage.System("Test assistant.")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Main Street Deli", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Farmhouse Market Deli", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not retrieve live local business results", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("can you tell me about some florists nearby")]   // discovery, not specific-place
    [InlineData("florists nearby")]                              // discovery
    [InlineData("restaurants near me")]                           // discovery
    [InlineData("where is a good bakery around me")]              // discovery
    [InlineData("find me a salon close by")]                      // discovery
    [InlineData("find me some bakeries nearby")]                  // discovery
    [InlineData("bakeries nearby")]                               // discovery
    public void LooksLikeDeepDiveLookup_RejectsDiscoveryQueries(string input)
    {
        var result = IntentFeatureExtractor.LooksLikeDeepDiveLookup(input.ToLowerInvariant());
        Assert.False(result, $"Discovery queries should NOT match deep dive: {input}");
    }

    [Theory]
    [InlineData("is left bank pastry open")]                     // "is X open" pattern
    [InlineData("what time does walmart close")]                 // "what time does X close" pattern
    [InlineData("hours and reviews for target")]                 // hours + reviews signal
    public void LooksLikeDeepDiveLookup_MatchesSpecificPlaceQueries(string input)
    {
        var result = IntentFeatureExtractor.LooksLikeDeepDiveLookup(input.ToLowerInvariant());
        Assert.True(result, $"Expected deep dive match for specific-place query: {input}");
    }

    [Theory]
    [InlineData("create a briefing on french market")]
    [InlineData("give me a briefing on target")]
    [InlineData("brief me on the new bakery downtown")]
    [InlineData("briefing on walmart hours")]
    [InlineData("briefing for trader joe's")]
    [InlineData("can you pull me up more info on new olympia flower shop")]
    public void LooksLikeDeepDiveLookup_MatchesBriefingSignals(string input)
    {
        var result = IntentFeatureExtractor.LooksLikeDeepDiveLookup(input.ToLowerInvariant());
        Assert.True(result, $"Expected deep dive match for: {input}");
    }

    [Theory]
    [InlineData("tell me about quantum computing")]    // no business term
    [InlineData("what is the news today")]              // news, not local business
    [InlineData("open source software")]                // "open source" guard
    [InlineData("can you tell me if the new live-action How to Train Your Dragon is word for word like the original movies?")]
    public void LooksLikeDeepDiveLookup_RejectsNonLocalBusiness(string input)
    {
        var result = IntentFeatureExtractor.LooksLikeDeepDiveLookup(input.ToLowerInvariant());
        Assert.False(result, $"Expected no deep dive match for: {input}");
    }

    // ── ExtractFollowUpSubject tests ─────────────────────────────────

    [Theory]
    [InlineData("tell me more about Left Bank Pastry", "Left Bank Pastry")]
    [InlineData("can you tell me more about New Olympia Flower Shop?", "New Olympia Flower Shop")]
    [InlineData("can you pull me up more info on new olympia flower shop?", "new olympia flower shop")]
    [InlineData("more info on Target", "Target")]
    [InlineData("show me Trader Joe's", "Trader Joe's")]
    [InlineData("brief me on the new bakery downtown", "the new bakery downtown")]
    [InlineData("give me a brief on french market", "french market")]
    [InlineData("create a brief about Target", "Target")]
    public void ExtractFollowUpSubject_PrefixStripping_Works(string input, string expected)
    {
        var result = SearchOrchestrator.ExtractFollowUpSubject(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("New Olympia Flower Shop -- can you tell me more about this one?", "New Olympia Flower Shop")]
    [InlineData("Left Bank Pastry -- tell me more", "Left Bank Pastry")]
    [InlineData("Target — more info please?", "Target")]
    [InlineData("Trader Joe's -- what can you tell me about this?", "Trader Joe's")]
    [InlineData("The West Olympia Woman -- give me a brief", "The West Olympia Woman")]
    public void ExtractFollowUpSubject_SeparatorPattern_ExtractsEntityName(string input, string expected)
    {
        var result = SearchOrchestrator.ExtractFollowUpSubject(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("this one", true)]
    [InlineData("that one", true)]
    [InlineData("this place", true)]
    [InlineData("that restaurant", true)]
    [InlineData("the first one", true)]
    [InlineData("Left Bank Pastry", false)]
    [InlineData("Target in Olympia", false)]
    public void IsPronounSubjectReference_ClassifiesCorrectly(string subject, bool expected)
    {
        Assert.Equal(expected, SearchOrchestrator.IsPronounSubjectReference(subject));
    }

    [Theory]
    [InlineData("New Olympia Flower Shop | Yelp", "New Olympia Flower Shop")]
    [InlineData("Target — Google Maps", "Target")]
    [InlineData("Left Bank Pastry - TripAdvisor", "Left Bank Pastry")]
    [InlineData("Plain Title", "Plain Title")]
    public void StripTitleSuffix_StripsCommonSiteSuffixes(string title, string expected)
    {
        Assert.Equal(expected, SearchOrchestrator.StripTitleSuffix(title));
    }

    [Theory]
    [InlineData("can you tell me more about this one?", true)]
    [InlineData("tell me more", true)]
    [InlineData("more info please", true)]
    [InlineData("give me a brief on it", true)]
    [InlineData("elaborate on that", true)]
    [InlineData("some random search query", false)]
    [InlineData("new olympia flower shop", false)]
    public void IsFollowUpFiller_ClassifiesCorrectly(string text, bool expected)
    {
        Assert.Equal(expected, SearchOrchestrator.IsFollowUpFiller(text.ToLowerInvariant()));
    }
}

public class AuditedMcpToolClientBudgetTests
{
    [Fact]
    public async Task TurnBudget_ResetsAfterNotifyNewTurn()
    {
        var callCount = 0;
        var inner = new FakeMcpClient((_, _) => { callCount++; return "ok"; });
        var audit = new TestAuditLogger();
        var gate = new AllowAllGate();

        var settings = new SirThaddeus.Config.ToolBudgetSettings
        {
            Enabled = true,
            MaxToolCallsPerTurn = 2,
            MaxToolCallsPerSession = 100,
            MaxWebPullsPerTurn = 1,
            MaxFileOpsPerMinute = 30
        };
        var controls = new RuntimeControlState
        {
            ToolBudgets = settings
        };

        var client = new AuditedMcpToolClient(
            inner, audit, gate, "test-session",
            () => controls);

        // First turn: use up the web budget (1 web call allowed)
        var r1 = await client.CallToolAsync("web_search", "{}", default);
        Assert.Equal("ok", r1);

        // Second web call in same turn should be blocked
        var r2 = await client.CallToolAsync("web_search", "{}", default);
        Assert.Contains("budget", r2, StringComparison.OrdinalIgnoreCase);

        // Reset turn
        client.NotifyNewTurn();

        // Now web call should succeed again
        var r3 = await client.CallToolAsync("web_search", "{}", default);
        Assert.Equal("ok", r3);
    }

    private sealed class AllowAllGate : IToolPermissionGate
    {
        public Task<ToolPermissionResult> CheckAsync(
            string toolName, string argumentsJson, CancellationToken ct)
            => Task.FromResult(new ToolPermissionResult
            {
                Granted = true,
                PermissionRequired = false
            });
    }
}

#endregion

// ── Local Business Name Extraction Tests ────────────────────────────
public class LocalBusinessNameExtractionTests
{
    [Fact]
    public void ExtractBusinessNames_NumberedList_ReturnsNames()
    {
        var article = """
            Best Bakeries in Olympia, WA
            1. Left Bank Pastry
            Incredible croissants and fresh bread daily.
            2. The Bread Peddler
            Known for artisan sourdough loaves.
            3. Olympia Coffee Roasters
            Also serves amazing pastries.
            """;

        var names = SearchOrchestrator.ExtractBusinessNamesFromArticles(
            [article], "bakeries nearby");

        Assert.Contains("Left Bank Pastry", names);
        Assert.Contains("The Bread Peddler", names);
        Assert.Contains("Olympia Coffee Roasters", names);
    }

    [Fact]
    public void ExtractBusinessNames_HeadingFollowedByDetails_ReturnsNames()
    {
        var article = """
            Top local bakeries

            Wagner's European Bakery
            123 Main St, Olympia, WA 98501
            Authentic German breads and pastries.

            San Francisco Street Bakery
            4.5 stars - Open now
            Fresh sourdough and artisan bread daily.
            """;

        var names = SearchOrchestrator.ExtractBusinessNamesFromArticles(
            [article], "bakeries nearby");

        Assert.Contains("Wagner's European Bakery", names);
        Assert.Contains("San Francisco Street Bakery", names);
    }

    [Fact]
    public void ExtractBusinessNames_SkipsGenericPhrases()
    {
        var article = """
            Best Bakeries
            1. Read More
            2. Left Bank Pastry
            Great bread.
            3. Advertisement
            4. Show More
            5. Sweet Flour Baking Co
            Artisan cakes.
            """;

        var names = SearchOrchestrator.ExtractBusinessNamesFromArticles(
            [article], "bakeries nearby");

        Assert.DoesNotContain("Read More", names);
        Assert.DoesNotContain("Advertisement", names);
        Assert.DoesNotContain("Show More", names);
        Assert.Contains("Left Bank Pastry", names);
        Assert.Contains("Sweet Flour Baking Co", names);
    }

    [Fact]
    public void ExtractBusinessNames_DeduplicatesAcrossArticles()
    {
        var article1 = """
            1. Left Bank Pastry
            Great croissants.
            2. Some Other Bakery
            Fine bread.
            """;
        var article2 = """
            1. Left Bank Pastry
            Best pastries in town.
            2. Third Place Bakery
            Wonderful pies.
            """;

        var names = SearchOrchestrator.ExtractBusinessNamesFromArticles(
            [article1, article2], "bakeries nearby");

        Assert.Single(names, n => n.Equals("Left Bank Pastry", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Some Other Bakery", names);
        Assert.Contains("Third Place Bakery", names);
    }

    [Fact]
    public void ExtractBusinessNames_CleansTrailingJunk()
    {
        var article = """
            1. Wagner's Bakery - Best in Town
            Amazing bread.
            2. The Flour Shop (Olympia)
            Fresh daily.
            3. Bread & Butter Co. 4.5 stars
            Artisan loaves.
            """;

        var names = SearchOrchestrator.ExtractBusinessNamesFromArticles(
            [article], "bakeries nearby");

        Assert.Contains("Wagner's Bakery", names);
        Assert.Contains("The Flour Shop", names);
        Assert.Contains("Bread & Butter Co.", names);
    }

    [Fact]
    public void ExtractBusinessNames_EmptyArticles_ReturnsEmpty()
    {
        var names = SearchOrchestrator.ExtractBusinessNamesFromArticles(
            [], "bakeries nearby");
        Assert.Empty(names);
    }

    [Fact]
    public void ExtractBusinessNames_NoMatchableContent_ReturnsEmpty()
    {
        var article = "This is just a paragraph about the history of bread baking in the Pacific Northwest.";
        var names = SearchOrchestrator.ExtractBusinessNamesFromArticles(
            [article], "bakeries nearby");
        Assert.Empty(names);
    }

    /// <summary>
    /// Regression: "Bakeries in Olympia, WA - The Real Yellow Pages" must be
    /// caught as an aggregator and must not survive title extraction.
    /// </summary>
    [Fact]
    public void YellowPagesAggregator_DetectedAndRejected()
    {
        var source = new SourceItem
        {
            Url   = "https://www.realyellowpages.com/olympia-wa/bakeries",
            Title = "Bakeries in Olympia, WA - The Real Yellow Pages"
        };

        // Should be recognized as an aggregator directory source.
        Assert.True(SearchOrchestrator.IsDirectoryAggregatorSource(source));

        // Even if extraction were attempted, the title must be rejected
        // (the name after stripping is "Bakeries in Olympia, WA" which
        // starts with the category label).
        var name = SearchOrchestrator.TestHook_ExtractBusinessNameFromSourceTitle(
            source.Title, "can you bring me up some local bakeries please?");
        Assert.Null(name);
    }

    [Fact]
    public void DiscussionHeadline_RejectedAsBusinessName()
    {
        var name = SearchOrchestrator.TestHook_ExtractBusinessNameFromSourceTitle(
            "Help! Does anyone know of a good deli in Portland or close? - Reddit",
            "Can you find me a good deli in Hillsboro, OR?");

        Assert.Null(name);
    }

    [Theory]
    [InlineData("Flower Delivery: Send Flowers Online | FTD", "Can you find me a good florist in Hillsboro, OR?")]
    [InlineData("Delicatessen - Wikipedia", "Can you find me a good deli in Hillsboro, OR?")]
    [InlineData("50 Best Sandwich Recipes & Ideas | Food Network", "Can you find me a good deli in Hillsboro, OR?")]
    public void GenericCategoryLandingTitle_RejectedAsBusinessName(string title, string userMessage)
    {
        var name = SearchOrchestrator.TestHook_ExtractBusinessNameFromSourceTitle(title, userMessage);
        Assert.Null(name);
    }

    [Fact]
    public void DirectoryClaimBannerPrefix_StrippedFromBusinessName()
    {
        var name = SearchOrchestrator.TestHook_ExtractBusinessNameFromSourceTitle(
            "You Unclaimed Cheba Hut Toasted Subs",
            "Can you find me a good deli in Hillsboro, OR?");

        Assert.Equal("Cheba Hut Toasted Subs", name);
    }

    [Fact]
    public void FloristSeoTitle_WithCategoryPrefix_ExtractsCleanBusinessName()
    {
        var name = SearchOrchestrator.TestHook_ExtractBusinessNameFromSourceTitle(
            "Hillsboro Florist: Flowers by Zsuzsana - Flower Delivery in OR, 97124",
            "Can you find me a good florist in Hillsboro, OR?");

        Assert.Equal("Flowers by Zsuzsana", name);
    }

    [Fact]
    public void FloristSeoTitle_WithPipeSegments_ExtractsBusinessNameFromLaterSegment()
    {
        var name = SearchOrchestrator.TestHook_ExtractBusinessNameFromSourceTitle(
            "Flower Shop Hillsboro | Florist in Hillsboro, OR | FLOWERS BY BURKHARDT'S",
            "Can you find me a good florist in Hillsboro, OR?");

        Assert.Equal("FLOWERS BY BURKHARDT'S", name);
    }

    [Fact]
    public void FloristDashSeparatedPageTitle_ExtractsBusinessNameFromLaterSegment()
    {
        var name = SearchOrchestrator.TestHook_ExtractBusinessNameFromSourceTitle(
            "Shop by Flowers Delivery Hillsboro OR - Hill Florist & Gifts",
            "Can you find me a good florist in Hillsboro, OR?");

        Assert.Equal("Hill Florist & Gifts", name);
    }

    [Fact]
    public void FloristSeoTitle_WithCivicDirectorySegment_IsRejected()
    {
        var name = SearchOrchestrator.TestHook_ExtractBusinessNameFromSourceTitle(
            "Florists - Hillsboro, OR | City of Hillsboro, OR | Chamber of Commerce",
            "Can you find me a good florist in Hillsboro, OR?");

        Assert.Null(name);
    }

    [Fact]
    public void SanitizedWebSearchQuery_StripsRetryPlannerScaffold()
    {
        var query = """
            good deli in Hillsboro, OR?
            Retry strategy: official_source_search
            Guidance: Prioritize official/first-party documentation and policy pages.
            Previous answer for verification:
            I could not retrieve live local business results.
            Return concise, evidence-grounded output and call out uncertainty when unresolved.
            """;

        var sanitized = SearchOrchestrator.TestHook_SanitizeWebSearchQuery(query);

        Assert.Equal("good deli in Hillsboro, OR?", sanitized);
    }

    [Fact]
    public void LocationOnlyTitle_RejectedAsBusinessName()
    {
        var name = SearchOrchestrator.TestHook_ExtractBusinessNameFromSourceTitle(
            "Hillsboro",
            "Can you find me a good deli in Hillsboro, OR?");

        Assert.Null(name);
    }

    [Fact]
    public void ExtractBusinessNames_FiltersGenericListingHeadings()
    {
        var article = """
            - San Francisco Street Bakery
            - Local Bakery Locations in Olympia, Washington
            - Grocery Hours
            """;

        var names = SearchOrchestrator.ExtractBusinessNamesFromArticles(
            [article], "bakeries nearby");

        Assert.Contains("San Francisco Street Bakery", names);
        Assert.DoesNotContain("Local Bakery Locations in Olympia, Washington", names);
        Assert.DoesNotContain("Grocery Hours", names);
    }

    [Fact]
    public void ChainDepartmentTitle_RejectedForGenericLocalBusinessDiscovery()
    {
        var name = SearchOrchestrator.TestHook_ExtractBusinessNameFromSourceTitle(
            "Walmart Deli in Hillsboro, OR | Grab & Go Sandwiches & Wraps, Party Trays, Charcuterie & Gourmet Cheese | Store #2590",
            "Can you find me a good deli in Hillsboro, OR?");

        Assert.Null(name);
    }

    [Fact]
    public void ExtractBusinessNames_FiltersChainDepartmentEntries()
    {
        var article = """
            1. Isabella's Deli
            2. Walmart Deli in Hillsboro, OR | Grab & Go Sandwiches & Wraps, Party Trays, Charcuterie & Gourmet Cheese | Store #2590
            """;

        var names = SearchOrchestrator.ExtractBusinessNamesFromArticles(
            [article], "Can you find me a good deli in Hillsboro, OR?");

        Assert.Contains("Isabella's Deli", names);
        Assert.DoesNotContain(names, name => name.Contains("Walmart", StringComparison.OrdinalIgnoreCase));
    }
}
