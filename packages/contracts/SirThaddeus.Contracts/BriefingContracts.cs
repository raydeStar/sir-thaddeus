namespace SirThaddeus.Contracts;

public sealed record DeepDiveBriefingDto(
    int Version,
    BriefingTopicDto Topic,
    BriefingHeroDto Hero,
    IReadOnlyList<BriefingCardDto> Cards,
    BriefingMapDto? Map,
    IReadOnlyList<BriefingAuditStepDto> Audit);

public sealed record BriefingTopicDto(
    string Kind,
    string Query,
    string Timezone,
    string Locale,
    string? UserLocationHint);

public sealed record BriefingHeroDto(
    string Title,
    string Confidence,
    string LastCheckedIso,
    string StatusLine,
    string ClosesText,
    string Address,
    string Phone,
    string Website,
    string DirectionsUrl);

public sealed record BriefingCardDto(
    string Type,
    string Title,
    IReadOnlyList<BriefingSourceRefDto> Sources,
    IReadOnlyList<string> Bullets);

public sealed record BriefingSourceRefDto(
    string Name,
    string Url,
    string FetchedIso);

public sealed record BriefingAuditStepDto(
    string Step,
    string Detail,
    string TimestampIso,
    IReadOnlyList<BriefingSourceRefDto> Sources);

public sealed record BriefingMapDto(
    double Latitude,
    double Longitude,
    string Label);
