using Thaddeus.Runtime.Chat;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tests;

public sealed class WorkPlanBuilderTests
{
    [Theory]
    [InlineData("Hello!")]
    [InlineData("What is photosynthesis?")]
    [InlineData("Summarize this paragraph.")]
    [InlineData("What time is it?")]
    public void TryBuild_keeps_simple_conversation_on_the_direct_path(string prompt)
    {
        Assert.Null(WorkPlanBuilder.TryBuild(prompt));
    }

    [Fact]
    public void TryBuild_plans_research_to_durable_wiki_work()
    {
        var plan = WorkPlanBuilder.TryBuild(
            "Research the current release evidence, compare the sources, and save a brief to the Wiki.");

        Assert.NotNull(plan);
        Assert.Equal(WorkPlanRisk.Medium, plan.Risk);
        Assert.Equal(
            [
                WorkPlanCapability.Context,
                WorkPlanCapability.Research,
                WorkPlanCapability.Compose,
                WorkPlanCapability.DurableOutput,
                WorkPlanCapability.Verify,
            ],
            plan.Steps.Select(step => step.Capability));
        Assert.All(plan.Steps, step => Assert.Equal(WorkPlanStepStatus.Pending, step.Status));
    }

    [Fact]
    public void TryBuild_always_plans_consequential_single_action()
    {
        var plan = WorkPlanBuilder.TryBuild("Delete the outdated local report.");

        Assert.NotNull(plan);
        Assert.Equal(WorkPlanRisk.High, plan.Risk);
        Assert.Contains(plan.Steps, step => step.Capability == WorkPlanCapability.DurableOutput);
        Assert.Contains("irreversible", plan.RiskSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryBuild_detects_explicit_sequence_without_using_a_model()
    {
        var plan = WorkPlanBuilder.TryBuild(
            "First review the notes. Then summarize the decision. Finally verify the result.");

        Assert.NotNull(plan);
        Assert.True(plan.Steps.Count >= 2);
    }

    [Fact]
    public void Edited_plan_validation_rejects_duplicate_ids_and_completed_states()
    {
        var plan = WorkPlanBuilder.TryBuild("Research this and save a Wiki brief.")!;
        var duplicate = plan.Steps
            .Select((step, index) => index == 1 ? step with { StepId = plan.Steps[0].StepId } : step)
            .ToArray();
        Assert.False(WorkPlanBuilder.TryValidateEditedSteps(duplicate, out _));

        var completed = plan.Steps
            .Select((step, index) => index == 0 ? step with { Status = WorkPlanStepStatus.Done } : step)
            .ToArray();
        Assert.False(WorkPlanBuilder.TryValidateEditedSteps(completed, out _));
    }
}
