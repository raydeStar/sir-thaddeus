namespace SirThaddeus.Agent.Orchestration;

/// <summary>
/// Handles intents that bypass the tool loop entirely (search, news,
/// deep-dive, screen capture, memory read, utility deterministic).
/// The V2 pipeline delegates to this instead of throwing exceptions
/// for non-tool-loop intents.
/// </summary>
public interface IDeterministicIntentExecutor
{
    /// <summary>
    /// Attempts to execute a deterministic (no-tool-loop) intent.
    /// Returns null if the intent is not recognised, allowing the
    /// caller to decide on a fallback.
    /// </summary>
    Task<AgentResponse?> TryExecuteAsync(
        string userMessage,
        IntentDecisionV2 decision,
        string canonicalIntent,
        CancellationToken cancellationToken);
}
