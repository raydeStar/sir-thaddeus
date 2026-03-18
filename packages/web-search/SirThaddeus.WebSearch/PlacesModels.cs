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

public sealed record PlacesCacheMetadata
{
    public bool Hit { get; init; }
    public int AgeSeconds { get; init; }
}

public sealed record PlaceDiscoveryOptions
{
    public int MaxResults { get; init; } = 10;
    public int RadiusMeters { get; init; } = 4_000;
    public string Locale { get; init; } = "en-US";
}

public sealed record PlacesDiscoveryCenter
{
    public string Label { get; init; } = "";
    public double Latitude { get; init; }
    public double Longitude { get; init; }
}

public sealed record PlaceDiscoveryCandidate
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Address { get; init; } = "";
    public string Category { get; init; } = "";
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public int? DistanceMeters { get; init; }
    public string OsmUrl { get; init; } = "";
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record PlacesDiscoveryResult
{
    public string Provider { get; init; } = "unknown";
    public string Query { get; init; } = "";
    public string UserLocationHint { get; init; } = "";
    public string ResolvedLocation { get; init; } = "";
    public PlacesDiscoveryCenter? Center { get; init; }
    public PlaceDiscoveryOptions Options { get; init; } = new();
    public IReadOnlyList<PlaceDiscoveryCandidate> Results { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public PlacesCacheMetadata Cache { get; init; } = new();
}
