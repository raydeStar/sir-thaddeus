namespace SirThaddeus.Agent.Search;

/// <summary>
/// Deterministic pre-router used before LLM intent classification.
/// Confidence is derived from deterministic parse quality only.
/// </summary>
public static class DeterministicPreRouter
{
    public static DeterministicUtilityMatch? TryRoute(string userMessage)
        => DeterministicUtilityEngine.TryMatch(userMessage);
}
