namespace SirThaddeus.Agent.Validation.Completion;

/// <summary>
/// Defines "what does done look like" for a specific intent.
/// The <see cref="CompletionChecker"/> evaluates tool results against
/// the contract and produces a <see cref="CompletionReport"/>.
///
/// Design rules:
///   • Contracts are static per intent — no LLM involvement.
///   • A missing required field makes the response partial, never fabricated.
///   • Contracts are intentionally conservative; over-specifying leads to
///     false negatives on otherwise good responses.
/// </summary>
public sealed record CompletionContract
{
    /// <summary>
    /// The intent this contract applies to (e.g. "lookup_fact", "lookup_deep_dive").
    /// Used for lookup and diagnostics.
    /// </summary>
    public required string Intent { get; init; }

    /// <summary>
    /// Fields that should appear in the tool results or final answer.
    /// Required fields missing → response is partial.
    /// Optional fields missing → noted but response is still complete.
    /// </summary>
    public IReadOnlyList<FieldRequirement> Fields { get; init; } = [];

    /// <summary>
    /// Evidence requirements (source URLs, named citations).
    /// </summary>
    public EvidenceRequirement Evidence { get; init; } = EvidenceRequirement.None;

    /// <summary>
    /// For list-type intents (e.g. "nearby businesses"), the minimum
    /// number of result items. Zero means no minimum.
    /// </summary>
    public int MinItems { get; init; }

    /// <summary>
    /// Human-readable label for diagnostics/logs.
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// A null-object contract that is always satisfied.
    /// Used for intents that don't need completion checking
    /// (e.g. ChatOnly, UtilityDeterministic).
    /// </summary>
    public static readonly CompletionContract AlwaysSatisfied = new()
    {
        Intent = "*",
        Label = "always_satisfied",
        Fields = [],
        Evidence = EvidenceRequirement.None,
        MinItems = 0
    };
}
