namespace SirThaddeus.WebSearch;

/// <summary>
/// Shared data contract for place lookup payloads crossing the MCP boundary.
/// Network calls belong in MCP tools; this package only defines shapes.
/// </summary>
public sealed record PlacesLookupResult
{
    public string Provider { get; init; } = "unknown";
    public string Query { get; init; } = "";
    public string FetchedAt { get; init; } = DateTimeOffset.UtcNow.ToString("O");
    public string? Error { get; init; }
    public PlacesPlaceDetails? Place { get; init; }
    public IReadOnlyList<PlacesSourceRef> Sources { get; init; } = [];
}

public sealed record PlacesPlaceDetails
{
    public string PlaceId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Address { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Website { get; init; } = "";
    public string DirectionsUrl { get; init; } = "";
    public double? Rating { get; init; }
    public int? UserRatingsTotal { get; init; }
    public bool? OpenNow { get; init; }
    public IReadOnlyList<string> WeekdayText { get; init; } = [];
    public IReadOnlyList<PlacesReviewSnippet> Reviews { get; init; } = [];
    public PlacesGeometry? Geometry { get; init; }
}

public sealed record PlacesReviewSnippet
{
    public string Author { get; init; } = "";
    public double? Rating { get; init; }
    public string Text { get; init; } = "";
    public string RelativeTimeDescription { get; init; } = "";
}

public sealed record PlacesGeometry
{
    public double Lat { get; init; }
    public double Lng { get; init; }
}

public sealed record PlacesSourceRef
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string FetchedIso { get; init; } = DateTimeOffset.UtcNow.ToString("O");
}
