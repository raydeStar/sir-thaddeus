namespace SirThaddeus.Agent.Workflow;

public sealed class RetryPlanner : IRetryPlanner
{
    private static readonly string[] StrategyLadder =
    [
        "official_source_search",
        "known_docs_page_search",
        "site_specific_search",
        "broader_alternative_keywords",
        "community_issue_tracker_search",
        "best_available_summary"
    ];

    public Task<IReadOnlyList<PlannedAction>> BuildRetryPlanAsync(TaskRunState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        var strategyIndex = Math.Clamp(state.RetriesUsed, 0, StrategyLadder.Length - 1);
        var strategy = StrategyLadder[strategyIndex];

        var action = new PlannedAction
        {
            StepId = state.Checklist.Items.FirstOrDefault(i => i.Order == 3)?.Id ?? string.Empty,
            ActionType = "retry_search",
            RetryStrategy = strategy,
            UserVisible = false,
            Instruction = BuildInstruction(state.Envelope.UserRequest, state.DraftAnswer, strategy)
        };

        return Task.FromResult<IReadOnlyList<PlannedAction>>([action]);
    }

    private static string BuildInstruction(string userRequest, string? priorAnswer, string strategy)
    {
        var prior = string.IsNullOrWhiteSpace(priorAnswer)
            ? "No prior answer available."
            : priorAnswer;

        var strategyInstruction = strategy switch
        {
            "official_source_search" => "Prioritize official/first-party documentation and policy pages.",
            "known_docs_page_search" => "Search known help center and documentation sections directly.",
            "site_specific_search" => "Use site-specific query variants against official domains.",
            "broader_alternative_keywords" => "Use broader, alternate query terms and synonyms.",
            "community_issue_tracker_search" => "Cross-check reputable community discussions and issue trackers with caveats.",
            _ => "Summarize best available evidence with explicit uncertainty caveats."
        };

        return $"User request: {userRequest}\n" +
               $"Retry strategy: {strategy}\n" +
               $"Guidance: {strategyInstruction}\n" +
               $"Previous answer for verification:\n{prior}\n" +
               "Return concise, evidence-grounded output and call out uncertainty when unresolved.";
    }
}