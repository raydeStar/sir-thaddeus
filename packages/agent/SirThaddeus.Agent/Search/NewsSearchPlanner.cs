using SirThaddeus.AuditLog;

namespace SirThaddeus.Agent.Search;

// ─────────────────────────────────────────────────────────────────────────
// News Search Planner — Seam 2 Implementation
//
// Builds a constrained search plan for news intents. Every plan contains
// 3–5 deterministic query variants with mandatory freshness on each.
//
// Hard-stop validation rejects plans that:
//   1. Contain blocked patterns (wikipedia, dictionary, thesaurus, etc.)
//   2. Exceed maximum query count
//   3. Miss freshness on any news query
//   4. Drift from the topic anchor (TOPIC_NEWS)
//   5. Miss news vertical when required
//
// Plans that fail validation are replaced with a safe deterministic
// fallback — never silently passed through.
// ─────────────────────────────────────────────────────────────────────────

public static class NewsSearchPlanner
{
    // ── Blocked patterns — these should never appear in news queries ──
    private static readonly string[] BlockedPatterns =
    [
        "wikipedia",
        "wiki",
        "thesaurus",
        "dictionary",
        "definition of",
        "define ",
        "biography",
        "biography of",
        "help center",
        "help forum",
        "stack overflow",
        "stackoverflow",
        "how to ",
        "tutorial",
        "recipe for",
        "lyrics to"
    ];

    // ── Query diversity templates for headline news ──────────────────

    private static readonly string[] HeadlineTemplates =
    [
        "top headlines today",
        "breaking news today",
        "latest world news",
        "current events today",
        "major news stories today"
    ];

    // ── Configuration ────────────────────────────────────────────────

    private const int MinQueriesPerPlan = 3;
    private const int MaxQueriesPerPlan = 5;
    private const int DefaultMinResults = 8;
    private const int DefaultMaxIterations = 3;

    // ─────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a constrained search plan for a news request. The plan
    /// is validated before return — invalid plans are replaced with
    /// a safe fallback and carry a <see cref="BuildSearchPlanResult.ValidationFailure"/>.
    /// </summary>
    public static BuildSearchPlanResult BuildPlan(BuildSearchPlanRequest request)
    {
        var queries = request.Intent switch
        {
            SearchIntent.NewsHeadlines => BuildHeadlineQueries(request),
            SearchIntent.TopicNews     => BuildTopicNewsQueries(request),
            _                          => BuildGenericNewsQueries(request)
        };

        var plan = new BuildSearchPlanResult
        {
            Intent          = request.Intent,
            Queries         = queries,
            BlockedPatterns = BlockedPatterns,
            MinResults      = DefaultMinResults,
            MaxIterations   = DefaultMaxIterations,
            ValidationFailure = null
        };

        // ── Hard-stop validation ─────────────────────────────────────
        var validationCode = Validate(plan, request);
        if (validationCode != PlanValidationCodes.Valid)
        {
            // Build deterministic fallback plan instead of passing
            // a broken plan through.
            var fallbackQueries = BuildFallbackQueries(request);
            plan = plan with
            {
                Queries           = fallbackQueries,
                ValidationFailure = validationCode
            };
        }

        return plan;
    }

    // ─────────────────────────────────────────────────────────────────
    // Query Builders — Per Intent
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds 4–5 diverse headline queries covering different news angles.
    /// No topic anchoring — these are broad headline sweeps.
    /// </summary>
    private static IReadOnlyList<SearchPlanQuery> BuildHeadlineQueries(
        BuildSearchPlanRequest request)
    {
        var freshness = ResolveDefaultFreshness(request.Recency);
        var geo = request.GeoAnchor;

        var queries = new List<SearchPlanQuery>();

        if (!string.IsNullOrWhiteSpace(geo))
        {
            // Geo-scoped headline queries.
            queries.Add(new SearchPlanQuery { Query = $"{geo} news today",       Freshness = freshness, Vertical = "news" });
            queries.Add(new SearchPlanQuery { Query = $"{geo} latest headlines",  Freshness = freshness, Vertical = "news" });
            queries.Add(new SearchPlanQuery { Query = $"{geo} breaking news",     Freshness = freshness, Vertical = "general" });
            queries.Add(new SearchPlanQuery { Query = "top headlines today",      Freshness = freshness, Vertical = "news" });
        }
        else
        {
            // Broad headline sweep.
            queries.Add(new SearchPlanQuery { Query = "top headlines today",      Freshness = freshness, Vertical = "news" });
            queries.Add(new SearchPlanQuery { Query = "breaking news today",      Freshness = freshness, Vertical = "news" });
            queries.Add(new SearchPlanQuery { Query = "latest world news",        Freshness = freshness, Vertical = "general" });
            queries.Add(new SearchPlanQuery { Query = "current events today",     Freshness = freshness, Vertical = "news" });
            queries.Add(new SearchPlanQuery { Query = "major news stories today", Freshness = freshness, Vertical = "general" });
        }

        return queries;
    }

