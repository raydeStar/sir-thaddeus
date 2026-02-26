namespace SirThaddeus.Agent.Orchestration.IntentSlots;

/// <summary>
/// Slots extracted for memory write operations.
/// </summary>
public sealed record MemoryWriteSlots : IIntentSlots
{
    /// <summary>
    /// The subject the memory is about (e.g., "the user", "project X").
    /// </summary>
    public string? Subject { get; init; }

    /// <summary>
    /// The specific fact or rule to remember.
    /// </summary>
    public string Fact { get; init; } = string.Empty;
}
