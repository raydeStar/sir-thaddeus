using SirThaddeus.Agent;
using SirThaddeus.Harness.Execution;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Tracing;

namespace SirThaddeus.Harness.Scoring;

public sealed class ScoringEngine
{
    private sealed record SoftScoreBreakdown
    {
        public double Total { get; init; }
        public double KeywordPenalty { get; init; }
        public int RequiredKeywordsFound { get; init; }
        public int RequiredKeywordsTotal { get; init; }
        public double DeflectionPenalty { get; init; }
        public int DeflectionPhraseCount { get; init; }
        public double ToolIncorporationPenalty { get; init; }
        public int ToolTokensIncorporated { get; init; }
        public int ToolTokensAvailable { get; init; }
        public double AssertionDensityPenalty { get; init; }
        public double HedgeRatio { get; init; }
        public double PersonalityAdjustment { get; init; }
    }

    public ScoreCard Score(
        HarnessTestCase test,
        AgentResponse response,
        IReadOnlyList<TraceStep> steps,
        CursorJudgeResult? judgeResult)
    {
        var hardFailures = EvaluateHardAssertions(test, response, steps);
        var hardPass = hardFailures.Count == 0;
        var breakdown = EvaluateSoftScoreDetailed(test, response, steps);
        var judgeScore = judgeResult?.Score;

        // Judge is the primary signal when available
        var merged = judgeScore is null
            ? breakdown.Total
            : ((breakdown.Total * 0.3) + (judgeScore.Value * 0.7));

        var finalScore = hardPass ? Math.Round(merged, 2) : 0.0;
        return new ScoreCard
        {
            HardPass = hardPass,
            HardFailures = hardFailures,
            SoftScore = Math.Round(breakdown.Total, 2),
            JudgeScore = judgeScore is null ? null : Math.Round(judgeScore.Value, 2),
            FinalScore = finalScore,
            JudgeReasons = judgeResult?.Reasons ?? [],
            JudgeSuggestions = judgeResult?.Suggestions ?? [],
            KeywordPenalty = Math.Round(breakdown.KeywordPenalty, 2),
            DeflectionPenalty = Math.Round(breakdown.DeflectionPenalty, 2),
            ToolIncorporationPenalty = Math.Round(breakdown.ToolIncorporationPenalty, 2),
            AssertionDensityPenalty = Math.Round(breakdown.AssertionDensityPenalty, 2),
            PersonalityAdjustment = Math.Round(breakdown.PersonalityAdjustment, 2),
            DeflectionPhraseCount = breakdown.DeflectionPhraseCount,
            HedgeRatio = Math.Round(breakdown.HedgeRatio, 2),
            ToolTokensIncorporated = breakdown.ToolTokensIncorporated,
            ToolTokensAvailable = breakdown.ToolTokensAvailable,
            RequiredKeywordsFound = breakdown.RequiredKeywordsFound,
            RequiredKeywordsTotal = breakdown.RequiredKeywordsTotal
        };
    }