    /// <summary>
    /// Builds 3–4 topic-anchored queries. Every query must contain the
    /// topic anchor to prevent drift.
    /// </summary>
    private static IReadOnlyList<SearchPlanQuery> BuildTopicNewsQueries(
        BuildSearchPlanRequest request)
    {
        var topic = request.TopicAnchor ?? ExtractFallbackTopic(request.UserMessage);
        var freshness = ResolveDefaultFreshness(request.Recency);
        var geo = request.GeoAnchor;

        var queries = new List<SearchPlanQuery>();

        // Primary: topic + news + freshness marker.
        queries.Add(new SearchPlanQuery
        {
            Query     = $"{topic} latest news",
            Freshness = freshness,
            Vertical  = "news"
        });

        // Secondary: topic + coverage angle.
        queries.Add(new SearchPlanQuery
        {
            Query     = $"{topic} news coverage",
            Freshness = freshness,
            Vertical  = "news"
        });

        // Tertiary: topic with general vertical for broader reach.
        queries.Add(new SearchPlanQuery
        {
            Query     = $"{topic} news today",
            Freshness = freshness,
            Vertical  = "general"
        });

        // Optional geo-scoped variant.
        if (!string.IsNullOrWhiteSpace(geo))
        {
            queries.Add(new SearchPlanQuery
            {
                Query     = $"{topic} {geo} news",
                Freshness = freshness,
                Vertical  = "news"
            });
        }
        else
        {
            // Additional angle: topic + "latest headlines"
            queries.Add(new SearchPlanQuery
            {
                Query     = $"{topic} latest headlines",
                Freshness = freshness,
                Vertical  = "general"
            });
        }

        return queries;
    }

    /// <summary>
    /// Fallback generic news queries for intents that don't have
    /// specific templates.
    /// </summary>
    private static IReadOnlyList<SearchPlanQuery> BuildGenericNewsQueries(
        BuildSearchPlanRequest request)
    {
        var freshness = ResolveDefaultFreshness(request.Recency);
        var userTopic = ExtractFallbackTopic(request.UserMessage);

        return
        [
            new SearchPlanQuery { Query = $"{userTopic} latest news",   Freshness = freshness, Vertical = "news" },
            new SearchPlanQuery { Query = $"{userTopic} news today",    Freshness = freshness, Vertical = "general" },
            new SearchPlanQuery { Query = $"{userTopic} news coverage", Freshness = freshness, Vertical = "news" }
        ];
    }

    /// <summary>
    /// Builds a safe deterministic fallback when the original plan
    /// fails validation.
    /// </summary>
    private static IReadOnlyList<SearchPlanQuery> BuildFallbackQueries(
        BuildSearchPlanRequest request)
    {
        var freshness = ResolveDefaultFreshness(request.Recency);

        if (request.Intent == SearchIntent.TopicNews &&
            !string.IsNullOrWhiteSpace(request.TopicAnchor))
        {
            var anchor = SanitizeForQuery(request.TopicAnchor!);
            return
            [
                new SearchPlanQuery { Query = $"{anchor} latest news",   Freshness = freshness, Vertical = "news" },
                new SearchPlanQuery { Query = $"{anchor} news today",    Freshness = freshness, Vertical = "general" },
                new SearchPlanQuery { Query = $"{anchor} news coverage", Freshness = freshness, Vertical = "news" }
            ];
        }

        return
        [
            new SearchPlanQuery { Query = "top headlines today",  Freshness = freshness, Vertical = "news" },
            new SearchPlanQuery { Query = "breaking news today",  Freshness = freshness, Vertical = "news" },
            new SearchPlanQuery { Query = "latest world news",    Freshness = freshness, Vertical = "general" }
        ];
    }

    // ─────────────────────────────────────────────────────────────────
    // Plan Validation — Hard-Stop Checks
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates a search plan against hard-stop criteria. Returns
    /// <see cref="PlanValidationCodes.Valid"/> when all checks pass.
    /// </summary>
    internal static string Validate(BuildSearchPlanResult plan, BuildSearchPlanRequest request)
    {
        // ── 1. Max queries ───────────────────────────────────────────
        if (plan.Queries.Count > MaxQueriesPerPlan)
            return PlanValidationCodes.ExceedsMaxQueries;

        // ── 2. Blocked patterns ──────────────────────────────────────
        foreach (var query in plan.Queries)
        {
            var lower = query.Query.ToLowerInvariant();
            foreach (var blocked in BlockedPatterns)
            {
                if (lower.Contains(blocked, StringComparison.Ordinal))
                    return PlanValidationCodes.ContainsBlockedPattern;
            }
        }

        // ── 3. Freshness on news queries ─────────────────────────────
        // Every query in a news plan must have freshness set.
        if (request.Intent is SearchIntent.NewsHeadlines or SearchIntent.TopicNews)
        {
            foreach (var query in plan.Queries)
            {
                if (string.IsNullOrWhiteSpace(query.Freshness) ||
                    query.Freshness.Equals("any", StringComparison.OrdinalIgnoreCase))
                    return PlanValidationCodes.MissingFreshness;
            }
        }

        // ── 4. Topic drift (TOPIC_NEWS only) ─────────────────────────
        if (request.Intent == SearchIntent.TopicNews &&
            !string.IsNullOrWhiteSpace(request.TopicAnchor))
        {
            var anchorLower = request.TopicAnchor!.ToLowerInvariant();
            var anchorTokens = anchorLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var query in plan.Queries)
            {
                var queryLower = query.Query.ToLowerInvariant();

                // At least one anchor token must appear in the query to
                // prevent drift to unrelated topics.
                if (!anchorTokens.Any(token => queryLower.Contains(token, StringComparison.Ordinal)))
                    return PlanValidationCodes.TopicDrift;
            }
        }

