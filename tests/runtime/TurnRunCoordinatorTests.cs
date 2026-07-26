using Microsoft.Extensions.Logging.Abstractions;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.AuditLog;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Events;

namespace Thaddeus.Runtime.Tests;

public sealed class TurnRunCoordinatorTests
{
    [Fact]
    public async Task Pause_holds_at_next_safe_checkpoint_until_resume()
    {
        using var sut = Create();
        var run = sut.Create("thread-1", "user-1", CancellationToken.None);
        using var activation = sut.Activate(run.RunId);

        var pausing = sut.Pause(run.RunId);
        Assert.NotNull(pausing);
        Assert.Equal(TurnRunState.Pausing, pausing.State);

        var wait = sut.ReachCheckpointAsync(Context(), "tool-loop:tool:wiki_page_create", CancellationToken.None);

        Assert.False(wait.IsCompleted);
        Assert.Equal(TurnRunState.Paused, sut.Get(run.RunId)!.State);
        Assert.Equal("tool-loop:tool:wiki_page_create", sut.Get(run.RunId)!.Checkpoint);

        var resumed = sut.Resume(run.RunId);
        var steering = await wait.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(TurnRunState.Running, resumed!.State);
        Assert.Equal("assistant-1", resumed.AssistantMessageId);
        Assert.Null(steering);
    }

    [Fact]
    public async Task Cancel_propagates_to_checkpoint_and_terminal_state()
    {
        using var sut = Create();
        var run = sut.Create("thread-1", "user-1", CancellationToken.None);
        using var activation = sut.Activate(run.RunId);
        sut.Pause(run.RunId);

        var wait = sut.ReachCheckpointAsync(Context(), "pipeline:ToolLoop", sut.GetCancellationToken(run.RunId));
        sut.Cancel(run.RunId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        var completed = sut.Complete(run.RunId, cancelled: true);
        Assert.Equal(TurnRunState.Cancelled, completed!.State);
    }

    [Fact]
    public void CancelAll_cancels_only_live_runs()
    {
        using var sut = Create();
        var first = sut.Create("thread-1", "user-1", CancellationToken.None);
        var second = sut.Create("thread-2", "user-2", CancellationToken.None);
        sut.Complete(first.RunId, cancelled: false);

        var count = sut.CancelAll();

        Assert.Equal(1, count);
        Assert.Equal(TurnRunState.Completed, sut.Get(first.RunId)!.State);
        Assert.Equal(TurnRunState.Cancelling, sut.Get(second.RunId)!.State);
        Assert.True(sut.GetCancellationToken(second.RunId).IsCancellationRequested);
    }

    [Fact]
    public void Control_actions_are_written_to_local_audit_log()
    {
        var audit = new TestAuditLogger();
        using var sut = Create(audit);
        var run = sut.Create("thread-1", "user-1", CancellationToken.None);

        sut.Pause(run.RunId);
        sut.Resume(run.RunId);
        sut.Cancel(run.RunId);

        Assert.Equal(
            ["CHAT_RUN_PAUSE", "CHAT_RUN_RESUME", "CHAT_RUN_CANCEL"],
            audit.Events.Select(item => item.Action));
        Assert.All(audit.Events, item => Assert.Equal(run.RunId, item.Target));
    }

    [Fact]
    public async Task TakeOver_waits_then_redirect_returns_user_instruction_to_pipeline()
    {
        using var sut = Create();
        var run = sut.Create("thread-1", "user-1", CancellationToken.None);
        using var activation = sut.Activate(run.RunId);
        sut.TakeOver(run.RunId);

        var wait = sut.ReachCheckpointAsync(
            Context(),
            "tool-loop:tool:web_search",
            sut.GetCancellationToken(run.RunId));

        Assert.False(wait.IsCompleted);
        Assert.Equal(TurnRunState.TakingOver, sut.Get(run.RunId)!.State);

        sut.Redirect(run.RunId, "Use the local Wiki sources instead.");
        var steering = await wait.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("Use the local Wiki sources instead.", steering);
        Assert.Equal(TurnRunState.Running, sut.Get(run.RunId)!.State);
    }

    [Fact]
    public async Task Planned_run_does_not_release_execution_before_matching_version_is_approved()
    {
        using var sut = Create();
        var plan = WorkPlanBuilder.TryBuild("Research this and save a brief to the Wiki.")!;
        var run = sut.Create("thread-1", "user-1", CancellationToken.None, plan);

        var wait = sut.WaitForApprovalAsync(run.RunId, CancellationToken.None);

        Assert.False(wait.IsCompleted);
        Assert.Equal(TurnRunState.AwaitingApproval, run.State);
        Assert.Throws<TurnRunConflictException>(() => sut.ApprovePlan(run.RunId, plan.Version + 1));

        var approved = sut.ApprovePlan(run.RunId, plan.Version);
        var releasedPlan = await wait.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(TurnRunState.Running, approved!.State);
        Assert.Equal(plan.PlanId, releasedPlan!.PlanId);
    }

    [Fact]
    public void Plan_edit_is_versioned_and_requires_fresh_approval_version()
    {
        using var sut = Create();
        var plan = WorkPlanBuilder.TryBuild("Research this and save a brief to the Wiki.")!;
        var run = sut.Create("thread-1", "user-1", CancellationToken.None, plan);
        var reordered = plan.Steps.Reverse().ToArray();

        var edited = sut.EditPlan(run.RunId, plan.Version, reordered);

        Assert.Equal(2, edited!.Plan!.Version);
        Assert.Equal(reordered.Select(step => step.StepId), edited.Plan.Steps.Select(step => step.StepId));
        Assert.Throws<TurnRunConflictException>(() => sut.ApprovePlan(run.RunId, plan.Version));
        Assert.Equal(TurnRunState.Running, sut.ApprovePlan(run.RunId, edited.Plan.Version)!.State);
    }

    private static TurnRunCoordinator Create(TestAuditLogger? audit = null)
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        return new TurnRunCoordinator(
            new ChatTurnPublisher(bus),
            audit ?? new TestAuditLogger(),
            NullLogger<TurnRunCoordinator>.Instance);
    }

    private static TurnContext Context() => new()
    {
        ThreadId = "thread-1",
        MessageId = "assistant-1",
        UserText = "Research and save this.",
    };
}
