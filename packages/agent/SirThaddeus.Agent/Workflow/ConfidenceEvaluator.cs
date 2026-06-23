namespace SirThaddeus.Agent.Workflow;

public sealed class ConfidenceEvaluator : IConfidenceEvaluator
{
    public ConfidenceSnapshot Evaluate(TaskRunState state)
    {
        var evidence = state.Evidence;
        var avgTrust = evidence.Count == 0 ? 0.45 : evidence.Average(e => Clamp01(e.TrustScore));
        var avgRelevance = evidence.Count == 0 ? 0.45 : evidence.Average(e => Clamp01(e.RelevanceScore));
        var hasStrongToolEvidence = evidence.Any(e =>
            !string.Equals(e.Title, "llm_response", StringComparison.OrdinalIgnoreCase) &&
            e.SupportsCandidateAnswer &&
            e.TrustScore >= 0.60);

        var supports = evidence.Count(e => e.SupportsCandidateAnswer);
        var contradicts = evidence.Count(e => e.ContradictsCandidateAnswer);
        var agreement = evidence.Count == 0 ? 0.45 : Clamp01((supports - contradicts + evidence.Count) / (2.0 * evidence.Count));

        var hasAnswer = !string.IsNullOrWhiteSpace(state.DraftAnswer);
        var hasFailurePlaceholderAnswer = LooksLikeFailurePlaceholder(state.DraftAnswer);
        var coverage = hasAnswer ? 0.9 : 0.3;
        var contradictionPenalty = evidence.Count == 0 ? 0 : Math.Min(0.35, contradicts * 0.12);

        var score = (avgTrust * 0.35) +
                    (agreement * 0.30) +
                    (avgRelevance * 0.20) +
                    (coverage * 0.15) -
                    contradictionPenalty;

        if (state.Envelope.Complexity == TaskComplexity.MultiStepResearch && !hasStrongToolEvidence)
        {
            score = Math.Min(score, 0.58);
        }

        if (hasFailurePlaceholderAnswer)
        {
            score = Math.Min(score, 0.20);
        }

        score = Clamp01(score);

        var band = score switch
        {
            >= 0.85 => "High",
            >= 0.65 => "Medium",
            >= 0.40 => "Low",
            _ => "VeryLow"
        };

        var shouldRetry = band is "Low" or "VeryLow";
        var summary = band switch
        {
            "High" => "Strong supporting evidence with low contradiction.",
            "Medium" => "Reasonable support, but some uncertainty remains.",
            "Low" => "Limited support; better evidence is recommended.",
            _ => "Insufficient evidence to answer confidently."
        };

        return new ConfidenceSnapshot
        {
            Score = score,
            Band = band,
            Summary = summary,
            ShouldRetry = shouldRetry,
            RetryReason = shouldRetry ? "Confidence below threshold" : null
        };
    }

    private static double Clamp01(double value)
        => Math.Max(0.0, Math.Min(1.0, value));

    private static bool LooksLikeFailurePlaceholder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var trimmed = text.Trim();
        var normalized = trimmed.TrimEnd('.', '!', '?');

        return normalized.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Canceled", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("(The model returned an empty response.)", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("couldn't finish the answer cleanly", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("event stream ended before completion", StringComparison.OrdinalIgnoreCase);
    }
}