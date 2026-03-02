namespace SirThaddeus.Agent.Validation.Completion;

/// <summary>
/// Whether a field is mandatory or best-effort.
/// </summary>
public enum FieldNecessity
{
    /// <summary>Response is incomplete without this field.</summary>
    Required,

    /// <summary>Nice to have — absence doesn't mark the response as partial.</summary>
    Optional
}

/// <summary>
/// A single named field that a completion contract expects in the
/// tool results or final answer. Used by <see cref="CompletionChecker"/>
/// to determine whether the response is complete.
/// </summary>
public sealed record FieldRequirement
{
    /// <summary>
    /// Machine-readable field name (e.g. "name", "address", "phone").
    /// Used for lookup in structured tool results.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// Human-readable description shown when the field is missing
    /// (e.g. "business phone number or website").
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Whether this field is required or optional.</summary>
    public FieldNecessity Necessity { get; init; } = FieldNecessity.Required;

    /// <summary>
    /// Alternative field names that can satisfy this requirement.
    /// For example, "phone" might accept "phone_number" or "telephone".
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}
