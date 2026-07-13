using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;

namespace SirThaddeus.Agent.Workflow;

public sealed class TaskClassifier : ITaskClassifier
{
    public Task<TaskEnvelope> ClassifyAsync(string userRequest, CancellationToken ct)
    {
        var text = (userRequest ?? string.Empty).Trim();
        var lower = text.ToLowerInvariant();

        if (IsWorkflowDirectAnswerPrompt(lower))
        {
            return Task.FromResult(new TaskEnvelope
            {
                UserRequest = text,
                Intent = "direct_answer",
                Complexity = TaskComplexity.Trivial,
                NeedsTools = false,
                ShowChecklist = false,
                // 180s for the trivial direct-answer path: no tool calls
                // but the LLM still has to draft + sanitize, and a 4B
                // local model's first draft can easily take 30-60s on
                // longer prompts. Earlier 30/60s ceilings caused
                // "Cancelled" mid-response.
                TimeBudget = TimeSpan.FromSeconds(180),
                MaxRetries = 1,
                MaxToolCalls = 8
            });
        }

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
                 lower.Contains("billing") ||
                 lower.Contains("flight") ||
                 lower.Contains("cheapest") ||
                 lower.Contains("in stock") ||
                 lower.Contains("availability") ||
                 lower.Contains("verify"))
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
            // Budgets sized for a 4B-class local model — generous ceilings
            // so the whole tool loop + final draft fits comfortably. A
            // single 4B response can take 40-60s; a multi-round research
            // turn can stack to several minutes. Smaller models will still
            // return long before the timer fires.
            //   trivial/simple lookup:       240s  (draft + 0-2 tool calls)
            //   multi-step research:         600s  (multiple LLM rounds)
            TimeBudget = complexity == TaskComplexity.MultiStepResearch
                ? TimeSpan.FromSeconds(600)
                : TimeSpan.FromSeconds(240),
            MaxRetries = complexity == TaskComplexity.MultiStepResearch ? 1 : 1,
            MaxToolCalls = 8
        };

        return Task.FromResult(envelope);
    }

    public static bool IsWorkflowDirectAnswerPrompt(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return true;

        return IntentFeatureExtractor.LooksLikeGreetingOnlyOrSmallTalk(lower) ||
               IntentFeatureExtractor.LooksLikeVoiceMicCheck(lower) ||
               IntentFeatureExtractor.LooksLikeLogicPuzzlePrompt(lower) ||
               ExplicitResponseContractDetector.IsNoToolDirectAnswer(lower) ||
               UtilityRouter.TryHandle(lower) is not null;
    }
}
