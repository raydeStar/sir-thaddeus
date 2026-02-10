namespace SirThaddeus.WebSearch;

// ─────────────────────────────────────────────────────────────────────────
// Weather Models
//
// Shared DTOs for geocoding + weather forecast flows.
// Kept transport-friendly and deterministic for MCP tool outputs.
// ─────────────────────────────────────────────────────────────────────────

public sealed record WeatherServiceOptions
{
    /// <summary>
    /// Provider strategy:
    ///   - "nws_us_openmeteo_fallback" (default)
    ///   - "openmeteo_only"
    ///   - "nws_only_us"
    /// </summary>
    public string ProviderMode { get; init; } = "nws_us_openmeteo_fallback";

    /// <summary>Forecast cache TTL in minutes (clamped 10..30).</summary>
    public int ForecastCacheMinutes { get; init; } = 15;

    /// <summary>Geocode cache TTL in minutes (default 24h).</summary>
    public int GeocodeCacheMinutes { get; init; } = 1_440;

    /// <summary>Optional local place memory (user-approved only).</summary>
    public bool PlaceMemoryEnabled { get; init; }

    /// <summary>
    /// Path to local place memory JSON. Empty means disabled unless
    /// <see cref="PlaceMemoryEnabled"/> is false.
    /// </summary>
    public string PlaceMemoryPath { get; init; } = "";

    /// <summary>
    /// User-Agent used for weather/geocoding HTTP calls.
    /// NWS requires a non-empty identifying User-Agent.
    /// </summary>
    public string UserAgent { get; init; } =
        "SirThaddeusCopilot/1.0 (contact: local-runtime@localhost)";
}

public sealed record WeatherCacheMetadata
{
    public bool Hit { get; init; }
    public int AgeSeconds { get; init; }
}

public sealed record GeocodeCandidate
{
    public required string Name { get; init; }
    public string Region { get; init; } = "";
    public required string CountryCode { get; init; }
    public bool IsUs { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double Confidence { get; init; }
}

public sealed record GeocodeLookup
{
    public required string Query { get; init; }
    public IReadOnlyList<GeocodeCandidate> Results { get; init; } = [];
    public WeatherCacheMetadata Cache { get; init; } = new();
    public string Source { get; init; } = "photon";
}

public sealed record WeatherCurrent
{
    public int? Temperature { get; init; }
    public string Unit { get; init; } = "";
    public string Condition { get; init; } = "";
    public string Wind { get; init; } = "";
    public int? HumidityPercent { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
}

public sealed record WeatherDaily
{
    public DateOnly Date { get; init; }
    public int? TempHigh { get; init; }
    public int? TempLow { get; init; }
    public string Unit { get; init; } = "";
    public string Condition { get; init; } = "";
    public int? AvgTemp { get; init; }
}

public sealed record WeatherAlert
{
    public string Headline { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Event { get; init; } = "";
}

public sealed record WeatherLocation
{
    public string Name { get; init; } = "";
    public string CountryCode { get; init; } = "";
    public bool IsUs { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
}

public sealed record WeatherForecast
{
    public string Provider { get; init; } = "";
    public string ProviderReason { get; init; } = "";
    public WeatherLocation Location { get; init; } = new();
    public WeatherCurrent? Current { get; init; }
    public IReadOnlyList<WeatherDaily> Daily { get; init; } = [];
    public IReadOnlyList<WeatherAlert> Alerts { get; init; } = [];
    public WeatherCacheMetadata Cache { get; init; } = new();
}
