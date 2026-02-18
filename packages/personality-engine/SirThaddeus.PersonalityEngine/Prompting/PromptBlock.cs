namespace SirThaddeus.PersonalityEngine.Prompting;

/// <summary>
/// Stable prompt layer categories.
/// </summary>
public enum PromptBlockKind
{
    Trust = 0,
    Security = 1,
    Personality = 2,
    Task = 3,
    Mode = 4,
    MemoryAnchor = 5
}

/// <summary>
/// Structured prompt block that participates in deterministic rendering.
/// </summary>
public sealed record PromptBlock
{
    public required string Id { get; init; }
    public required int Priority { get; init; }
    public required PromptBlockKind Kind { get; init; }
    public required string Text { get; init; }
    public int MaxTokensHint { get; init; }
    public string? Hash { get; init; }
}
