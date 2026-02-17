namespace SirThaddeus.Agent.Search.DeepDive;

/// <summary>
/// UI-facing projection that normalizes briefing payloads into a shape that
/// desktop view-models can consume safely.
/// </summary>
public sealed record DeepDiveBriefingProjection
{
    public required string HeroTitle { get; init; }
    public required string HeroConfidence { get; init; }
    public required string HeroStatusLine { get; init; }
    public required IReadOnlyList<DeepDiveCardProjection> Cards { get; init; }
    public required IReadOnlyList<DeepDiveAuditProjection> Audit { get; init; }
    public bool HasMap { get; init; }
}

public sealed record DeepDiveCardProjection
{
    public required string Type { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<string> Bullets { get; init; }
    public required IReadOnlyList<SourceRef> Sources { get; init; }
}

public sealed record DeepDiveAuditProjection
{
    public required string Step { get; init; }
    public required string Detail { get; init; }
    public required string TimestampIso { get; init; }
}

public static class DeepDiveBriefingViewModelProjection
{
    public static DeepDiveBriefingProjection Map(DeepDiveBriefing briefing)
    {
        ArgumentNullException.ThrowIfNull(briefing);

        return new DeepDiveBriefingProjection
        {
            HeroTitle = briefing.Hero.Title,
            HeroConfidence = briefing.Hero.Confidence,
            HeroStatusLine = briefing.Hero.StatusLine,
            Cards = briefing.Cards.Select(card => new DeepDiveCardProjection
            {
                Type = card.Type,
                Title = card.Title,
                Bullets = card.Bullets,
                Sources = card.Sources
            }).ToList(),
            Audit = briefing.Audit.Select(step => new DeepDiveAuditProjection
            {
                Step = step.Step,
                Detail = step.Detail,
                TimestampIso = step.TimestampIso
            }).ToList(),
            HasMap = briefing.Map is not null
        };
    }
}
