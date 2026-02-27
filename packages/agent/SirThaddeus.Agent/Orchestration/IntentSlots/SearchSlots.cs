namespace SirThaddeus.Agent.Orchestration.IntentSlots;

/// <summary>
/// Slots extracted for search or fact-lookup intents.
/// </summary>
public sealed record SearchSlots : IIntentSlots
{
    /// <summary>
    /// The core query to execute.
    /// </summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>
    /// An optional location context extracted from the query (e.g., "Seattle" or "nearby").
    /// </summary>
    public string? Location { get; init; }
}
