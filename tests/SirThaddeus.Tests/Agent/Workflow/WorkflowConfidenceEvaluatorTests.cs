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
}