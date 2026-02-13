using SirThaddeus.Agent;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Search;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

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
    [InlineData("pull up the news",         SearchMode.NewsAggregate)]
    [InlineData("top headlines today",       SearchMode.NewsAggregate)]
    [InlineData("whats happening",           SearchMode.NewsAggregate)]
    [InlineData("breaking news",             SearchMode.NewsAggregate)]
    [InlineData("daily briefing",            SearchMode.NewsAggregate)]
    public void NewsQueries_ClassifyAsNewsAggregate(string message, SearchMode expected)
    {
        var mode = SearchModeRouter.Classify(message, EmptySession(), DateTimeOffset.UtcNow);
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("who is Elon Musk",         SearchMode.WebFactFind)]
    [InlineData("explain quantum computing", SearchMode.WebFactFind)]
    [InlineData("stock price of AAPL",       SearchMode.WebFactFind)]
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
    [InlineData("what's 15% of 230",  "calculator", "15% of 230 = **34.50**")]
    [InlineData("what is 15 percent of 230", "calculator", "15% of 230 = **34.50**")]
    [InlineData("what's 10*45?",      "calculator", "10*45 = **450**")]
    [InlineData("what is 6 plus 7?",  "calculator", "6 + 7 = **13**")]
    [InlineData("Hey, Thaddeus, what's 6x7?", "calculator", "6 * 7 = **42**")]
    [InlineData("100 + 50",           "calculator", "100 + 50 = **150**")]
    public void Calculator_ReturnsInlineAnswer(string input, string category, string expected)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.NotNull(result);
        Assert.Equal(category, result!.Category);
        Assert.Equal(expected, result.Answer);
        Assert.Null(result.McpToolName); // Inline — no MCP call needed
    }

    [Theory]
    [InlineData("convert 10 miles to km",      "conversion")]
    [InlineData("convert 1 mile to feet",      "conversion")]
    [InlineData("convert 100 fahrenheit to celsius", "conversion")]
    [InlineData("convert 5 lbs to kg",         "conversion")]
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
    [InlineData("time in Tokyo",           "time")]
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
    [InlineData("what is the weather like in Rexburg, ID?")]
    [InlineData("can you tell me what the weather is in rexburg,id?")]
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
        var result = UtilityRouter.TryHandle("what is the weather like in Rexburg, ID? please");
        Assert.NotNull(result);
        Assert.Equal("weather_geocode", result!.McpToolName);
        Assert.NotNull(result.McpToolArgs);
        Assert.Contains("\"place\":\"Rexburg, ID\"", result.McpToolArgs);
        Assert.Contains("\"maxResults\":3", result.McpToolArgs);
    }

    [Fact]
    public void Weather_GeocodeArgs_StripsTemporalTailFromPlace()
    {
        var result = UtilityRouter.TryHandle("What's the forecast for Rexburg today?");
        Assert.NotNull(result);
        Assert.Equal("weather_geocode", result!.McpToolName);
        Assert.NotNull(result.McpToolArgs);
        Assert.Contains("\"place\":\"Rexburg\"", result.McpToolArgs, StringComparison.OrdinalIgnoreCase);
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
            Url = "https://a.com", Title = "Test",
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
            CanonicalName  = "Elon Musk",
            Type           = "Person",
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
        Assert.Equal("day",   QueryBuilder.DetectRecencyFromMessage("news today"));
        Assert.Equal("week",  QueryBuilder.DetectRecencyFromMessage("events this week"));
        Assert.Equal("week",  QueryBuilder.DetectRecencyFromMessage("recent headlines from last week"));
        Assert.Equal("month", QueryBuilder.DetectRecencyFromMessage("updates past month"));
        Assert.Equal("day",   QueryBuilder.DetectRecencyFromMessage("breaking news"));
        Assert.Equal("day",   QueryBuilder.DetectRecencyFromMessage("what's the dow jones at most recently?"));
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
            LocationName = "Boise, Idaho",
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

        Assert.Equal("Boise, Idaho", merged.LocationText);
        Assert.True(merged.LocationInferredFromState);
    }

    [Theory]
    [InlineData("you. Why is Dante so chunky?")]       // sentence fragment with ? — the exact bug
    [InlineData("Hello sir Thaddeus I have a question")] // too many words, starts with "hello"
    [InlineData("tell me about the weather")]            // starts with "tell"
    [InlineData("is it going to rain tomorrow")]         // starts with "is"
    [InlineData("What time is it in New York")]          // starts with "what"
    [InlineData("I was wondering why is Dante so chunky")] // starts with "I", too many words
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
    [InlineData("Boise, Idaho")]
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
        string queryJson  = """{"query":"test query","recency":"any"}""",
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
            """{"query":"Rexburg, ID","source":"open-meteo","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Rexburg, Idaho, US","countryCode":"US","isUs":true,"latitude":43.826,"longitude":-111.789,"confidence":0.95}]}""";
        var forecastResult =
            """{"provider":"nws","providerReason":"us_primary","cache":{"hit":false,"ageSeconds":0},"location":{"name":"Rexburg, Idaho, US","countryCode":"US","isUs":true,"latitude":43.826,"longitude":-111.789},"current":{"temperature":39,"unit":"F","condition":"windy","wind":"12 mph","humidityPercent":71},"daily":[{"date":"2026-02-10","tempHigh":42,"tempLow":30,"avgTemp":36,"unit":"F","condition":"windy"}],"alerts":[]}""";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "weather_geocode" => geocodeResult,
            "weather_forecast" => forecastResult,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("What is the weather like in Rexburg, ID?");

        Assert.True(result.Success);
        Assert.Contains("Rexburg", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("39F", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wind", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Avg temp", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\n1.", result.Text, StringComparison.Ordinal);
        Assert.Equal(0, result.LlmRoundTrips); // weather brief is deterministic
        Assert.Equal(0, llmCalls);

        Assert.Contains(mcp.Calls, c => c.Tool.Equals("weather_geocode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("weather_forecast", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
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
                    Content = """{"category":"weather","canonicalMessage":"weather in Rexburg, ID","confidence":0.92}""",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Rexburg will be cool tomorrow with a chance of wind.",
                FinishReason = "stop"
            };
        });

        var geocodeResult =
            """{"query":"Rexburg, ID","source":"open-meteo","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Rexburg, Idaho, US","countryCode":"US","isUs":true,"latitude":43.826,"longitude":-111.789,"confidence":0.95}]}""";
        var forecastResult =
            """{"provider":"nws","providerReason":"us_primary","cache":{"hit":false,"ageSeconds":0},"location":{"name":"Rexburg, Idaho, US","countryCode":"US","isUs":true,"latitude":43.826,"longitude":-111.789},"current":{"temperature":39,"unit":"F","condition":"partly cloudy","wind":"7 mph","humidityPercent":54},"daily":[{"date":"2026-02-10","tempHigh":44,"tempLow":31,"avgTemp":38,"unit":"F","condition":"partly cloudy"}],"alerts":[]}""";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "weather_geocode" => geocodeResult,
            "weather_forecast" => forecastResult,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync(
            "I'm going on a trip to Rexburg tomorrow and want to check conditions there.");

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
        Assert.Contains("rexburg", geocodeCalls[0].Args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UtilityBypass_Weather_IgnoresTemporalTail_AndUsesBestGeocodeCandidate()
    {
        var llm = new FakeLlmClient("LLM should not be called");
        var geocodeResult =
            """{"query":"Rexburg","source":"photon","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Day-Today, Scotland, GB","countryCode":"GB","isUs":false,"latitude":55.9551009,"longitude":-2.9878669,"confidence":0.10},{"name":"Rexburg, Idaho, US","countryCode":"US","isUs":true,"latitude":43.826,"longitude":-111.789,"confidence":0.95}]}""";
        var forecastResult =
            """{"provider":"nws","providerReason":"us_primary","cache":{"hit":false,"ageSeconds":0},"location":{"name":"Rexburg, Idaho, US","countryCode":"US","isUs":true,"latitude":43.826,"longitude":-111.789},"current":{"temperature":25,"unit":"F","condition":"partly sunny","wind":"3 mph","humidityPercent":34},"daily":[{"date":"2026-02-10","tempHigh":31,"tempLow":19,"avgTemp":25,"unit":"F","condition":"partly sunny"}],"alerts":[]}""";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "weather_geocode" => geocodeResult,
            "weather_forecast" => forecastResult,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("What's the forecast for Rexburg today?");

        Assert.True(result.Success);
        Assert.Contains("Rexburg", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Scotland", result.Text, StringComparison.OrdinalIgnoreCase);

        var geocodeCall = mcp.Calls.First(c =>
            c.Tool.Equals("weather_geocode", StringComparison.OrdinalIgnoreCase));
        var forecastCall = mcp.Calls.First(c =>
            c.Tool.Equals("weather_forecast", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("\"place\":\"Rexburg\"", geocodeCall.Args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("today", geocodeCall.Args, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"latitude\":43.826", forecastCall.Args, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"longitude\":-111.789", forecastCall.Args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UtilityBypass_Weather_FollowUp_ReusesPreviousPlaceContext()
    {
        var llm = new FakeLlmClient("LLM should not be called");
        var boiseGeocode =
            """{"query":"Boise, ID","source":"photon","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Boise, Idaho, US","countryCode":"US","isUs":true,"latitude":43.616613,"longitude":-116.200886,"confidence":0.95}]}""";
        var todayMuseumGeocode =
            """{"query":"today","source":"photon","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Today Art Museum, CN","countryCode":"CN","isUs":false,"latitude":39.896836,"longitude":116.461529,"confidence":0.70}]}""";
        var boiseForecast =
            """{"provider":"nws","providerReason":"us_primary","cache":{"hit":false,"ageSeconds":0},"location":{"name":"Boise, Idaho, US","countryCode":"US","isUs":true,"latitude":43.616613,"longitude":-116.200886},"current":{"temperature":32,"unit":"F","condition":"partly sunny","wind":"3 mph","humidityPercent":34},"daily":[{"date":"2026-02-10","tempHigh":48,"tempLow":38,"avgTemp":43,"unit":"F","condition":"partly sunny"}],"alerts":[]}""";

        var mcp = new FakeMcpClient((tool, args) => tool switch
        {
            "weather_geocode" => args.Contains("boise", StringComparison.OrdinalIgnoreCase)
                ? boiseGeocode
                : todayMuseumGeocode,
            "weather_forecast" => boiseForecast,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var first = await agent.ProcessAsync("Whats the weather like in Boise, ID?");
        Assert.True(first.Success);
        Assert.Contains("Boise", first.Text, StringComparison.OrdinalIgnoreCase);

        var second = await agent.ProcessAsync("Thats great! can you get the forecast for today?");
        Assert.True(second.Success);
        Assert.Contains("Boise", second.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Today Art Museum", second.Text, StringComparison.OrdinalIgnoreCase);

        var geocodeCalls = mcp.Calls.Where(c =>
            c.Tool.Equals("weather_geocode", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(geocodeCalls.Count >= 2);
        Assert.Contains("\"place\":\"Boise", geocodeCalls[1].Args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"place\":\"today", geocodeCalls[1].Args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UtilityBypass_WeatherActivityFollowUps_StayOnWeatherPipeline()
    {
        var llm = new FakeLlmClient("LLM should not be called");
        var rexburgGeocode =
            """{"query":"Rexburg, Idaho","source":"open-meteo","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Rexburg, Idaho, US","countryCode":"US","isUs":true,"latitude":43.826,"longitude":-111.789,"confidence":0.95}]}""";
        var rexburgForecast =
            """{"provider":"nws","providerReason":"us_primary","cache":{"hit":false,"ageSeconds":0},"location":{"name":"Rexburg, Idaho, US","countryCode":"US","isUs":true,"latitude":43.826,"longitude":-111.789},"current":{"temperature":39,"unit":"F","condition":"chance rain and snow","wind":"7 mph","humidityPercent":85},"daily":[{"date":"2026-02-10","tempHigh":42,"tempLow":30,"avgTemp":35,"unit":"F","condition":"chance rain and snow"}],"alerts":[]}""";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "weather_geocode" => rexburgGeocode,
            "weather_forecast" => rexburgForecast,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var first = await agent.ProcessAsync("What is the weather in Rexburg, Idaho today?");
        Assert.True(first.Success);

        var second = await agent.ProcessAsync("What can I do in that kind of weather?");
        Assert.True(second.Success);
        Assert.Contains("Best fit right now", second.Text, StringComparison.OrdinalIgnoreCase);

        var third = await agent.ProcessAsync("That's great, but what kind of things could I do in that weather?");
        Assert.True(third.Success);
        Assert.Contains("Best fit right now", third.Text, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(mcp.Calls,
            c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
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
                var query = userInput.Contains("boise", StringComparison.OrdinalIgnoreCase)
                    ? """{"query":"boise latest news","recency":"day"}"""
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
                Content = "Here is a Boise-focused news summary.",
                FinishReason = "stop"
            };
        });

        var boiseGeocode =
            """{"query":"Boise, ID","source":"photon","cache":{"hit":false,"ageSeconds":0},"results":[{"name":"Boise, Idaho, US","countryCode":"US","isUs":true,"latitude":43.616613,"longitude":-116.200886,"confidence":0.95}]}""";
        var boiseForecast =
            """{"provider":"nws","providerReason":"us_primary","cache":{"hit":false,"ageSeconds":0},"location":{"name":"Boise, Idaho, US","countryCode":"US","isUs":true,"latitude":43.616613,"longitude":-116.200886},"current":{"temperature":32,"unit":"F","condition":"partly sunny","wind":"3 mph","humidityPercent":34},"daily":[{"date":"2026-02-10","tempHigh":48,"tempLow":38,"avgTemp":43,"unit":"F","condition":"partly sunny"}],"alerts":[]}""";
        var newsSearchResult =
            "1. Boise city update — example.com\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/boise-news\",\"title\":\"Boise city update\"}]";

        var mcp = new FakeMcpClient((tool, _) => tool switch
        {
            "weather_geocode" => boiseGeocode,
            "weather_forecast" => boiseForecast,
            "web_search" => newsSearchResult,
            _ => "unexpected tool call"
        });
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var weather = await agent.ProcessAsync("Whats the weather like in Boise, ID?");
        Assert.True(weather.Success);

        var news = await agent.ProcessAsync("oh cool! can i get the news?");
        Assert.True(news.Success);

        var webSearchCall = mcp.Calls.Last(c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("boise", webSearchCall.Args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NewsSearch_EntityResolution_ProducesGoodQuery()
    {
        var llm = MakePipelineLlm(
            entityJson:  """{"name":"SpaceX","type":"Org","hint":"Elon Musk's space company"}""",
            queryJson:   """{"query":"SpaceX latest news","recency":"week"}""",
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
            entityJson:  """{"name":"SpaceX","type":"Org","hint":"space company"}""",
            queryJson:   """{"query":"SpaceX news","recency":"week"}""",
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
            entityJson:  """{"name":"","type":"none","hint":""}""",
            queryJson:   """{"query":"tech layoffs 2026","recency":"week"}""",
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
            ReasoningGuardrailsMode = "off"
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
            ReasoningGuardrailsMode = "auto"
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
