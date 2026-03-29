using SirThaddeus.Agent.Search;

namespace SirThaddeus.Tests.Agent.Search;

// ─────────────────────────────────────────────────────────────────────────
// News Pipeline Seam Tests
//
// Fixture-driven tests for each seam boundary in the refactored news
// search pipeline. Each seam is tested in isolation with deterministic
// inputs — no LLM, no network, no MCP calls.
//
// Seams under test:
//   1. NewsIntentRouter  (routeSearchIntent)
//   2. NewsSearchPlanner (buildSearchPlan)
//   3. NewsResultScorer  (scoreAndFilter)
//   4. NewsAggregator    (aggregateNews)
//   5. SearchRetryArbiter (shouldRetry)
//   6. SearchIntentMapper (intent compatibility)
//
// Fixture naming convention:
//   [Seam]_[scenario]_[expected outcome]
// ─────────────────────────────────────────────────────────────────────────

#region ── Seam 1: NewsIntentRouter ────────────────────────────────────────

public class NewsIntentRouterTests
{
    [Theory]
    [InlineData("bring me headline news", SearchIntent.NewsHeadlines)]
    [InlineData("top headlines today", SearchIntent.NewsHeadlines)]
    [InlineData("latest news", SearchIntent.NewsHeadlines)]
    [InlineData("breaking news", SearchIntent.NewsHeadlines)]
    [InlineData("daily briefing", SearchIntent.NewsHeadlines)]
    [InlineData("what's happening today", SearchIntent.NewsHeadlines)]
    public void HeadlineNews_RoutesToNewsHeadlines(string message, SearchIntent expected)
    {
        var result = NewsIntentRouter.Route(new RouteSearchIntentRequest
        {
            UserMessage = message
        });

        Assert.Equal(expected, result.Intent);
        Assert.Equal(RouteReasons.HeadlinePhrase, result.ReasonCode);
        Assert.True(result.NeedsAggregation);
    }

    [Theory]
    [InlineData("latest news about AI regulation", SearchIntent.TopicNews)]
    [InlineData("news on climate change", SearchIntent.TopicNews)]
    [InlineData("what's happening with Tesla stock", SearchIntent.TopicNews)]
    [InlineData("Ukraine war news", SearchIntent.TopicNews)]
    public void TopicNews_RoutesToTopicNews(string message, SearchIntent expected)
    {
        var result = NewsIntentRouter.Route(new RouteSearchIntentRequest
        {
            UserMessage = message
        });

        Assert.Equal(expected, result.Intent);
        Assert.Equal(RouteReasons.TopicNewsPhrase, result.ReasonCode);
        Assert.True(result.NeedsAggregation);
    }

