namespace SirThaddeus.Agent.Search;

/// <summary>
/// Abstraction over deterministic utility parsing/matching.
/// Keeps orchestrator and router code decoupled from static helpers.
/// </summary>
public interface IDeterministicUtilityEngine
{
    /// <summary>
    /// Attempts to match a user message to a deterministic utility result.
    /// </summary>
    DeterministicUtilityMatch? TryMatch(string userMessage);
}

/// <summary>
/// Default adapter to the existing deterministic pre-router.
/// </summary>
public sealed class DeterministicUtilityEngineAdapter : IDeterministicUtilityEngine
{
    public DeterministicUtilityMatch? TryMatch(string userMessage)
        => DeterministicPreRouter.TryRoute(userMessage);
}

