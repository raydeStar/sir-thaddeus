using SirThaddeus.Agent.Search.DeepDive;
using SirThaddeus.Contracts;
using SirThaddeus.Memory;

internal static partial class RuntimeApiServer
{
    private static MemoryFactItemDto ToFactDto(MemoryFact fact) => new(
        MemoryId: fact.MemoryId,
        ProfileId: fact.ProfileId,
        Subject: fact.Subject,
        Predicate: fact.Predicate,
        Object: fact.Object,
        Confidence: fact.Confidence,
        UpdatedAtUtc: fact.UpdatedAt.ToUniversalTime(),
        SourceRef: fact.SourceRef);

    private static MemoryEventItemDto ToEventDto(MemoryEvent evt) => new(
        EventId: evt.EventId,
        ProfileId: evt.ProfileId,
        Type: evt.Type,
        Title: evt.Title,
        Summary: evt.Summary,
        WhenUtc: evt.WhenIso?.ToUniversalTime(),
        Confidence: evt.Confidence,
        UpdatedAtUtc: evt.UpdatedAt.ToUniversalTime(),
        SourceRef: evt.SourceRef);

    private static MemoryChunkItemDto ToChunkDto(MemoryChunk chunk) => new(
        ChunkId: chunk.ChunkId,
        SourceType: chunk.SourceType,
        SourceRef: chunk.SourceRef,
        Text: chunk.Text,
        WhenUtc: chunk.WhenIso?.ToUniversalTime());

    private static MemoryNuggetItemDto ToNuggetDto(MemoryNugget nugget) => new(
        NuggetId: nugget.NuggetId,
        Text: nugget.Text,
        Tags: nugget.Tags,
        Weight: nugget.Weight,
        PinLevel: nugget.PinLevel,
        UseCount: nugget.UseCount,
        UpdatedAtUtc: nugget.UpdatedAt.ToUniversalTime());

    private static DeepDiveBriefingDto? ToBriefingDto(DeepDiveBriefing? briefing)
    {
        if (briefing is null)
        {
            return null;
        }

        return new DeepDiveBriefingDto(
            briefing.Version,
            new BriefingTopicDto(
                briefing.Topic.Kind,
                briefing.Topic.Query,
                briefing.Topic.Timezone,
                briefing.Topic.Locale,
                briefing.Topic.UserLocationHint),
            new BriefingHeroDto(
                briefing.Hero.Title,
                briefing.Hero.Confidence,
                briefing.Hero.LastCheckedIso,
                briefing.Hero.StatusLine,
                briefing.Hero.ClosesText,
                briefing.Hero.Address,
                briefing.Hero.Phone,
                briefing.Hero.Website,
                briefing.Hero.DirectionsUrl),
            briefing.Cards.Select(card => new BriefingCardDto(
                card.Type,
                card.Title,
                card.Sources.Select(ToSourceRefDto).ToArray(),
                card.Bullets.ToArray())).ToArray(),
            briefing.Map is null
                ? null
                : new BriefingMapDto(briefing.Map.Latitude, briefing.Map.Longitude, briefing.Map.Label),
            briefing.Audit.Select(step => new BriefingAuditStepDto(
                step.Step,
                step.Detail,
                step.TimestampIso,
                step.Sources.Select(ToSourceRefDto).ToArray())).ToArray());
    }

    private static BriefingSourceRefDto ToSourceRefDto(SourceRef source)
        => new(source.Name, source.Url, source.FetchedIso);
}
