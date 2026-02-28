using SirThaddeus.AuditLog;

namespace SirThaddeus.Agent.Orchestration;

/// <summary>
/// Bridges deterministic no-tool-loop intents (search, news, deep-dive,
/// screen capture, memory read, utility) back to the legacy orchestrator
/// until their logic is fully migrated into V2 pipeline stages.
/// </summary>
internal sealed class LegacyDeterministicExecutor : IDeterministicIntentExecutor
{
    private static readonly HashSet<string> DeterministicIntents = new(StringComparer.OrdinalIgnoreCase)
    {
        Intents.LookupSearch,
        Intents.LookupFact,
        Intents.LookupNews,
        Intents.LookupDeepDive,
        Intents.ScreenObserve,
        Intents.MemoryRead,
        Intents.UtilityDeterministic
    };

    private readonly IAgentOrchestrator _legacy;
    private readonly IAuditLogger _audit;

    public LegacyDeterministicExecutor(IAgentOrchestrator legacy, IAuditLogger audit)
    {
        _legacy = legacy;
        _audit = audit;
    }

    public async Task<AgentResponse?> TryExecuteAsync(
        string userMessage,
        IntentDecisionV2 decision,
        string canonicalIntent,
        CancellationToken cancellationToken)
    {
        if (!DeterministicIntents.Contains(canonicalIntent))
            return null;

        _audit.Append(new AuditEvent
        {
            Actor = "v2_pipeline",
            Action = "DETERMINISTIC_LEGACY_DELEGATE",
            Result = $"Intent={canonicalIntent}, Confidence={decision.Confidence:F2}"
        });

        return await _legacy.ProcessAsync(userMessage, cancellationToken);
    }
}
