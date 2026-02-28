namespace SirThaddeus.Agent.Policy;

/// <summary>
/// Per-turn budget constraints for the orchestrator. Controls how many
/// tool calls, LLM round-trips, and repair attempts a single turn may
/// consume. Intents can override the defaults to be tighter (e.g. chat)
/// or looser (e.g. deep-dive research).
///
/// Design rules:
///   • Budgets are hard ceilings — never exceeded, even if the LLM asks.
///   • Defaults are deliberately conservative for small local models.
///   • The orchestrator reads these at turn-start and stamps them into
///     <see cref="Orchestration.Correlation.RunContext"/>.
/// </summary>
public sealed record BudgetPolicy
{
    /// <summary>Maximum tool calls in a single turn.</summary>
    public int MaxToolCalls { get; init; } = Defaults.MaxToolCalls;

    /// <summary>Maximum LLM round-trips in a single turn.</summary>
    public int MaxLlmRoundTrips { get; init; } = Defaults.MaxLlmRoundTrips;

    /// <summary>Maximum tool calls proposed in a single LLM response.</summary>
    public int MaxToolCallsPerResponse { get; init; } = Defaults.MaxToolCallsPerResponse;

    /// <summary>Maximum repair attempts (re-plan after incomplete results).</summary>
    public int MaxRepairs { get; init; } = Defaults.MaxRepairs;

    /// <summary>Maximum tokens for the LLM response.</summary>
    public int? MaxResponseTokens { get; init; }

    /// <summary>Hard timeout for the entire turn.</summary>
    public TimeSpan? TurnTimeout { get; init; }

    /// <summary>Default budget constants — matches current hardcoded values.</summary>
    public static class Defaults
    {
        public const int MaxToolCalls = 15;
        public const int MaxLlmRoundTrips = 10;
        public const int MaxToolCallsPerResponse = 5;
        public const int MaxRepairs = 2;
    }

    /// <summary>Default budget — matches current behavior exactly.</summary>
    public static readonly BudgetPolicy Default = new();

    /// <summary>
    /// Tight budget for chat-only / utility intents that should
    /// never enter the tool loop.
    /// </summary>
    public static readonly BudgetPolicy NoTools = new()
    {
        MaxToolCalls = 0,
        MaxLlmRoundTrips = 1,
        MaxToolCallsPerResponse = 0,
        MaxRepairs = 0
    };

    /// <summary>
    /// Research budget for deep-dive / discovery intents that
    /// may need more tool calls and round-trips.
    /// </summary>
    public static readonly BudgetPolicy Research = new()
    {
        MaxToolCalls = 20,
        MaxLlmRoundTrips = 12,
        MaxToolCallsPerResponse = 8,
        MaxRepairs = 3
    };
}
