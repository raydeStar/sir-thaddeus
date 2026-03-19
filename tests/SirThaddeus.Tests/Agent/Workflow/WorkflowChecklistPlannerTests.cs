using SirThaddeus.Agent.Workflow;

namespace SirThaddeus.Tests;

public sealed class WorkflowChecklistPlannerTests
{
    private readonly IChecklistPlanner _planner = new ChecklistPlanner();

    // ── Simple checklist ─────────────────────────────────────────────────────

    [Fact]
    public async Task SimpleLookup_ProducesFourItemChecklist()
    {
        var envelope = MakeEnvelope(TaskComplexity.SimpleLookup);

        var checklist = await _planner.BuildChecklistAsync(envelope, CancellationToken.None);

        Assert.Equal(4, checklist.Items.Count);
    }

    [Fact]
    public async Task SimpleLookup_ChecklistStartsInPlanningPhase()
    {
        var envelope = MakeEnvelope(TaskComplexity.SimpleLookup);

        var checklist = await _planner.BuildChecklistAsync(envelope, CancellationToken.None);

        Assert.Equal("Planning", checklist.CurrentPhase);
    }

    [Fact]
    public async Task SimpleLookup_ItemsAreOrderedOneToFour()
    {
        var envelope = MakeEnvelope(TaskComplexity.SimpleLookup);

        var checklist = await _planner.BuildChecklistAsync(envelope, CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], checklist.Items.Select(i => i.Order).ToArray());
    }

    [Fact]
    public async Task SimpleLookup_AllItemsStartAsPending()
    {
        var envelope = MakeEnvelope(TaskComplexity.SimpleLookup);

        var checklist = await _planner.BuildChecklistAsync(envelope, CancellationToken.None);

        Assert.All(checklist.Items, item => Assert.Equal(ChecklistItemState.Pending, item.State));
    }

    [Fact]
    public async Task SimpleLookup_FinalItemContainsAnswerWithConfidence()
    {
        var envelope = MakeEnvelope(TaskComplexity.SimpleLookup);

        var checklist = await _planner.BuildChecklistAsync(envelope, CancellationToken.None);

        Assert.Contains("confidence", checklist.Items.Last().Title, StringComparison.OrdinalIgnoreCase);
    }

    // ── Research checklist ───────────────────────────────────────────────────

    [Fact]
    public async Task MultiStepResearch_ProducesFiveItemChecklist()
    {
        var envelope = MakeEnvelope(TaskComplexity.MultiStepResearch);

        var checklist = await _planner.BuildChecklistAsync(envelope, CancellationToken.None);

        Assert.Equal(5, checklist.Items.Count);
    }

    [Fact]
    public async Task MultiStepResearch_ItemsAreOrderedOneToFive()
    {
        var envelope = MakeEnvelope(TaskComplexity.MultiStepResearch);

        var checklist = await _planner.BuildChecklistAsync(envelope, CancellationToken.None);

        Assert.Equal([1, 2, 3, 4, 5], checklist.Items.Select(i => i.Order).ToArray());
    }

    [Fact]
    public async Task MultiStepResearch_IncludesOfficialDocumentationStep()
    {
        var envelope = MakeEnvelope(TaskComplexity.MultiStepResearch);

        var checklist = await _planner.BuildChecklistAsync(envelope, CancellationToken.None);

        Assert.Contains(checklist.Items, i =>
            i.Title.Contains("documentation", StringComparison.OrdinalIgnoreCase) ||
            i.Title.Contains("official", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MultiStepResearch_IncludesEvidenceStep()
    {
        var envelope = MakeEnvelope(TaskComplexity.MultiStepResearch);

        var checklist = await _planner.BuildChecklistAsync(envelope, CancellationToken.None);

        Assert.Contains(checklist.Items, i =>
            i.Title.Contains("evidence", StringComparison.OrdinalIgnoreCase));
    }

    // ── Trivial complexity falls back to simple ──────────────────────────────

    [Fact]
    public async Task TrivialComplexity_FallsBackToFourItemChecklist()
    {
        var envelope = MakeEnvelope(TaskComplexity.Trivial);

        var checklist = await _planner.BuildChecklistAsync(envelope, CancellationToken.None);

        Assert.Equal(4, checklist.Items.Count);
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
