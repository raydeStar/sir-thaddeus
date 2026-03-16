namespace SirThaddeus.Agent.Workflow;

public sealed class ProgressNarrator : IProgressNarrator
{
    public Task<string?> BuildUpdateAsync(TaskRunState state, ProgressTrigger trigger, CancellationToken ct)
    {
        var message = trigger switch
        {
            ProgressTrigger.TaskStarted => "Checking the strongest source first.",
            ProgressTrigger.ChecklistInitialized => "I mapped a quick checklist and started the first step.",
            ProgressTrigger.MilestoneReached => "I found useful evidence and I’m validating it now.",
            ProgressTrigger.ContradictionDetected => "I’m seeing conflicting details, so I’m comparing stronger sources.",
            ProgressTrigger.RetryStarted => "Confidence is still low, so I’m trying a different verification strategy.",
            ProgressTrigger.PartialAnswerReady => "I have a likely answer, but I’m doing one more verification pass.",
            ProgressTrigger.Finalizing => "I’ve reached the best-supported answer and I’m finalizing it.",
            ProgressTrigger.Completed => "All right — I’ve got the best-supported answer.",
            ProgressTrigger.TimedOut => "I hit the time budget, so I’m finalizing with the best available evidence.",
            ProgressTrigger.Cancelled => "Stopped on request.",
            ProgressTrigger.Failed => "I ran into an issue and couldn’t complete this run.",
            _ => null
        };

        return Task.FromResult<string?>(message);
    }
}