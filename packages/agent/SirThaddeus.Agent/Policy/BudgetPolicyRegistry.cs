namespace SirThaddeus.Agent.Policy;

/// <summary>
/// Maps intent strings to their budget policies.
/// Intents without an explicit mapping get <see cref="BudgetPolicy.Default"/>.
/// </summary>
public static class BudgetPolicyRegistry
{
    private static readonly Dictionary<string, BudgetPolicy> Policies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // No tools needed
            [Intents.ChatOnly] = BudgetPolicy.NoTools,
            [Intents.UtilityDeterministic] = BudgetPolicy.NoTools,
            [Intents.MemoryRead] = BudgetPolicy.NoTools,

            // Standard tool budgets
            [Intents.LookupFact] = BudgetPolicy.Default,
            [Intents.LookupSearch] = BudgetPolicy.Default,
            [Intents.LookupNews] = BudgetPolicy.Default,
            [Intents.ScreenObserve] = BudgetPolicy.Default,
            [Intents.FileTask] = BudgetPolicy.Default,
            [Intents.SystemTask] = BudgetPolicy.Default,
            [Intents.MemoryWrite] = BudgetPolicy.Default,
            [Intents.BrowseOnce] = BudgetPolicy.Default,

            // Research budgets (more headroom)
            [Intents.LookupDeepDive] = BudgetPolicy.Research,
            [Intents.OneShotDiscovery] = BudgetPolicy.Research,
        };

    /// <summary>
    /// Returns the budget policy for the given intent.
    /// Unknown intents get <see cref="BudgetPolicy.Default"/>.
    /// </summary>
    public static BudgetPolicy For(string intent) =>
        Policies.TryGetValue(intent, out var policy)
            ? policy
            : BudgetPolicy.Default;
}
