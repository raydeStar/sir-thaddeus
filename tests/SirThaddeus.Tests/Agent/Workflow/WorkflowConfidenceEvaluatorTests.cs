using SirThaddeus.Agent.Workflow;

namespace SirThaddeus.Tests;

public sealed class WorkflowConfidenceEvaluatorTests
{
    [Fact]
    public void MultiStepResearch_WithoutStrongToolEvidence_ProducesLowConfidenceAndRetry()
    {
        var evaluator = new ConfidenceEvaluator();
        var state = new TaskRunState
        {
            Envelope = new TaskEnvelope
            {
                UserRequest = "Research a complex pricing policy",
                Complexity = TaskComplexity.MultiStepResearch
            },
            DraftAnswer = "Tentative answer"
        };

        state.Evidence.Add(new EvidenceRecord
        {
            SourceType = "primary",
            Title = "llm_response",
            Summary = "Model-only answer",
            TrustScore = 0.45,
            RelevanceScore = 0.60,
            SupportsCandidateAnswer = true,
            ContradictsCandidateAnswer = false
        });

        var snapshot = evaluator.Evaluate(state);

        Assert.True(snapshot.Score < 0.65);
        Assert.Equal("Low", snapshot.Band);
        Assert.True(snapshot.ShouldRetry);
    }

    [Fact]
    public void MultiStepResearch_WithStrongToolEvidence_ProducesMediumOrHigherConfidence()
    {
        var evaluator = new ConfidenceEvaluator();
        var state = new TaskRunState
        {
            Envelope = new TaskEnvelope
            {
                UserRequest = "Research a complex pricing policy",
                Complexity = TaskComplexity.MultiStepResearch
            },
            DraftAnswer = "Supported answer"
        };

        state.Evidence.Add(new EvidenceRecord
        {
            SourceType = "primary",
            Title = "web_search",
            Summary = "Official docs found",
            TrustScore = 0.85,
            RelevanceScore = 0.80,
            SupportsCandidateAnswer = true,
            ContradictsCandidateAnswer = false
        });

        var snapshot = evaluator.Evaluate(state);

        Assert.True(snapshot.Score >= 0.65);
        Assert.Contains(snapshot.Band, new[] { "Medium", "High" });
    }

    [Fact]
    public void RetryJudgedOnOwnEvidence_IsNotPenalizedByPriorAttemptFailures()
    {
        // Regression guard for the first-vs-retry selection in
        // WorkflowChatRunCoordinator. A retry that recovers from a tool
        // error (failed call -> fixed call -> correct answer) has the same
        // evidence shape as the first attempt. Judged on its OWN evidence
        // it must tie the first attempt (so >= selection keeps the retry).
        // Judged on the ACCUMULATED first+retry evidence (the old bug), the
        // first attempt's failed call is charged a second time as a
        // contradiction, so the retry can never win and its correct answer
        // is discarded.
        var evaluator = new ConfidenceEvaluator();

        EvidenceRecord Success() => new()
        {
            SourceType = "primary",
            Title = "python_eval",
            Summary = "Tool succeeded",
            TrustScore = 0.70,
            RelevanceScore = 0.62,
            SupportsCandidateAnswer = true,
            ContradictsCandidateAnswer = false
        };
        EvidenceRecord Failure() => new()
        {
            SourceType = "primary",
            Title = "python_eval",
            Summary = "Tool call failed",
            TrustScore = 0.28,
            RelevanceScore = 0.62,
            SupportsCandidateAnswer = false,
            ContradictsCandidateAnswer = true
        };

        TaskRunState StateWith(params EvidenceRecord[] evidence)
        {
            var state = new TaskRunState
            {
                Envelope = new TaskEnvelope { UserRequest = "compute", Complexity = TaskComplexity.SimpleLookup },
                DraftAnswer = "111"
            };
            state.Evidence.AddRange(evidence);
            return state;
        }

        // First attempt: one failed call, then a successful compute.
        var firstScore = evaluator.Evaluate(StateWith(Failure(), Success())).Score;
        // Retry judged on its OWN evidence (same shape): must tie the first.
        var retryOwnScore = evaluator.Evaluate(StateWith(Failure(), Success())).Score;
        // Retry judged on ACCUMULATED evidence (the old bug): first attempt's
        // failure double-counts against it.
        var retryCumulativeScore = evaluator.Evaluate(
            StateWith(Failure(), Success(), Failure(), Success())).Score;

        Assert.Equal(firstScore, retryOwnScore, precision: 6);
        Assert.True(
            retryCumulativeScore < firstScore,
            $"Accumulated evidence should sink the retry below the first attempt " +
            $"(cumulative={retryCumulativeScore:0.000}, first={firstScore:0.000}).");
    }

    [Fact]
    public void BareCancelledAnswer_WithStrongToolEvidence_RemainsVeryLowConfidence()
    {
        var evaluator = new ConfidenceEvaluator();
        var state = new TaskRunState
        {
            Envelope = new TaskEnvelope
            {
                UserRequest = "What would be the plot of Episode 2 of Season 7 of Meridian Drift about?",
                Complexity = TaskComplexity.MultiStepResearch
            },
            DraftAnswer = "Cancelled"
        };

        state.Evidence.Add(new EvidenceRecord
        {
            SourceType = "retry",
            Title = "web_search",
            Summary = "Returned relevant release/cancellation evidence.",
            TrustScore = 0.85,
            RelevanceScore = 0.80,
            SupportsCandidateAnswer = true,
            ContradictsCandidateAnswer = false
        });

        var snapshot = evaluator.Evaluate(state);

        Assert.True(snapshot.Score <= 0.20, $"Expected failure placeholder answers to stay capped low, but score was {snapshot.Score:0.000}.");
        Assert.Equal("VeryLow", snapshot.Band);
        Assert.True(snapshot.ShouldRetry);
    }
}