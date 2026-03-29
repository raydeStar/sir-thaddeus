namespace SirThaddeus.Agent.Search;

// ─────────────────────────────────────────────────────────────────────────
// Search Retry Arbiter — Seam 7 Implementation (shouldRetry)
//
// Deterministic stop/retry decision for the news search pipeline.
// Evaluates coverage confidence, unique story count, quality floor,
// and iteration budget to decide whether to retry with a broadened plan.
//
// Reason codes are always attached for observability — "why did we stop?"
// and "why did we retry?" are first-class audit signals.
//
// Retries only broaden within the same intent/topic constraints.
// The planner's BroadenForRetry handles the actual query adjustment.
// ─────────────────────────────────────────────────────────────────────────

public static class SearchRetryArbiter
{
    // ── Thresholds ──────────────────────────────────────────────────
    private const double CoverageConfidenceFloor    = 0.4;
    private const int    MinUniqueStoriesForHeadlines = 3;
    private const int    MinUniqueStoriesForTopic     = 2;
    private const double QualityFloor                = 0.3;

    // ─────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates whether the search pipeline should retry with a
    /// broadened plan, or stop and use current results.
    /// </summary>
    public static RetryDecision Evaluate(ShouldRetryRequest request)
    {
        var plan   = request.Plan;
        var scored = request.Scored;
        var agg    = request.Aggregate;
        var trace  = request.Trace;

        var currentIteration = trace?.Iteration ?? 1;

        // ── Hard stop: max iterations reached ────────────────────────
        if (currentIteration >= plan.MaxIterations)
        {
            return new RetryDecision
            {
                ShouldRetry  = false,
                ReasonCode   = RetryReasons.MaxIterationsHit,
                AdjustedPlan = null
            };
        }

        // ── Check: sufficient coverage (also verifies quality floor) ─
        if (agg is not null && !agg.IsLowConfidence)
        {
            var storyCount = agg.Stories.Count;
            var minStories = plan.Intent switch
            {
                SearchIntent.NewsHeadlines => MinUniqueStoriesForHeadlines,
                SearchIntent.TopicNews     => MinUniqueStoriesForTopic,
                _                          => MinUniqueStoriesForTopic
            };

            // "Sufficient" requires adequate coverage AND quality.
            var retainedForCheck = scored.Retained;
            var avgQuality = retainedForCheck.Count > 0
                ? retainedForCheck.Average(r => r.CompositeScore)
                : 0.0;

            if (storyCount >= minStories &&
                agg.CoverageConfidence >= CoverageConfidenceFloor &&
                avgQuality >= QualityFloor)
            {
                return new RetryDecision
                {
                    ShouldRetry  = false,
                    ReasonCode   = RetryReasons.Sufficient,
                    AdjustedPlan = null
                };
            }
        }

        // ── Check: too few unique stories ───────────────────────────
        var uniqueStoryCount = agg?.Stories.Count ?? 0;
        var minRequired = plan.Intent switch
        {
            SearchIntent.NewsHeadlines => MinUniqueStoriesForHeadlines,
            SearchIntent.TopicNews     => MinUniqueStoriesForTopic,
            _                          => MinUniqueStoriesForTopic
        };

        if (uniqueStoryCount < minRequired)
        {
            var broadened = NewsSearchPlanner.BroadenForRetry(
                plan,
                new BuildSearchPlanRequest
                {
                    Intent      = plan.Intent,
                    UserMessage = "", // Original user message not needed for broadening
                    TopicAnchor = null, // Preserved from original plan's queries
                    Recency     = InferRecencyFromPlan(plan)
                },
                currentIteration + 1);

            return new RetryDecision
            {
                ShouldRetry  = true,
                ReasonCode   = RetryReasons.TooFewUniqueStories,
                AdjustedPlan = broadened
            };
        }

        // ── Check: coverage confidence below floor ──────────────────
        var coverageConfidence = agg?.CoverageConfidence ?? scored.RetrievalConfidence;
        if (coverageConfidence < CoverageConfidenceFloor)
        {
            var broadened = NewsSearchPlanner.BroadenForRetry(
                plan,
                new BuildSearchPlanRequest
                {
                    Intent      = plan.Intent,
                    UserMessage = "",
                    TopicAnchor = null,
                    Recency     = InferRecencyFromPlan(plan)
                },
                currentIteration + 1);

            return new RetryDecision
            {
                ShouldRetry  = true,
                ReasonCode   = RetryReasons.BelowCoverageFloor,
                AdjustedPlan = broadened
            };
        }

        // ── Check: average quality below floor ──────────────────────
        var retained = scored.Retained;
        if (retained.Count > 0)
        {
            var avgQuality = retained.Average(r => r.CompositeScore);
            if (avgQuality < QualityFloor)
            {
                var broadened = NewsSearchPlanner.BroadenForRetry(
                    plan,
                    new BuildSearchPlanRequest
                    {
                        Intent      = plan.Intent,
                        UserMessage = "",
                        TopicAnchor = null,
                        Recency     = InferRecencyFromPlan(plan)
                    },
                    currentIteration + 1);

                return new RetryDecision
                {
                    ShouldRetry  = true,
                    ReasonCode   = RetryReasons.BelowQualityFloor,
                    AdjustedPlan = broadened
                };
            }
        }

        // ── Default: sufficient ─────────────────────────────────────
        return new RetryDecision
        {
            ShouldRetry  = false,
            ReasonCode   = RetryReasons.Sufficient,
            AdjustedPlan = null
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Infers the original recency from the plan's queries for
    /// broadening purposes.
    /// </summary>
    private static string InferRecencyFromPlan(BuildSearchPlanResult plan)
    {
        // Use the freshness of the first query as the baseline.
        var firstQuery = plan.Queries.FirstOrDefault();
        return firstQuery?.Freshness ?? "day";
    }
}
