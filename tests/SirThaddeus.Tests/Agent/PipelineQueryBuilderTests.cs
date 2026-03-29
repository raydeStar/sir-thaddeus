using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Tests;

public sealed class PipelineQueryBuilderTests
{
    [Fact]
    public async Task ChatIntent_ProducesInlineAnswer()
    {
        var classified = BuildSingleClassified(Intents.ChatOnly, PipelineIntentType.Chat, "hello there");
        var builder = new PipelineQueryBuilder();

        var result = await builder.BuildAsync(classified, new QueryBuilderContext());

        Assert.Single(result.Queries);
        Assert.False(result.Queries[0].RequiresExecution);
        Assert.NotEmpty(result.Queries[0].InlineAnswer);
    }

    [Fact]
    public async Task SearchIntent_ProducesSearchQuery()
    {
        var classified = BuildSingleClassified(Intents.LookupNews, PipelineIntentType.WebSearch, "latest AI news");
        var builder = new PipelineQueryBuilder();

        var result = await builder.BuildAsync(classified, new QueryBuilderContext());

        Assert.Single(result.Queries);
        Assert.True(result.Queries[0].RequiresExecution);
        Assert.Contains("AI news", result.Queries[0].SearchQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchQuery_StripsFiller()
    {
        var classified = BuildSingleClassified(
            Intents.LookupSearch,
            PipelineIntentType.WebSearch,
            "can you please tell me the latest news about AI");
        var builder = new PipelineQueryBuilder();

        var result = await builder.BuildAsync(classified, new QueryBuilderContext());

        var query = result.Queries[0].SearchQuery;
        Assert.DoesNotContain("can you", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("please", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tell me", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("news", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WeatherQuery_InjectsCity()
    {
        var classified = BuildSingleClassified(
            Intents.LookupSearch,
            PipelineIntentType.WebSearch,
            "weather today");
        var builder = new PipelineQueryBuilder();

        var ctx = new QueryBuilderContext { UserCity = "Denver" };
        var result = await builder.BuildAsync(classified, ctx);

        Assert.Contains("Denver", result.Queries[0].SearchQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoGreetingLeaksIntoSearchQuery()
    {
        // Simulate the multi-intent case where greeting is separate
        var preprocessor = new RequestPreprocessor();
        var preprocessed = preprocessor.Decompose("Hey! What's the latest AI news?");

        // Simulate classifier: first intent is chat, second is web search
        var classified = new ClassifierResult
        {
            ClassifiedIntents = preprocessed.Intents.Select((intent, i) => new ClassifiedIntent
            {
                Source = intent,
                ResolvedIntent = i == 0 ? Intents.ChatOnly : Intents.LookupNews,
                RouterOutput = new RouterOutput
                {
                    Intent = i == 0 ? Intents.ChatOnly : Intents.LookupNews,
                    NeedsSearch = i > 0,
                    Confidence = 0.95
                },
                Policy = PolicyGate.Evaluate(new RouterOutput
                {
                    Intent = i == 0 ? Intents.ChatOnly : Intents.LookupNews,
                    NeedsSearch = i > 0,
                    Confidence = 0.95
                }),
                MappedType = i == 0 ? PipelineIntentType.Chat : PipelineIntentType.WebSearch,
                Confidence = 0.95
            }).ToList(),
            AllDeterministic = true
        };

        var builder = new PipelineQueryBuilder();
        var result = await builder.BuildAsync(classified, new QueryBuilderContext());

        // The search query should not contain greeting text
        var searchQueries = result.Queries.Where(q => !string.IsNullOrWhiteSpace(q.SearchQuery)).ToList();
        foreach (var sq in searchQueries)
        {
            Assert.DoesNotContain("hey", sq.SearchQuery, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hello", sq.SearchQuery, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task QueryLength_IsBounded()
    {
        var longInput = string.Join(" ", Enumerable.Repeat("test word", 100));
        var classified = BuildSingleClassified(Intents.LookupSearch, PipelineIntentType.WebSearch, longInput);
        var builder = new PipelineQueryBuilder();

        var result = await builder.BuildAsync(classified, new QueryBuilderContext());

        Assert.True(result.Queries[0].SearchQuery.Length <= 200);
    }

    [Fact]
    public void StripFiller_RemovesCommonPhrases()
    {
        Assert.DoesNotContain("can you", PipelineQueryBuilder.StripFiller("can you find AI news"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("please", PipelineQueryBuilder.StripFiller("please search for cats"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsLocationSensitive_DetectsWeatherAndLocal()
    {
        Assert.True(PipelineQueryBuilder.IsLocationSensitive("weather today"));
        Assert.True(PipelineQueryBuilder.IsLocationSensitive("restaurants near me"));
        Assert.True(PipelineQueryBuilder.IsLocationSensitive("local events"));
        Assert.False(PipelineQueryBuilder.IsLocationSensitive("who invented the internet"));
    }

    [Fact]
    public void ExtractFilePath_FindsQuotedPaths()
    {
        Assert.Equal("C:\\docs\\readme.md", PipelineQueryBuilder.ExtractFilePath("read \"C:\\docs\\readme.md\"", ""));
    }

    [Fact]
    public void ExtractFilePath_FindsExtensionPaths()
    {
        Assert.Equal("readme.md", PipelineQueryBuilder.ExtractFilePath("open readme.md", ""));
    }

    [Fact]
    public void HasExplicitLocation_DetectsInCity()
    {
        Assert.True(PipelineQueryBuilder.HasExplicitLocation("weather in Seattle"));
        Assert.True(PipelineQueryBuilder.HasExplicitLocation("restaurants near Denver"));
        Assert.True(PipelineQueryBuilder.HasExplicitLocation("traffic around Chicago"));
        Assert.False(PipelineQueryBuilder.HasExplicitLocation("weather today"));
        Assert.False(PipelineQueryBuilder.HasExplicitLocation("local news"));
    }

    [Fact]
    public async Task SearchQuery_WithExplicitCity_DoesNotAppendUserCity()
    {
        var classified = BuildSingleClassified(
            Intents.LookupFact, PipelineIntentType.WebSearch,
            "What's the weather like in Seattle");

        var builder = new PipelineQueryBuilder();
        var context = new QueryBuilderContext { UserCity = "Rexburg, ID" };
        var result = await builder.BuildAsync(classified, context);

        Assert.DoesNotContain("Rexburg", result.Queries[0].SearchQuery);
        Assert.Contains("Seattle", result.Queries[0].SearchQuery);
    }

    [Fact]
    public async Task SearchQuery_WithoutCity_AppendsUserCity()
    {
        var classified = BuildSingleClassified(
            Intents.LookupFact, PipelineIntentType.WebSearch,
            "What's the weather today");

        var builder = new PipelineQueryBuilder();
        var context = new QueryBuilderContext { UserCity = "Rexburg, ID" };
        var result = await builder.BuildAsync(classified, context);

        Assert.Contains("Rexburg", result.Queries[0].SearchQuery);
    }

    [Fact]
    public async Task VagueBakeryFollowUp_UsesAssistantContextAnchor()
    {
        var classified = BuildSingleClassified(
            Intents.LookupSearch, PipelineIntentType.WebSearch,
            "Pull up more info about that bakery.");

        var builder = new PipelineQueryBuilder();
        var context = new QueryBuilderContext
        {
            RecentMessages =
            [
                ("assistant", "Here's a bakery I found nearby in Olympia: Left Bank Pastry at 108 5th Ave SW, Olympia, WA.")
            ]
        };

        var result = await builder.BuildAsync(classified, context);

        Assert.Contains("Left Bank Pastry", result.Queries[0].SearchQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("that bakery", result.Queries[0].SearchQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitBakeryFollowUp_KeepsNamedSubject()
    {
        var classified = BuildSingleClassified(
            Intents.LookupSearch, PipelineIntentType.WebSearch,
            "Pull up more info about Left Bank Pastry.");

        var builder = new PipelineQueryBuilder();
        var context = new QueryBuilderContext
        {
            RecentMessages =
            [
                ("assistant", "Here's a bakery I found nearby in Olympia: Wagner's European Bakery and Cafe at 1013 Capitol Way S, Olympia, WA.")
            ]
        };

        var result = await builder.BuildAsync(classified, context);

        Assert.Contains("Left Bank Pastry", result.Queries[0].SearchQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Wagner", result.Queries[0].SearchQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VagueFollowUp_UsesExplicitAnchorBeforeAssistantParsing()
    {
        var classified = BuildSingleClassified(
            Intents.LookupSearch, PipelineIntentType.WebSearch,
            "Pull up more info about that bakery.");

        var builder = new PipelineQueryBuilder();
        var context = new QueryBuilderContext
        {
            FollowUpAnchor = "Left Bank Pastry",
            RecentMessages =
            [
                ("assistant", "Here's a bakery I found nearby in Olympia: Wagner's European Bakery and Cafe at 1013 Capitol Way S, Olympia, WA.")
            ]
        };

        var result = await builder.BuildAsync(classified, context);

        Assert.Equal("Left Bank Pastry", result.Queries[0].SearchQuery);
    }

    private static ClassifierResult BuildSingleClassified(
        string intent, PipelineIntentType type, string normalizedRequest)
    {
        return new ClassifierResult
        {
            ClassifiedIntents =
            [
                new ClassifiedIntent
                {
                    Source = new PipelineIntent
                    {
                        OriginalFragment = normalizedRequest,
                        NormalizedRequest = normalizedRequest,
                        Order = 0
                    },
                    ResolvedIntent = intent,
                    RouterOutput = new RouterOutput
                    {
                        Intent = intent,
                        NeedsSearch = type == PipelineIntentType.WebSearch,
                        Confidence = 0.95
                    },
                    Policy = PolicyGate.Evaluate(new RouterOutput
                    {
                        Intent = intent,
                        NeedsSearch = type == PipelineIntentType.WebSearch,
                        Confidence = 0.95
                    }),
                    MappedType = type,
                    Confidence = 0.95
                }
            ],
            AllDeterministic = true
        };
    }
}
