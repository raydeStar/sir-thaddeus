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