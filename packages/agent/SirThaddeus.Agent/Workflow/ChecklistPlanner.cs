namespace SirThaddeus.Agent.Workflow;

public sealed class ChecklistPlanner : IChecklistPlanner
{
    public Task<UserVisibleChecklist> BuildChecklistAsync(TaskEnvelope envelope, CancellationToken ct)
    {
        return Task.FromResult(new UserVisibleChecklist
        {
            TaskId = envelope.TaskId,
            CurrentPhase = "Planning",
            Items = []
        });
    }
}