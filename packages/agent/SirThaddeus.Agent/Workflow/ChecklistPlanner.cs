namespace SirThaddeus.Agent.Workflow;

public sealed class ChecklistPlanner : IChecklistPlanner
{
    public Task<UserVisibleChecklist> BuildChecklistAsync(TaskEnvelope envelope, CancellationToken ct)
    {
        var items = envelope.Complexity == TaskComplexity.MultiStepResearch
            ? BuildResearchChecklist()
            : BuildSimpleChecklist();

        return Task.FromResult(new UserVisibleChecklist
        {
            TaskId = envelope.TaskId,
            CurrentPhase = "Planning",
            Items = items
        });
    }

    private static List<ChecklistItem> BuildSimpleChecklist()
    {
        return
        [
            new ChecklistItem { Order = 1, Title = "Understand the request" },
            new ChecklistItem { Order = 2, Title = "Check trusted sources" },
            new ChecklistItem { Order = 3, Title = "Compare findings" },
            new ChecklistItem { Order = 4, Title = "Answer with confidence" }
        ];
    }

    private static List<ChecklistItem> BuildResearchChecklist()
    {
        return
        [
            new ChecklistItem { Order = 1, Title = "Understand the question" },
            new ChecklistItem { Order = 2, Title = "Search official documentation" },
            new ChecklistItem { Order = 3, Title = "Gather supporting evidence" },
            new ChecklistItem { Order = 4, Title = "Resolve unclear points" },
            new ChecklistItem { Order = 5, Title = "Answer with confidence" }
        ];
    }
}