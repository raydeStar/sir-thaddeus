using System.Text.Json.Serialization;

namespace SirThaddeus.Agent.Orchestration;

/// <summary>
/// A strict contract representing the router's final decision.
/// Downstream stages (Clarify Gate, Plan Validator) consume this.
/// </summary>
public sealed record IntentDecisionV2
{
    /// <summary>
    /// The classified intent (e.g., "LookupFact", "ChatOnly", "GeneralTool").
    /// </summary>
    public string Intent { get; init; } = "ChatOnly";

    /// <summary>
    /// The confidence score of the classification (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// Whether the intent classifier (or rules) determined the user's message is too ambiguous.
    /// </summary>
    public bool RequiresClarification { get; init; } = false;

    /// <summary>
    /// The clarification question to ask the user, if <see cref="RequiresClarification"/> is true.
    /// </summary>
    public string? ClarificationQuestion { get; init; }

    /// <summary>
    /// Strongly-typed slots extracted for this intent.
    /// </summary>
    public IIntentSlots? Slots { get; init; }

    /// <summary>
    /// Optional observability codes describing how this route was chosen.
    /// </summary>
    public IReadOnlyList<string> RouteReasonCodes { get; init; } = [];
}

/// <summary>
/// Base interface for intent-specific slots.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(IntentSlots.SearchSlots), typeDiscriminator: "search")]
[JsonDerivedType(typeof(IntentSlots.MemoryWriteSlots), typeDiscriminator: "memory_write")]
[JsonDerivedType(typeof(IntentSlots.OpenEntitySlots), typeDiscriminator: "open_entity")]
public interface IIntentSlots
{
}
