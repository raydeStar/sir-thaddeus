namespace SirThaddeus.Agent.Search.DeepDive;

/// <summary>
/// Deterministic payload for deep-dive briefing UI rendering.
/// Contract-first shape: avoid freeform prose fields outside cards/hero.
/// </summary>
public sealed record DeepDiveBriefing
{
    /// <summary>
    /// Contract version. Starts at 1 and must match validator expectations.
    /// </summary>
    public int Version { get; init; } = 1;

    public required DeepDiveTopic Topic { get; init; }
    public required DeepDiveHero Hero { get; init; }
    public IReadOnlyList<DeepDiveCard> Cards { get; init; } = [];
    public DeepDiveMap? Map { get; init; }
    public IReadOnlyList<DeepDiveAuditStep> Audit { get; init; } = [];
}

public sealed record DeepDiveTopic
{
    /// <summary>
    /// Topic kind: place | product.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Original user query / normalized lookup query.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Timezone used for hours interpretation (IANA or Windows ID).
    /// </summary>
    public string Timezone { get; init; } = "";

    /// <summary>
    /// Locale used for formatting assumptions.
    /// </summary>
    public string Locale { get; init; } = "";

    /// <summary>
    /// Optional user location hint that influenced provider selection.
    /// </summary>
    public string? UserLocationHint { get; init; }
}

public sealed record DeepDiveHero
{
    public required string Title { get; init; }

    /// <summary>
    /// high | medium | low
    /// </summary>
    public required string Confidence { get; init; }

    /// <summary>
    /// ISO-8601 timestamp of latest provider check.
    /// </summary>
    public required string LastCheckedIso { get; init; }

    /// <summary>
    /// Open now / Closed now / Unknown status.
    /// </summary>
    public string StatusLine { get; init; } = "";

    /// <summary>
    /// Human-friendly close/open line, e.g. "Today: 8:00 AM - 6:00 PM".
    /// </summary>
    public string ClosesText { get; init; } = "";

    public string Address { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Website { get; init; } = "";
    public string DirectionsUrl { get; init; } = "";
}

public sealed record DeepDiveCard
{
    /// <summary>
    /// Card type: hours | reviews | summary | links | alternatives | warnings
    /// </summary>
    public required string Type { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<string> Bullets { get; init; } = [];
    public IReadOnlyList<SourceRef> Sources { get; init; } = [];
}

public sealed record SourceRef
{
    public required string Name { get; init; }
    public required string Url { get; init; }
    public required string FetchedIso { get; init; }
}

public sealed record DeepDiveAuditStep
{
    /// <summary>
    /// Step key: search | details_fetch | open_page | extract | summarize | assemble
    /// </summary>
    public required string Step { get; init; }

    /// <summary>
    /// Small deterministic summary of what happened in this step.
    /// </summary>
    public required string Detail { get; init; }

    public required string TimestampIso { get; init; }

    public IReadOnlyList<SourceRef> Sources { get; init; } = [];
}

public sealed record DeepDiveMap
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string Label { get; init; } = "";
}

public static class DeepDiveConstants
{
    public const int ContractVersion = 1;

    public const string KindPlace = "place";
    public const string KindProduct = "product";

    public const string ConfidenceHigh = "high";
    public const string ConfidenceMedium = "medium";
    public const string ConfidenceLow = "low";
}
