using SirThaddeus.Agent.Workflow;

namespace SirThaddeus.Tests;

public sealed class WorkflowRetryPlannerTests
{
    [Fact]
    public async Task BuildRetryPlan_FirstRetry_UsesOfficialSourceStrategy()
    {
        var planner = new RetryPlanner();
        var state = new TaskRunState
        {
            Envelope = new TaskEnvelope
            {
                UserRequest = "Find pricing details",
                Complexity = TaskComplexity.MultiStepResearch
            },
            DraftAnswer = "Tentative answer",
            RetriesUsed = 0
        };

        var plan = await planner.BuildRetryPlanAsync(state, CancellationToken.None);

        var action = Assert.Single(plan);
        Assert.Equal("official_source_search", action.RetryStrategy);
        Assert.Contains("official/first-party", action.Instruction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildRetryPlan_AdvancesStrategyWithRetryCount()
    {
        var planner = new RetryPlanner();
        var state = new TaskRunState
        {
            Envelope = new TaskEnvelope
            {
                UserRequest = "Find pricing details",
                Complexity = TaskComplexity.MultiStepResearch
            },
            DraftAnswer = "Tentative answer",
            RetriesUsed = 3
        };

        var plan = await planner.BuildRetryPlanAsync(state, CancellationToken.None);

        var action = Assert.Single(plan);
        Assert.Equal("broader_alternative_keywords", action.RetryStrategy);
        Assert.Contains("alternate query terms", action.Instruction, StringComparison.OrdinalIgnoreCase);
    }
}