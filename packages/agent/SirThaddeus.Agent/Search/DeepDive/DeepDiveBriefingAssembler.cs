namespace SirThaddeus.Agent.Search.DeepDive;

/// <summary>
/// Deterministic assembly of the final briefing payload from normalized inputs.
/// </summary>
public sealed class DeepDiveBriefingAssembler
{
    public DeepDiveBriefing Assemble(DeepDiveAssembleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cards = request.Cards?.ToList() ?? [];
        cards = [.. DeepDiveCardOrdering.Apply(request.TopicKind, cards)];

        return new DeepDiveBriefing
        {
            Version = DeepDiveConstants.ContractVersion,
            Topic = new DeepDiveTopic
            {
                Kind = request.TopicKind,
                Query = request.Query,
                Timezone = string.IsNullOrWhiteSpace(request.Timezone) ? "unknown" : request.Timezone,
                Locale = string.IsNullOrWhiteSpace(request.Locale) ? "en-US" : request.Locale,
                UserLocationHint = request.UserLocationHint
            },
            Hero = new DeepDiveHero
            {
                Title = request.HeroTitle,
                Confidence = request.Confidence,
                LastCheckedIso = request.LastCheckedIso,
                StatusLine = request.StatusLine,
                ClosesText = request.ClosesText,
                Address = request.Address,
                Phone = request.Phone,
                Website = request.Website,
                DirectionsUrl = request.DirectionsUrl
            },
            Cards = cards,
            Map = request.Map,
            Audit = request.AuditSteps ?? []
        };
    }
}

public sealed record DeepDiveAssembleRequest
{
    public string TopicKind { get; init; } = DeepDiveConstants.KindPlace;
    public string Query { get; init; } = "";
    public string Timezone { get; init; } = "unknown";
    public string Locale { get; init; } = "en-US";
    public string? UserLocationHint { get; init; }

    public string HeroTitle { get; init; } = "";
    public string Confidence { get; init; } = DeepDiveConstants.ConfidenceLow;
    public string LastCheckedIso { get; init; } = DateTimeOffset.UtcNow.ToString("O");
    public string StatusLine { get; init; } = "";
    public string ClosesText { get; init; } = "";
    public string Address { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Website { get; init; } = "";
    public string DirectionsUrl { get; init; } = "";

    public DeepDiveMap? Map { get; init; }
    public IReadOnlyList<DeepDiveCard> Cards { get; init; } = [];
    public IReadOnlyList<DeepDiveAuditStep> AuditSteps { get; init; } = [];
}