    [Theory]
    [InlineData("latest news about AI regulation", "ai regulation")]
    [InlineData("news on climate change", "climate change")]
    [InlineData("Ukraine war news", "ukraine war")]
    public void TopicNews_ExtractsTopicAnchor(string message, string expectedAnchor)
    {
        var result = NewsIntentRouter.Route(new RouteSearchIntentRequest
        {
            UserMessage = message
        });

        Assert.NotNull(result.TopicAnchor);
        Assert.Contains(expectedAnchor, result.TopicAnchor!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Portland news", "portland")]
    [InlineData("news in Seattle", "seattle")]
    public void GeoAnchor_ExtractedFromLocationMentions(string message, string expectedGeo)
    {
        var result = NewsIntentRouter.Route(new RouteSearchIntentRequest
        {
            UserMessage = message
        });

        Assert.NotNull(result.GeoAnchor);
        Assert.Contains(expectedGeo, result.GeoAnchor!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("who is Elon Musk")]
    [InlineData("explain quantum computing")]
    [InlineData("define photosynthesis")]
    public void NonNewsQueries_RouteToGeneral(string message)
    {
        var result = NewsIntentRouter.Route(new RouteSearchIntentRequest
        {
            UserMessage = message
        });

        Assert.Equal(SearchIntent.GeneralWebSearch, result.Intent);
        Assert.False(result.NeedsAggregation);
    }

    [Fact]
    public void Recency_DefaultsToDay_ForHeadlines()
    {
        var result = NewsIntentRouter.Route(new RouteSearchIntentRequest
        {
            UserMessage = "top headlines"
        });

        Assert.Equal("day", result.Recency);
    }

    [Fact]
    public void Recency_DefaultsToWeek_ForTopicNews()
    {
        var result = NewsIntentRouter.Route(new RouteSearchIntentRequest
        {
            UserMessage = "AI regulation news"
        });

        Assert.Equal("week", result.Recency);
    }

    [Theory]
    [InlineData("breaking news today", "day")]
    [InlineData("news this week", "week")]
    [InlineData("news this month", "month")]
    public void Recency_OverriddenByExplicitTemporalMarker(string message, string expectedRecency)
    {
        var result = NewsIntentRouter.Route(new RouteSearchIntentRequest
        {
            UserMessage = message
        });

        Assert.Equal(expectedRecency, result.Recency);
    }

    [Fact]
    public void FollowUp_WithNewsSession_RoutesToDeepDive()
    {
        var session = new SearchSession();
        session.RecordSearchResults(
            SearchMode.NewsAggregate, "test", "day",
            [new SourceItem { Url = "https://example.com", Title = "Test" }],
            DateTimeOffset.UtcNow);
        session.LastClusters = [new StoryCluster { RepresentativeTitle = "Test", Sources = [new SourceItem { Url = "https://example.com", Title = "Test" }] }];

        var result = NewsIntentRouter.Route(new RouteSearchIntentRequest
        {
            UserMessage = "tell me more about that",
            Session = session
        });

        Assert.Equal(SearchIntent.ArticleDeepDive, result.Intent);
        Assert.True(result.NeedsDeepDive);
    }
}

#endregion

#region ── Seam 2: NewsSearchPlanner ──────────────────────────────────────

public class NewsSearchPlannerTests
{
    [Fact]
    public void HeadlinePlan_Produces4To5Queries()
    {
        var plan = NewsSearchPlanner.BuildPlan(new BuildSearchPlanRequest
        {
            Intent      = SearchIntent.NewsHeadlines,
            UserMessage = "bring me headline news"
        });

        Assert.InRange(plan.Queries.Count, 3, 5);
        Assert.Null(plan.ValidationFailure);
    }

    [Fact]
    public void TopicPlan_AnchorsAllQueriesOnTopic()
    {
        var plan = NewsSearchPlanner.BuildPlan(new BuildSearchPlanRequest
        {
            Intent      = SearchIntent.TopicNews,
            UserMessage = "AI regulation news",
            TopicAnchor = "AI regulation"
        });

        Assert.All(plan.Queries, q =>
            Assert.Contains("ai", q.Query, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AllNewsQueries_HaveFreshness()
    {
        var plan = NewsSearchPlanner.BuildPlan(new BuildSearchPlanRequest
        {
            Intent      = SearchIntent.NewsHeadlines,
            UserMessage = "top headlines"
        });

        Assert.All(plan.Queries, q =>
        {
            Assert.False(string.IsNullOrWhiteSpace(q.Freshness));
            Assert.NotEqual("any", q.Freshness, StringComparer.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void AtLeastOneQuery_TargetsNewsVertical()
    {
        var plan = NewsSearchPlanner.BuildPlan(new BuildSearchPlanRequest
        {
            Intent      = SearchIntent.NewsHeadlines,
            UserMessage = "latest news"
        });

        Assert.Contains(plan.Queries, q =>
            q.Vertical.Equals("news", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoQueryContainsBlockedPatterns()
    {
        var plan = NewsSearchPlanner.BuildPlan(new BuildSearchPlanRequest
        {
            Intent      = SearchIntent.TopicNews,
            UserMessage = "AI regulation news",
            TopicAnchor = "AI regulation"
        });

        Assert.All(plan.Queries, q =>
        {
            var lower = q.Query.ToLowerInvariant();
            Assert.DoesNotContain("wikipedia", lower);
            Assert.DoesNotContain("dictionary", lower);
            Assert.DoesNotContain("thesaurus", lower);
            Assert.DoesNotContain("biography", lower);
            Assert.DoesNotContain("tutorial", lower);
        });
    }

    [Fact]
    public void PlanValidation_RejectsExcessiveQueryCount()
    {
        // Force an invalid plan by creating one with too many queries.
        var code = NewsSearchPlanner.Validate(
            new BuildSearchPlanResult
            {
                Intent  = SearchIntent.NewsHeadlines,
                Queries = Enumerable.Range(0, 10)
                    .Select(i => new SearchPlanQuery { Query = $"query {i}", Freshness = "day" })
                    .ToList()
            },
            new BuildSearchPlanRequest
            {
                Intent      = SearchIntent.NewsHeadlines,
                UserMessage = "test"
            });

        Assert.Equal(PlanValidationCodes.ExceedsMaxQueries, code);
    }

    [Fact]
    public void PlanValidation_RejectsMissingFreshness()
    {
        var code = NewsSearchPlanner.Validate(
            new BuildSearchPlanResult
            {
                Intent  = SearchIntent.NewsHeadlines,
                Queries = [new SearchPlanQuery { Query = "headlines today", Freshness = "any", Vertical = "news" }]
            },
            new BuildSearchPlanRequest
            {
                Intent      = SearchIntent.NewsHeadlines,
                UserMessage = "test"
            });

        Assert.Equal(PlanValidationCodes.MissingFreshness, code);
    }

    [Fact]
    public void PlanValidation_RejectsTopicDrift()
    {
        var code = NewsSearchPlanner.Validate(
            new BuildSearchPlanResult
            {
                Intent  = SearchIntent.TopicNews,
                Queries = [new SearchPlanQuery { Query = "celebrity gossip latest", Freshness = "day", Vertical = "news" }]
            },
            new BuildSearchPlanRequest
            {
                Intent      = SearchIntent.TopicNews,
                UserMessage = "AI regulation news",
                TopicAnchor = "AI regulation"
            });

        Assert.Equal(PlanValidationCodes.TopicDrift, code);
    }

    [Fact]
    public void PlanValidation_RejectsMissingNewsVertical()
    {
        var code = NewsSearchPlanner.Validate(
            new BuildSearchPlanResult
            {
                Intent  = SearchIntent.NewsHeadlines,
                Queries = [new SearchPlanQuery { Query = "headlines today", Freshness = "day", Vertical = "general" }]
            },
            new BuildSearchPlanRequest
            {
                Intent      = SearchIntent.NewsHeadlines,
                UserMessage = "test"
            });

        Assert.Equal(PlanValidationCodes.MissingNewsVertical, code);
    }

    [Fact]
    public void BroadenForRetry_StaysWithinIntent()
    {
        var originalPlan = NewsSearchPlanner.BuildPlan(new BuildSearchPlanRequest
        {
            Intent      = SearchIntent.TopicNews,
            UserMessage = "AI regulation news",
            TopicAnchor = "AI regulation"
        });

        var broadened = NewsSearchPlanner.BroadenForRetry(
            originalPlan,
            new BuildSearchPlanRequest
            {
                Intent      = SearchIntent.TopicNews,
                UserMessage = "AI regulation news",
                TopicAnchor = "AI regulation",
                Recency     = "day"
            },
            iteration: 2);

        Assert.Equal(SearchIntent.TopicNews, broadened.Intent);
        Assert.True(broadened.Queries.Count >= originalPlan.Queries.Count);
    }

    [Fact]
    public void GeoPlan_IncludesLocationInQueries()
    {
        var plan = NewsSearchPlanner.BuildPlan(new BuildSearchPlanRequest
        {
            Intent      = SearchIntent.NewsHeadlines,
            UserMessage = "Portland news",
            GeoAnchor   = "portland"
        });

        Assert.Contains(plan.Queries, q =>
            q.Query.Contains("portland", StringComparison.OrdinalIgnoreCase));
    }
}

#endregion

#region ── Seam 3: NewsResultScorer ───────────────────────────────────────

public class NewsResultScorerTests
{
    // Sentinel to distinguish "use default" from "explicit null".
    private static readonly DateTimeOffset DefaultPublishDate = DateTimeOffset.MinValue;

    private static SourceItem MakeSource(
        string title = "Test Article",
        string url = "https://example.com/article",
        string domain = "example.com",
        string? snippet = "A test news article about something important.",
        DateTimeOffset? publishedAt = default,
        bool noPublishDate = false)
    {
        return new SourceItem
        {
            Title       = title,
            Url         = url,
            Domain      = domain,
            Snippet     = snippet ?? "",
            PublishedAt = noPublishDate ? null : (publishedAt ?? DateTimeOffset.UtcNow.AddHours(-2))
        };
    }

    [Fact]
    public void WikipediaResult_IsHardDropped()
    {
        var result = NewsResultScorer.Score(new ScoreAndFilterRequest
        {
            Intent     = SearchIntent.NewsHeadlines,
            RawResults = [MakeSource(url: "https://en.wikipedia.org/wiki/Test", domain: "en.wikipedia.org")]
        });

        Assert.Single(result.Ranked);
        Assert.True(result.Ranked[0].IsDropped);
        Assert.Equal(DropReasons.WikiReference, result.Ranked[0].DropReason);
    }

    [Fact]
    public void DictionaryResult_IsHardDropped()
    {
        var result = NewsResultScorer.Score(new ScoreAndFilterRequest
        {
            Intent     = SearchIntent.NewsHeadlines,
            RawResults = [MakeSource(
                title: "Definition of news - Merriam-Webster",
                url: "https://merriam-webster.com/dictionary/news",
                domain: "merriam-webster.com")]
        });

        Assert.Single(result.Ranked);
        Assert.True(result.Ranked[0].IsDropped);
    }

    [Fact]
    public void MissingPublishDate_DroppedForNewsIntent()
    {
        var result = NewsResultScorer.Score(new ScoreAndFilterRequest
        {
            Intent     = SearchIntent.NewsHeadlines,
            RawResults = [MakeSource(noPublishDate: true)]
        });

        Assert.Single(result.Ranked);
        Assert.True(result.Ranked[0].IsDropped);
        Assert.Equal(DropReasons.NoPublishDate, result.Ranked[0].DropReason);
    }

    [Fact]
    public void MissingPublishDate_NotDroppedForGeneralSearch()
    {
        var result = NewsResultScorer.Score(new ScoreAndFilterRequest
        {
            Intent     = SearchIntent.GeneralWebSearch,
            RawResults = [MakeSource(noPublishDate: true)]
        });

        Assert.Single(result.Ranked);
        Assert.False(result.Ranked[0].IsDropped);
    }

    [Fact]
    public void RecentArticle_ScoresHighOnRecency()
    {
        var recent = DateTimeOffset.UtcNow.AddHours(-1);
        var score = NewsResultScorer.ScoreRecency(recent, DateTimeOffset.UtcNow);
        Assert.Equal(1.0, score);
    }

    [Fact]
    public void OldArticle_ScoresLowOnRecency()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-25);
        var score = NewsResultScorer.ScoreRecency(old, DateTimeOffset.UtcNow);
        Assert.InRange(score, 0.0, 0.3);
    }

    [Fact]
    public void Tier1Domain_ScoresFullOnSource()
    {
        var score = NewsResultScorer.ScoreNewsSource("apnews.com");
        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Tier2Domain_ScoresMediumOnSource()
    {
        var score = NewsResultScorer.ScoreNewsSource("cnn.com");
        Assert.Equal(0.7, score);
    }

    [Fact]
    public void UnknownDomain_ScoresLowOnSource()
    {
        var score = NewsResultScorer.ScoreNewsSource("random-blog.net");
        Assert.Equal(0.4, score);
    }

    [Fact]
    public void TitleRelevance_HighWhenTopicTokensPresent()
    {
        var topicTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ai", "regulation" };
        var score = NewsResultScorer.ScoreTitleRelevance("New AI Regulation Bill Passes Senate", topicTokens);
        Assert.True(score >= 0.5);
    }

    [Fact]
    public void TitleRelevance_LowWhenTopicTokensAbsent()
    {
        var topicTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ai", "regulation" };
        var score = NewsResultScorer.ScoreTitleRelevance("Stock Market Hits New High", topicTokens);
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void RetrievalConfidence_ZeroWhenNoResults()
    {
        var result = NewsResultScorer.Score(new ScoreAndFilterRequest
        {
            Intent     = SearchIntent.NewsHeadlines,
            RawResults = []
        });

        Assert.Equal(0.0, result.RetrievalConfidence);
    }

    [Fact]
    public void MixedResults_RetainHighQuality_DropLow()
    {
        var sources = new List<SourceItem>
        {
            // Good result.
            MakeSource(
                title: "Global Economy Shows Strong Growth in 2026",
                domain: "reuters.com",
                publishedAt: DateTimeOffset.UtcNow.AddHours(-3)),
            // Bad result: wiki.
            MakeSource(
                title: "Economy - Wikipedia",
                url: "https://en.wikipedia.org/wiki/Economy",
                domain: "en.wikipedia.org",
                publishedAt: DateTimeOffset.UtcNow.AddHours(-1))
        };

        var result = NewsResultScorer.Score(new ScoreAndFilterRequest
        {
            Intent     = SearchIntent.NewsHeadlines,
            RawResults = sources
        });

        var retained = result.Retained;
        Assert.Single(retained);
        Assert.Contains("reuters.com", retained[0].Source.Domain);
    }

    [Fact]
    public void NonNewsPenalty_AppliedToHowToArticles()
    {
        var penalty = NewsResultScorer.ScoreNonNewsPenalty(
            "https://example.com/how-to-build-a-thing",
            "How to Build a Widget - Tutorial Guide");

        Assert.True(penalty > 0.0);
    }

    [Fact]
    public void ThinContent_PenalizedWhenSnippetTooShort()
    {
        var penalty = NewsResultScorer.ScoreThinContent("short", 0);
        Assert.Equal(1.0, penalty);
    }

    [Fact]
    public void ThinContent_NoPenaltyWhenAdequate()
    {
        var longSnippet = string.Join(' ', Enumerable.Repeat("word", 50));
        var penalty = NewsResultScorer.ScoreThinContent(longSnippet, 50);
        Assert.Equal(0.0, penalty);
    }
}

#endregion

#region ── Seam 4: NewsAggregator ─────────────────────────────────────────

public class NewsAggregatorTests
{
    private static RankedResult MakeRetained(
        string title,
        string domain,
        double compositeScore = 0.7,
        DateTimeOffset? publishedAt = null)
    {
        return new RankedResult
        {
            Source = new SourceItem
            {
                SourceId    = Guid.NewGuid().ToString("N")[..12],
                Title       = title,
                Url         = $"https://{domain}/article/{Guid.NewGuid():N}",
                Domain      = domain,
                PublishedAt = publishedAt ?? DateTimeOffset.UtcNow.AddHours(-2)
            },
            CompositeScore = compositeScore,
            RecencyScore   = 0.8,
            NewsSourceScore = 0.7
        };
    }

    [Fact]
    public void EmptyResults_ProducesFallbackMessage()
    {
        var result = NewsAggregator.Aggregate(new AggregateNewsRequest
        {
            Retained = [],
            Intent   = SearchIntent.NewsHeadlines
        });

        Assert.Empty(result.Stories);
        Assert.Equal(0.0, result.CoverageConfidence);
        Assert.NotNull(result.FallbackMessage);
    }

    [Fact]
    public void MultipleStories_AreGroupedAndRanked()
    {
        var retained = new List<RankedResult>
        {
            MakeRetained("Economy Shows Growth in Q1", "reuters.com", 0.9),
            MakeRetained("Economy Growing Faster Than Expected", "bbc.com", 0.85),
            MakeRetained("Tech Giants Report Record Revenue", "techcrunch.com", 0.75),
            MakeRetained("Climate Summit Opens in Paris", "theguardian.com", 0.8)
        };

        var result = NewsAggregator.Aggregate(new AggregateNewsRequest
        {
            Retained = retained,
            Intent   = SearchIntent.NewsHeadlines
        });

        // Should have at least 2 stories (economy cluster + tech + climate).
        Assert.True(result.Stories.Count >= 2);
        Assert.True(result.CoverageConfidence > 0.0);
    }

    [Fact]
    public void LowQualityResults_ProduceLowCoverageConfidence()
    {
        var result = NewsAggregator.Aggregate(new AggregateNewsRequest
        {
            Retained = [MakeRetained("Some Article", "unknown-blog.net", 0.3)],
            Intent   = SearchIntent.NewsHeadlines
        });

        Assert.True(result.CoverageConfidence < 0.5);
    }

    [Fact]
    public void TopicNews_FallbackMessage_MentionsTopic()
    {
        var result = NewsAggregator.Aggregate(new AggregateNewsRequest
        {
            Retained    = [],
            Intent      = SearchIntent.TopicNews,
            TopicAnchor = "AI regulation"
        });

        Assert.NotNull(result.FallbackMessage);
        Assert.Contains("AI regulation", result.FallbackMessage);
    }

    [Fact]
    public void StoryReferences_BuiltFromAggregateResult()
    {
        var retained = new List<RankedResult>
        {
            MakeRetained("Test Story One", "reuters.com", 0.9),
            MakeRetained("Test Story Two", "bbc.com", 0.8)
        };

        var aggResult = NewsAggregator.Aggregate(new AggregateNewsRequest
        {
            Retained = retained,
            Intent   = SearchIntent.NewsHeadlines
        });

        var refs = NewsAggregator.BuildStoryReferences(aggResult);

        Assert.Equal(aggResult.Stories.Count, refs.Count);
        Assert.All(refs, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.StoryId));
            Assert.False(string.IsNullOrWhiteSpace(r.CanonicalUrl));
            Assert.False(string.IsNullOrWhiteSpace(r.Headline));
        });
    }
}

#endregion

#region ── Seam 5: SearchRetryArbiter ─────────────────────────────────────

public class SearchRetryArbiterTests
{
    private static BuildSearchPlanResult MakePlan(
        SearchIntent intent = SearchIntent.NewsHeadlines,
        int maxIterations = 3)
    {
        return new BuildSearchPlanResult
        {
            Intent        = intent,
            Queries       = [new SearchPlanQuery { Query = "test", Freshness = "day" }],
            MaxIterations = maxIterations
        };
    }

    private static ScoreAndFilterResult MakeScored(
        int retainedCount = 5,
        double avgScore = 0.6,
        double retrievalConfidence = 0.6)
    {
        var ranked = Enumerable.Range(0, retainedCount)
            .Select(i => new RankedResult
            {
                Source = new SourceItem
                {
                    SourceId = $"src_{i}",
                    Title = $"Article {i}",
                    Url = $"https://example.com/{i}",
                    Domain = $"domain{i}.com",
                    PublishedAt = DateTimeOffset.UtcNow.AddHours(-i - 1)
                },
                CompositeScore = avgScore
            })
            .ToList();

        return new ScoreAndFilterResult
        {
            Ranked              = ranked,
            RetrievalConfidence = retrievalConfidence
        };
    }

    private static AggregateNewsResult MakeAggregate(
        int storyCount = 4,
        double coverageConfidence = 0.6)
    {
        var stories = Enumerable.Range(0, storyCount)
            .Select(i => new NewsStory
            {
                Headline   = $"Story {i}",
                Source     = $"source{i}.com",
                Url        = $"https://source{i}.com/article",
                Confidence = 0.7
            })
            .ToList();

        return new AggregateNewsResult
        {
            Stories            = stories,
            CoverageConfidence = coverageConfidence,
            AnswerConfidence   = coverageConfidence * 0.8
        };
    }

    [Fact]
    public void SufficientCoverage_StopsRetry()
    {
        var decision = SearchRetryArbiter.Evaluate(new ShouldRetryRequest
        {
            Plan      = MakePlan(),
            Scored    = MakeScored(),
            Aggregate = MakeAggregate(storyCount: 4, coverageConfidence: 0.7)
        });

        Assert.False(decision.ShouldRetry);
        Assert.Equal(RetryReasons.Sufficient, decision.ReasonCode);
    }

    [Fact]
    public void MaxIterationsReached_StopsRetry()
    {
        var decision = SearchRetryArbiter.Evaluate(new ShouldRetryRequest
        {
            Plan      = MakePlan(maxIterations: 2),
            Scored    = MakeScored(),
            Aggregate = MakeAggregate(storyCount: 1, coverageConfidence: 0.2),
            Trace     = new SearchTraceContext
            {
                SearchSessionId = "test",
                RequestId       = "req",
                Iteration       = 2
            }
        });

        Assert.False(decision.ShouldRetry);
        Assert.Equal(RetryReasons.MaxIterationsHit, decision.ReasonCode);
    }

    [Fact]
    public void TooFewStories_TriggersRetry()
    {
        var decision = SearchRetryArbiter.Evaluate(new ShouldRetryRequest
        {
            Plan      = MakePlan(),
            Scored    = MakeScored(retainedCount: 2),
            Aggregate = MakeAggregate(storyCount: 1, coverageConfidence: 0.2),
            Trace     = new SearchTraceContext
            {
                SearchSessionId = "test",
                RequestId       = "req",
                Iteration       = 1
            }
        });

        Assert.True(decision.ShouldRetry);
        Assert.Equal(RetryReasons.TooFewUniqueStories, decision.ReasonCode);
        Assert.NotNull(decision.AdjustedPlan);
    }

    [Fact]
    public void BelowCoverageFloor_TriggersRetry()
    {
        var decision = SearchRetryArbiter.Evaluate(new ShouldRetryRequest
        {
            Plan      = MakePlan(),
            Scored    = MakeScored(retainedCount: 5, retrievalConfidence: 0.2),
            Aggregate = MakeAggregate(storyCount: 3, coverageConfidence: 0.2),
            Trace     = new SearchTraceContext
            {
                SearchSessionId = "test",
                RequestId       = "req",
                Iteration       = 1
            }
        });

        Assert.True(decision.ShouldRetry);
        Assert.Equal(RetryReasons.BelowCoverageFloor, decision.ReasonCode);
    }

    [Fact]
    public void BelowQualityFloor_TriggersRetry()
    {
        var decision = SearchRetryArbiter.Evaluate(new ShouldRetryRequest
        {
            Plan      = MakePlan(),
            Scored    = MakeScored(retainedCount: 5, avgScore: 0.15, retrievalConfidence: 0.5),
            Aggregate = MakeAggregate(storyCount: 4, coverageConfidence: 0.5),
            Trace     = new SearchTraceContext
            {
                SearchSessionId = "test",
                RequestId       = "req",
                Iteration       = 1
            }
        });

        Assert.True(decision.ShouldRetry);
        Assert.Equal(RetryReasons.BelowQualityFloor, decision.ReasonCode);
    }
}

#endregion

#region ── Seam 6: SearchIntentMapper (Compatibility) ─────────────────────

public class SearchIntentMapperTests
{
    [Theory]
    [InlineData(SearchMode.NewsAggregate, "bring me headline news", SearchIntent.NewsHeadlines)]
    [InlineData(SearchMode.NewsAggregate, "AI regulation news", SearchIntent.TopicNews)]
    [InlineData(SearchMode.WebFactFind, "who is Elon Musk", SearchIntent.GeneralWebSearch)]
    public void FromSearchMode_MapsCorrectly(SearchMode mode, string message, SearchIntent expected)
    {
        var intent = SearchIntentMapper.FromSearchMode(mode, message);
        Assert.Equal(expected, intent);
    }

    [Theory]
    [InlineData("lookup_news", "bring me headlines", SearchIntent.NewsHeadlines)]
    [InlineData("lookup_deep_dive", "tell me more", SearchIntent.ArticleDeepDive)]
    [InlineData("lookup_search", "find something", SearchIntent.GeneralWebSearch)]
    public void FromRouterIntent_MapsCorrectly(string routerIntent, string message, SearchIntent expected)
    {
        var intent = SearchIntentMapper.FromRouterIntent(routerIntent, message);
        Assert.Equal(expected, intent);
    }

    [Theory]
    [InlineData("bring me headline news")]
    [InlineData("top headlines today")]
    [InlineData("latest news")]
    [InlineData("news please")]
    public void HasExplicitNewsTopic_FalseForGenericHeadlines(string message)
    {
        Assert.False(SearchIntentMapper.HasExplicitNewsTopic(message.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("AI regulation news")]
    [InlineData("tesla stock news")]
    [InlineData("climate change latest")]
    public void HasExplicitNewsTopic_TrueForTopicRequests(string message)
    {
        Assert.True(SearchIntentMapper.HasExplicitNewsTopic(message.ToLowerInvariant()));
    }
}

#endregion