        // ── 5. News vertical presence ────────────────────────────────
        // At least one query in a news plan must target the news vertical.
        if (request.Intent is SearchIntent.NewsHeadlines or SearchIntent.TopicNews)
        {
            var hasNewsVertical = plan.Queries.Any(q =>
                q.Vertical.Equals("news", StringComparison.OrdinalIgnoreCase));

            if (!hasNewsVertical)
                return PlanValidationCodes.MissingNewsVertical;
        }

        return PlanValidationCodes.Valid;
    }

    // ─────────────────────────────────────────────────────────────────
    // Broadening — Retry variant generation (same-intent only)
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a broadened plan for retry. Only broadens within the
    /// same intent/topic constraints — never drifts to biography,
    /// reference, or help content.
    /// </summary>
    public static BuildSearchPlanResult BroadenForRetry(
        BuildSearchPlanResult originalPlan,
        BuildSearchPlanRequest request,
        int iteration)
    {
        var queries = new List<SearchPlanQuery>(originalPlan.Queries);
        var freshness = ResolveRetryFreshness(request.Recency, iteration);

        if (request.Intent == SearchIntent.TopicNews &&
            !string.IsNullOrWhiteSpace(request.TopicAnchor))
        {
            var anchor = request.TopicAnchor!;

            // Add a broader-recency variant of the topic query.
            queries.Add(new SearchPlanQuery
            {
                Query     = $"{anchor} news",
                Freshness = freshness,
                Vertical  = "general"
            });
        }
        else
        {
            // Headline broadening: add different angle queries.
            if (iteration == 2)
            {
                queries.Add(new SearchPlanQuery
                {
                    Query     = "today's top news stories",
                    Freshness = freshness,
                    Vertical  = "general"
                });
            }
            else
            {
                queries.Add(new SearchPlanQuery
                {
                    Query     = "this week's biggest news",
                    Freshness = "week",
                    Vertical  = "general"
                });
            }
        }

        // Trim to max queries.
        if (queries.Count > MaxQueriesPerPlan)
            queries = queries.Take(MaxQueriesPerPlan).ToList();

        var broadened = originalPlan with
        {
            Queries       = queries,
            MaxIterations = originalPlan.MaxIterations // preserve original budget
        };

        // Re-validate the broadened plan.
        var validationCode = Validate(broadened, request);
        if (validationCode != PlanValidationCodes.Valid)
        {
            return originalPlan with
            {
                ValidationFailure = validationCode
            };
        }

        return broadened;
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the default freshness for a news plan based on the
    /// recency hint from routing.
    /// </summary>
    private static string ResolveDefaultFreshness(string recency)
    {
        return (recency ?? "day").ToLowerInvariant() switch
        {
            "day"   => "day",
            "24h"   => "day",
            "week"  => "week",
            "month" => "month",
            _       => "day"  // News queries default to day freshness.
        };
    }

    /// <summary>
    /// Relaxes freshness on retry iterations to cast a wider net
    /// while staying within the same intent.
    /// </summary>
    private static string ResolveRetryFreshness(string originalRecency, int iteration)
    {
        return iteration switch
        {
            2 => (originalRecency ?? "day").ToLowerInvariant() switch
            {
                "day" => "week",
                _     => "month"
            },
            _ => "month"
        };
    }

    /// <summary>
    /// Extracts a rough topic from the user message when no explicit
    /// topic anchor is provided.
    /// </summary>
    private static string ExtractFallbackTopic(string userMessage)
    {
        var topic = QueryBuilder.ExtractTopicFromMessage(userMessage);
        return !string.IsNullOrWhiteSpace(topic)
            ? SanitizeForQuery(topic)
            : "news";
    }

    /// <summary>
    /// Strips blocked patterns and truncates for query safety.
    /// </summary>
    private static string SanitizeForQuery(string input)
    {
        var result = input.Trim();

        // Strip any blocked patterns that may have leaked in.
        foreach (var blocked in BlockedPatterns)
        {
            result = result.Replace(blocked, "", StringComparison.OrdinalIgnoreCase).Trim();
        }

        // Cap at 50 chars for query-level safety.
        if (result.Length > 50)
            result = result[..50].Trim();

        return string.IsNullOrWhiteSpace(result) ? "news" : result;
    }
}
