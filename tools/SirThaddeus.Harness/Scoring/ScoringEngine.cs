using SirThaddeus.Agent;
using SirThaddeus.Harness.Execution;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Tracing;

namespace SirThaddeus.Harness.Scoring;

public sealed class ScoringEngine
{
    public ScoreCard Score(
        HarnessTestCase test,
        AgentResponse response,
        IReadOnlyList<TraceStep> steps,
        CursorJudgeResult? judgeResult)
    {
        var hardFailures = EvaluateHardAssertions(test, response, steps);
        var hardPass = hardFailures.Count == 0;
        var softScore = EvaluateSoftScore(test, response, steps);
        var judgeScore = judgeResult?.Score;

        var merged = judgeScore is null
            ? softScore
            : ((softScore * 0.6) + (judgeScore.Value * 0.4));

        var finalScore = hardPass ? Math.Round(merged, 2) : 0.0;
        return new ScoreCard
        {
            HardPass = hardPass,
            HardFailures = hardFailures,
            SoftScore = Math.Round(softScore, 2),
            JudgeScore = judgeScore is null ? null : Math.Round(judgeScore.Value, 2),
            FinalScore = finalScore,
            JudgeReasons = judgeResult?.Reasons ?? [],
            JudgeSuggestions = judgeResult?.Suggestions ?? []
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

        return failures;
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

    private static double EvaluateSoftScore(
        HarnessTestCase test,
        AgentResponse response,
        IReadOnlyList<TraceStep> steps)
    {
        var score = 10.0;
        var final = response.Text ?? "";
        var finalLower = final.ToLowerInvariant();

        if (test.Expectations.RequiredKeywords.Count > 0)
        {
            var required = test.Expectations.RequiredKeywords
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .ToList();
            var found = required.Count(keyword => finalLower.Contains(keyword.ToLowerInvariant()));
            var coverage = required.Count == 0 ? 1.0 : (double)found / required.Count;
            score -= (1.0 - coverage) * 5.0;
        }

        if (test.Expectations.ForbiddenKeywords.Count > 0)
        {
            var forbiddenHits = test.Expectations.ForbiddenKeywords
                .Count(keyword => !string.IsNullOrWhiteSpace(keyword) && finalLower.Contains(keyword.ToLowerInvariant()));
            score -= forbiddenHits * 1.5;
        }

        if (test.Expectations.MaxResponseChars is { } maxChars &&
            final.Length > maxChars)
        {
            score -= 1.0;
        }

        var toolResultCount = steps.Count(step => step.StepType == "tool_result");
        if (toolResultCount > 0 && final.Length < 40)
            score -= 1.5;

        if (final.Contains("As an AI", StringComparison.OrdinalIgnoreCase))
            score -= 0.5;

        return Math.Clamp(score, 0, 10);
    }
}
