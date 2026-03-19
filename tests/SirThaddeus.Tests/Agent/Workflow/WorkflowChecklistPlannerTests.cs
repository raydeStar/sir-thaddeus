using SirThaddeus.Agent.Workflow;

namespace SirThaddeus.Tests;

public sealed class WorkflowChecklistPlannerTests
{
    private readonly IChecklistPlanner _planner = new ChecklistPlanner();

    // ── Checklist is intentionally disabled for now ─────────────────────────

    [Fact]
    public async Task SimpleLookup_ProducesNoChecklistItems()
    {
        var envelope = MakeEnvelope(TaskComplexity.SimpleLookup);

        var checklist = await _planner.BuildChecklistAsync(envelope, CancellationToken.None);

        Assert.Empty(checklist.Items);
    }

    [Fact]
    public async Task SimpleLookup_ChecklistStartsInPlanningPhase()
    {
        var envelope = MakeEnvelope(TaskComplexity.SimpleLookup);

        var checklist = await _planner.BuildChecklistAsync(envelope, CancellationToken.None);

        Assert.Equal("Planning", checklist.CurrentPhase);
    }

    [Fact]
    public async Task MultiStepResearch_ProducesNoChecklistItems()
    {
        var envelope = MakeEnvelope(TaskComplexity.MultiStepResearch);

        var checklist = await _planner.BuildChecklistAsync(envelope, CancellationToken.None);

        Assert.Empty(checklist.Items);
    }

    [Fact]
    public async Task TrivialComplexity_ProducesNoChecklistItems()
    {
        var envelope = MakeEnvelope(TaskComplexity.Trivial);

        var checklist = await _planner.BuildChecklistAsync(envelope, CancellationToken.None);

        Assert.Empty(checklist.Items);
    }

    // ── TaskId propagation ───────────────────────────────────────────────────

    [Fact]
    public async Task Checklist_TaskIdMatchesEnvelopeTaskId()
    {
        var envelope = MakeEnvelope(TaskComplexity.MultiStepResearch);

        var checklist = await _planner.BuildChecklistAsync(envelope, CancellationToken.None);

        Assert.Equal(envelope.TaskId, checklist.TaskId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TaskEnvelope MakeEnvelope(TaskComplexity complexity) =>
        new TaskEnvelope
        {
            UserRequest = "Test request",
            Complexity = complexity
        };
}
