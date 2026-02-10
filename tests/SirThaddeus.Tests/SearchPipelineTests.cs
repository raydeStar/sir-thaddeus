using SirThaddeus.Agent;
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
    [InlineData("convert 100 fahrenheit to celsius", "conversion")]
    [InlineData("convert 5 lbs to kg",         "conversion")]
    public void Conversion_ReturnsInlineAnswer(string input, string category)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.NotNull(result);
        Assert.Equal(category, result!.Category);
        Assert.Null(result.McpToolName);
    }

    [Theory]
    [InlineData("time in Tokyo",           "time")]
    [InlineData("what's the time in London", "time")]
    public void TimeZone_ReturnsInlineAnswer(string input, string category)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.NotNull(result);
        Assert.Equal(category, result!.Category);
        Assert.Null(result.McpToolName); // Known cities resolve inline
    }

    [Theory]
    [InlineData("weather in Seattle")]
    [InlineData("forecast for New York")]
    [InlineData("what is the weather like in Rexburg, ID?")]
    public void Weather_RoutesToWebSearch(string input)
    {
        var result = UtilityRouter.TryHandle(input);
        Assert.NotNull(result);
        Assert.Equal("weather", result!.Category);
        Assert.Equal("web_search", result.McpToolName);
    }

    [Fact]
    public void Weather_Query_IsConstrainedForLocation()
    {
        var result = UtilityRouter.TryHandle("what is the weather like in Rexburg, ID? please");
        Assert.NotNull(result);
        Assert.Equal("web_search", result!.McpToolName);
        Assert.NotNull(result.McpToolArgs);
        Assert.Contains("\"query\":\"Rexburg, ID weather forecast today\"", result.McpToolArgs);
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
    public void NonUtilityQueries_ReturnNull(string input)
    {
        var result = UtilityRouter.TryHandle(input);
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
            "  {\"url\":\"https://source1.com/article\",\"title\":\"Headline One\",\"domain\":\"source1.com\"},\n" +
            "  {\"url\":\"https://source2.com/article\",\"title\":\"Headline Two\",\"domain\":\"source2.com\"}\n" +
            "]";

        var sources = SearchOrchestrator.ParseSourcesFromToolResult(toolResult);

        Assert.Equal(2, sources.Count);
        Assert.Equal("Headline One", sources[0].Title);
        Assert.Equal("https://source1.com/article", sources[0].Url);
        Assert.False(string.IsNullOrWhiteSpace(sources[0].SourceId));
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

        // No web_search calls
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

        var searchCalls = mcp.Calls.Where(c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(searchCalls);
    }

    [Fact]
    public async Task UtilityBypass_Weather_UsesSingleSearchCall()
    {
        var llm = new FakeLlmClient("Rexburg is currently cool with light winds.");
        var searchResult =
            "1. \"Rexburg weather\" — weather.example\n" +
            "   Current conditions are 39F with light winds.\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://weather.example/rexburg\",\"title\":\"Rexburg weather\"}]";

        var mcp = new FakeMcpClient(returnValue: searchResult);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("What is the weather like in Rexburg, ID?");

        Assert.True(result.Success);
        Assert.Contains("Rexburg", result.Text, StringComparison.OrdinalIgnoreCase);

        var webSearchCalls = mcp.Calls.Where(c =>
            c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.Single(webSearchCalls);
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
}

#endregion
