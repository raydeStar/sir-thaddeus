namespace SirThaddeus.Agent.Workflow;

public sealed class TaskClassifier : ITaskClassifier
{
    public Task<TaskEnvelope> ClassifyAsync(string userRequest, CancellationToken ct)
    {
        var text = (userRequest ?? string.Empty).Trim();
        var lower = text.ToLowerInvariant();

        var complexity = TaskComplexity.SimpleLookup;
        if (text.Length <= 24 &&
            !lower.Contains("find ") &&
            !lower.Contains("search") &&
            !lower.Contains("compare") &&
            !lower.Contains("research") &&
            !lower.Contains("details"))
        {
            complexity = TaskComplexity.Trivial;
        }
        else if (lower.Contains("compare") ||
                 lower.Contains("research") ||
                 lower.Contains("details") ||
                 lower.Contains("pricing") ||
                 lower.Contains("github") ||
                 lower.Contains("billing"))
        {
            complexity = TaskComplexity.MultiStepResearch;
        }

        var needsTools = complexity != TaskComplexity.Trivial ||
                         lower.Contains("today") ||
                         lower.Contains("hours") ||
                         lower.Contains("price") ||
                         lower.Contains("latest");

        var envelope = new TaskEnvelope
        {
            UserRequest = text,
            Intent = needsTools ? "lookup" : "direct_answer",
            Complexity = complexity,
            NeedsTools = needsTools,
            ShowChecklist = complexity != TaskComplexity.Trivial,
            TimeBudget = complexity == TaskComplexity.MultiStepResearch
                ? TimeSpan.FromSeconds(60)
                : TimeSpan.FromSeconds(30),
            MaxRetries = complexity == TaskComplexity.MultiStepResearch ? 1 : 1,
            MaxToolCalls = 8
        };

        return Task.FromResult(envelope);
    }
}