    private static IReadOnlyList<string> EvaluateHardAssertions(
        HarnessTestCase test,
        AgentResponse response,
        IReadOnlyList<TraceStep> steps)
    {
        var failures = new List<string>();
        var actualTools = response.ToolCallsMade.Select(call => call.ToolName).ToList();
        var allowedTools = new HashSet<string>(
            test.AllowedTools.Select(NormalizeToolName),
            StringComparer.OrdinalIgnoreCase);
        var actualNormalized = actualTools.Select(NormalizeToolName).ToList();

        if (string.IsNullOrWhiteSpace(response.Text))
            failures.Add("Final response text is missing.");

        if (test.Assertions.AllowedToolsOnly && allowedTools.Count > 0)
        {
            var disallowed = actualTools
                .Where(name => !allowedTools.Contains(NormalizeToolName(name)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (disallowed.Count > 0)
                failures.Add($"Disallowed tools used: {string.Join(", ", disallowed)}");
        }

        foreach (var required in test.Assertions.RequiredTools)
        {
            if (!actualNormalized.Contains(NormalizeToolName(required), StringComparer.OrdinalIgnoreCase))
                failures.Add($"Required tool not called: {required}");
        }

        foreach (var forbidden in test.Assertions.ForbiddenTools)
        {
            if (actualNormalized.Contains(NormalizeToolName(forbidden), StringComparer.OrdinalIgnoreCase))
                failures.Add($"Forbidden tool was called: {forbidden}");
        }

        if (test.Assertions.RequireStructuredErrors)
        {
            var badErrorPayloads = steps
                .Where(step => string.Equals(step.StepType, "tool_result", StringComparison.OrdinalIgnoreCase))
                .Where(step => step.Error is not null && !ToolResultPayloads.LooksLikeStructuredError(step.Result ?? ""))
                .ToList();

            if (badErrorPayloads.Count > 0)
                failures.Add("Tool failures must use structured error JSON payloads.");
        }

        if (test.Assertions.RequireNoHallucinatedCitations)
        {
            var responseText = response.Text ?? "";
            if (responseText.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                responseText.Contains("https://", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("Final response contains URL citations; citation hygiene assertion failed.");
            }
        }

        // Infrastructure / configuration error detection — the agent surfaced
        // an internal setup problem to the user instead of answering.
        var isStubMode = string.Equals(test.Mode, "stub", StringComparison.OrdinalIgnoreCase);
        if (test.Assertions.ForbidInfrastructureErrors && !isStubMode)
        {
            var infraMatch = DetectInfrastructureErrorResponse(response.Text ?? "");
            if (infraMatch is not null)
                failures.Add($"Response is an infrastructure/configuration error, not a real answer: {infraMatch}");
        }

        if (!isStubMode)
        {
            var localBusinessFallbackMatch = DetectLocalBusinessFallbackNonAnswer(response.Text ?? "");
            if (localBusinessFallbackMatch is not null)
                failures.Add($"Response is a local-business fallback non-answer, not a grounded recommendation: {localBusinessFallbackMatch}");

            var webGroundingFallbackMatch = DetectWebGroundingNonAnswer(test, response.Text ?? "");
            if (webGroundingFallbackMatch is not null)
                failures.Add($"Response is a web-grounding fallback non-answer, not a grounded web answer: {webGroundingFallbackMatch}");
        }

        return failures;
    }

    /// <summary>
    /// Returns the first matching infrastructure-error phrase found in the
    /// response, or null if the response looks like genuine content.
    /// </summary>
    private static string? DetectInfrastructureErrorResponse(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return null;

        var lower = responseText.ToLowerInvariant();

        // Phrase families that indicate the agent dumped a config/infra error
        // to the user instead of providing an actual answer.
        ReadOnlySpan<string> infraPatterns =
        [
            "missing an api key",
            "missing api key",
            "missing a key",
            "not fully configured",
            "not configured",
            "is not configured",
            "provider is disabled",
            "provider is missing",
            "api key is not set",
            "api key not set",
            "set st_",                         // env-var setup instructions
            "set google_maps_api_key",
            "set your api key",
            "could not retrieve live results",
            "api.*unavailable",
        ];

        foreach (var pattern in infraPatterns)
        {
            if (pattern.Contains('*'))
            {
                // Treat as simple glob: split on * and check ordered containment
                var parts = pattern.Split('*');
                var idx = 0;
                var allFound = true;
                foreach (var part in parts)
                {
                    var pos = lower.IndexOf(part, idx, StringComparison.Ordinal);
                    if (pos < 0) { allFound = false; break; }
                    idx = pos + part.Length;
                }
                if (allFound) return pattern;
            }
            else
            {
                if (lower.Contains(pattern, StringComparison.Ordinal))
                    return pattern;
            }
        }

        return null;
    }

    /// <summary>
    /// Detects deterministic local-business fallback templates that do not
    /// provide an actual recommendation. These should hard-fail.
    /// </summary>
    private static string? DetectLocalBusinessFallbackNonAnswer(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return null;

        var lower = responseText.ToLowerInvariant();
        ReadOnlySpan<string> nonAnswerPatterns =
        [
            "could not retrieve live local business results",
            "try naming one specific place",
            "i can check its current hours",
            "directory-style local results rather than single verified storefront pages",
            "give me a neighborhood or major street"
        ];

        foreach (var pattern in nonAnswerPatterns)
        {
            if (lower.Contains(pattern, StringComparison.Ordinal))
                return pattern;
        }

        return null;
    }

    private static string? DetectWebGroundingNonAnswer(
        HarnessTestCase test,
        string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return null;

        var lower = responseText.ToLowerInvariant();
        var usesWebOrPlaces = test.AllowedTools
            .Select(NormalizeToolName)
            .Any(tool => tool is "websearch" or "browsernavigate" or "placeslookup");
        if (!usesWebOrPlaces)
            return null;

        ReadOnlySpan<string> nonAnswerPatterns =
        [
            "fallback search came back with 0 results",
            "i couldn't verify live hours, reviews, or contact details",
            "hours were not found in available sources",
            "current open status is unknown from the available sources",
            "directory-style local results rather than single verified storefront pages",
            "try a more specific business name",
            "search providers are responding"
        ];

        foreach (var pattern in nonAnswerPatterns)
        {
            if (lower.Contains(pattern, StringComparison.Ordinal))
                return pattern;
        }

        return null;
    }

    private static string NormalizeToolName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var chars = value
            .Trim()
            .Where(ch => ch != '_' && ch != '-' && !char.IsWhiteSpace(ch))
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }

    private static SoftScoreBreakdown EvaluateSoftScoreDetailed(
        HarnessTestCase test,
        AgentResponse response,
        IReadOnlyList<TraceStep> steps)
    {
        var score = 10.0;
        var final = response.Text ?? "";
        var finalLower = final.ToLowerInvariant();

        // --- Keyword scoring ---
        var keywordPenalty = 0.0;
        var reqKeywordsFound = 0;
        var reqKeywordsTotal = 0;

        if (test.Expectations.RequiredKeywords.Count > 0)
        {
            var required = test.Expectations.RequiredKeywords
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .ToList();
            reqKeywordsTotal = required.Count;
            reqKeywordsFound = required.Count(keyword => finalLower.Contains(keyword.ToLowerInvariant()));
            var coverage = required.Count == 0 ? 1.0 : (double)reqKeywordsFound / required.Count;
            keywordPenalty = -((1.0 - coverage) * 5.0);
            score += keywordPenalty;
        }

        if (test.Expectations.ForbiddenKeywords.Count > 0)
        {
            var forbiddenHits = test.Expectations.ForbiddenKeywords
                .Count(keyword => !string.IsNullOrWhiteSpace(keyword) && finalLower.Contains(keyword.ToLowerInvariant()));
            var forbiddenPenalty = -(forbiddenHits * 1.5);
            keywordPenalty += forbiddenPenalty;
            score += forbiddenPenalty;
        }

        if (test.Expectations.MaxResponseChars is { } maxChars &&
            final.Length > maxChars)
        {
            score -= 1.0;
        }

        // --- Semantic quality checks ---
        var toolResultCount = steps.Count(step =>
            string.Equals(step.StepType, "tool_result", StringComparison.OrdinalIgnoreCase));

        // Did the agent deflect instead of answering?
        var (deflectionPenalty, deflectionPhraseCount) = ComputeDeflectionPenalty(final, toolResultCount);
        score += deflectionPenalty;

        // Did the agent use the tool results it gathered?
        var (toolIncorpPenalty, tokensIncorporated, tokensAvailable) = ComputeToolResultIncorporation(steps, final);
        score += toolIncorpPenalty;

        // Is the response all hedging and no substance?
        var (assertionPenalty, hedgeRatio) = ComputeAssertionDensity(final);
        score += assertionPenalty;

        // Existing check: tools called but response is a stub
        if (toolResultCount > 0 && final.Length < 40)
            score -= 1.5;

        // Existing check: "As an AI" cop-out phrasing
        if (final.Contains("As an AI", StringComparison.OrdinalIgnoreCase))
            score -= 0.5;

        if (LooksLikeGracefulLiveWebOutage(test, response, steps))
            score = Math.Max(score, 9.0);

        // Personality-specific scoring dimensions
        var personalityAdj = PersonalityScoringHeuristics.ComputeAdjustment(
            test.Expectations,
            final);
        score += personalityAdj;

        return new SoftScoreBreakdown
        {
            Total = Math.Clamp(score, 0, 10),
            KeywordPenalty = keywordPenalty,
            RequiredKeywordsFound = reqKeywordsFound,
            RequiredKeywordsTotal = reqKeywordsTotal,
            DeflectionPenalty = deflectionPenalty,
            DeflectionPhraseCount = deflectionPhraseCount,
            ToolIncorporationPenalty = toolIncorpPenalty,
            ToolTokensIncorporated = tokensIncorporated,
            ToolTokensAvailable = tokensAvailable,
            AssertionDensityPenalty = assertionPenalty,
            HedgeRatio = hedgeRatio,
            PersonalityAdjustment = personalityAdj
        };
    }

    private static bool LooksLikeGracefulLiveWebOutage(
        HarnessTestCase test,
        AgentResponse response,
        IReadOnlyList<TraceStep> steps)
    {
        var requiresWeb = test.Assertions.RequiredTools
            .Any(tool => NormalizeToolName(tool) == NormalizeToolName("web_search"));
        if (!requiresWeb)
            return false;

        var final = response.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(final))
            return false;

        var finalLower = final.ToLowerInvariant();
        var hasGracefulOutageMessage =
            finalLower.Contains("live web lookup is unavailable", StringComparison.Ordinal) ||
            finalLower.Contains("cannot verify live web facts", StringComparison.Ordinal) ||
            finalLower.Contains("web search returned no results", StringComparison.Ordinal);
        if (!hasGracefulOutageMessage)
            return false;

        var webSearchResults = steps
            .Where(step => string.Equals(step.StepType, "tool_result", StringComparison.OrdinalIgnoreCase))
            .Where(step => string.Equals(NormalizeToolName(step.ToolName ?? string.Empty), NormalizeToolName("web_search"), StringComparison.OrdinalIgnoreCase))
            .Select(step => step.Result ?? string.Empty)
            .ToList();
        if (webSearchResults.Count == 0)
            return false;

        return webSearchResults.All(result =>
            result.Contains("[Search: 0 results", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("\"message\":\"Cancelled\"", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("\"message\": \"Cancelled\"", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("tool_unavailable", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Detects apologetic non-answers. If the agent called tools and STILL deflected,
    /// the penalty is even harsher — it wasted compute and gave up anyway.
    /// </summary>
    private static (double Penalty, int PhraseCount) ComputeDeflectionPenalty(string responseText, int toolResultCount)
    {
        var lower = responseText.ToLowerInvariant();

        var deflectionPatterns = new[]
        {
            "i cannot verify",
            "could not verify",
            "i'm unable to",
            "i don't have access",
            "web search returned no results",
            "may be incomplete or out of date",
            "i cannot provide a definitive",
            "i'm not able to confirm",
            "unable to search",
            "cannot confirm or deny",
            "i don't have real-time",
            "my knowledge cutoff",
            "unavailable right now",
            "i cannot browse",
            "i can't access",
            "live web lookup is unavailable",
            "best-effort answer from built-in reasoning",
            "i cannot search the web",
            "i do not have the ability"
        };

        var deflectionHits = deflectionPatterns.Count(p => lower.Contains(p));

        if (deflectionHits == 0)
            return (0.0, 0);

        // Base penalty scales with how many deflection phrases appear
        var basePenalty = Math.Min(deflectionHits * 2.0, 6.0);

        // If tools were called AND the response is still a deflection,
        // that's worse — the agent tried and gave up without synthesizing
        var toolWastePenalty = toolResultCount > 0 ? 1.5 : 0.0;

        return (-(basePenalty + toolWastePenalty), deflectionHits);
    }

    /// <summary>
    /// Measures whether tool results were actually incorporated into the final response.
    /// An agent that gathers information and then discards it is worse than one that
    /// never gathered it at all.
    /// </summary>
    private static (double Penalty, int Incorporated, int Available) ComputeToolResultIncorporation(
        IReadOnlyList<TraceStep> steps,
        string responseText)
    {
        var toolResults = steps
            .Where(s => string.Equals(s.StepType, "tool_result", StringComparison.OrdinalIgnoreCase))
            .Where(s => !string.IsNullOrWhiteSpace(s.Result))
            .Where(s => s.Error is null)  // Only count successful results
            .Where(s => !LooksLikeToolErrorOrEmptyResult(s.Result!))
            .ToList();

        if (toolResults.Count == 0)
            return (0.0, 0, 0);

        var significantTokens = toolResults
            .SelectMany(r => ExtractSignificantTokens(r.Result!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (significantTokens.Count == 0)
            return (0.0, 0, 0);

        var responseLower = responseText.ToLowerInvariant();
        var incorporated = significantTokens
            .Count(token => responseLower.Contains(token.ToLowerInvariant()));

        var rate = (double)incorporated / significantTokens.Count;

        // Used almost nothing from the tools? Heavy penalty.
        // Used some? Scaled penalty. Used most? No penalty.
        double penalty;
        if (rate < 0.05) penalty = -4.0;
        else if (rate < 0.2) penalty = -2.5;
        else if (rate < 0.4) penalty = -1.0;
        else penalty = 0.0;

        return (penalty, incorporated, significantTokens.Count);
    }

    /// <summary>
    /// Filters out tool results that are clearly error messages or empty-result
    /// indicators even when the trace error field is null. Prevents false
    /// tool-incorporation penalties from error text.
    /// </summary>
    private static bool LooksLikeToolErrorOrEmptyResult(string result)
    {
        // "[Places lookup error: ...]", "[Tool error: ...]", etc.
        if (result.TrimStart().StartsWith('[') &&
            result.Contains("error", StringComparison.OrdinalIgnoreCase))
            return true;

        if (result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            return true;

        // "[search: 0 result(s) returned]"
        if (result.Contains("0 result", StringComparison.OrdinalIgnoreCase))
            return true;

        // Redacted summaries of tools whose canonical response is
        // status/health info (tool_ping, health.check, policy.get_state).
        // A good model summarizes these instead of quoting version
        // numbers verbatim — penalizing that would reward verbose copy-
        // paste over actual comprehension.
        if (result.Contains("protocol_version", StringComparison.OrdinalIgnoreCase) &&
            result.Contains("contract_version", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Empty-scope discovery / geocode results — when the user didn't
        // specify a location, the tool correctly returns empty. The model
        // should ask the user for clarification (which is the whole point
        // of the test) rather than inventing content. Don't penalize that.
        if (result.Contains("\"resolvedLocation\":\"\"", StringComparison.Ordinal) ||
            result.Contains("\"resolvedLocation\": \"\"", StringComparison.Ordinal))
        {
            return true;
        }
        if (result.Contains("\"results\":[]", StringComparison.Ordinal) ||
            result.Contains("\"results\": []", StringComparison.Ordinal))
        {
            return true;
        }

        // Weather-specific: geocode summary that couldn't parse JSON
        // falls back to "[Weather geocode: N chars]". No parseable
        // content to cite.
        if (result.StartsWith("[Weather geocode:", StringComparison.OrdinalIgnoreCase) &&
            result.Contains("chars]", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // document_read often returns an opaque summary like
        // "[Document content: 100 chars, sha256=...]". That does not carry
        // meaningful tokens for incorporation scoring.
        if (result.StartsWith("[Document content:", StringComparison.OrdinalIgnoreCase) ||
            (result.Contains("Document content:", StringComparison.OrdinalIgnoreCase) &&
             result.Contains("sha256=", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Config/infra error text
        if (result.Contains("is not configured", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Extracts tokens from tool results that are likely meaningful —
    /// capitalized words, numbers, proper nouns. Skip stop words and noise.
    /// </summary>
    private static IEnumerable<string> ExtractSignificantTokens(string text)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "that", "this", "with", "from",
            "are", "was", "were", "been", "have", "has", "had",
            "not", "but", "what", "which", "their", "there", "about",
            "would", "could", "should", "will", "can", "may", "might",
            "http", "https", "www", "com", "org", "html", "null", "true", "false"
        };

        return text
            .Split(' ', '\n', '\t', '\r', ',', '.', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}')
            .Where(w => w.Length > 3)
            .Where(w => !stopWords.Contains(w))
            .Where(w => char.IsUpper(w[0]) || w.Any(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(25);
    }

    /// <summary>
    /// Penalizes responses that are overwhelmingly hedged.
    /// Some hedging is appropriate. ALL hedging means the agent has no answer.
    /// </summary>
    private static (double Penalty, double HedgeRatio) ComputeAssertionDensity(string responseText)
    {
        var sentences = responseText.Split('.', '!', '?')
            .Select(s => s.Trim())
            .Where(s => s.Length > 15)
            .ToList();

        if (sentences.Count == 0)
            return (-2.0, 1.0);

        var hedgePatterns = new[]
        {
            "might", "possibly", "perhaps", "i think",
            "it's possible", "could be", "may have", "not sure",
            "i believe", "it seems", "appears to", "likely",
            "if available", "when possible", "depending on",
            "indicate", "available sources indicate"
        };

        var hedgeCount = sentences.Count(s =>
            hedgePatterns.Any(h => s.Contains(h, StringComparison.OrdinalIgnoreCase)));

        var hedgeRatio = (double)hedgeCount / sentences.Count;

        // More than 70% hedging = the response is mush
        double penalty;
        if (hedgeRatio > 0.7) penalty = -3.0;
        else if (hedgeRatio > 0.5) penalty = -1.5;
        else penalty = 0.0;

        return (penalty, hedgeRatio);
    }

    /// <summary>
    /// Flags suspiciously perfect scores. Real AI systems have variance.
    /// A flat 10.0 across the board almost certainly means the tests were gamed.
    /// </summary>
    public static void DetectScoringAnomalies(
        IReadOnlyList<ScoreCard> suiteResults,
        string suiteName)
    {
        if (suiteResults.Count < 3) return;

        var scores = suiteResults.Select(r => r.FinalScore).ToList();
        var mean = scores.Average();
        var variance = scores.Sum(s => Math.Pow(s - mean, 2)) / scores.Count;
        var allNearPerfect = scores.All(s => s >= 9.5);

        if (allNearPerfect && variance < 0.1)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine();
            Console.WriteLine($"  ⚠ ANOMALY [{suiteName}]: All scores ≥9.5 with variance {variance:F3}.");
            Console.WriteLine("    Possible hardcoded responses. Inspect artifacts for derivation traces.");
            Console.ResetColor();
        }
    }
}
