namespace SirThaddeus.Agent.Orchestration.IntentSlots;

/// <summary>
/// Slots extracted for interacting with a specific entity (like opening a file or url).
/// </summary>
public sealed record OpenEntitySlots : IIntentSlots
{
    /// <summary>
    /// The type of entity (e.g., "url", "file", "folder").
    /// </summary>
    public string EntityType { get; init; } = string.Empty;

    /// <summary>
    /// The specific identifier or name of the entity.
    /// </summary>
    public string EntityIdOrName { get; init; } = string.Empty;
}
