namespace SirThaddeus.WebSearch;

// ─────────────────────────────────────────────────────────────────────────
// Public APIs Models (Zero-Setup Suite)
//
// Shared DTOs + options for timezone, holiday, feed, and status providers.
// All records are bounded/serializable for deterministic MCP transport.
// ─────────────────────────────────────────────────────────────────────────

public sealed record PublicApiServiceOptions
{
    /// <summary>
    /// User-Agent sent to upstream public endpoints.
    /// Keep non-empty to satisfy polite-use requirements.
    /// </summary>
    public string UserAgent { get; init; } =
        "SirThaddeusCopilot/1.0 (contact: local-runtime@localhost)";

    /// <summary>Default per-request timeout in milliseconds.</summary>
    public int RequestTimeoutMs { get; init; } = 8_000;

    /// <summary>Timezone cache TTL in minutes (long-lived; default 7 days).</summary>
    public int TimezoneCacheMinutes { get; init; } = 10_080;

    /// <summary>Holiday cache TTL in minutes (default 12 hours).</summary>
    public int HolidaysCacheMinutes { get; init; } = 720;

    /// <summary>Feed cache TTL in minutes (default 5 minutes).</summary>
    public int FeedCacheMinutes { get; init; } = 5;

    /// <summary>Status cache TTL in seconds (default 45 seconds).</summary>
    public int StatusCacheSeconds { get; init; } = 45;

    /// <summary>
    /// Max bytes accepted for feed XML payloads.
    /// 512 KB is enough for normal RSS/Atom feeds while staying bounded.
    /// </summary>
    public int FeedMaxBytes { get; init; } = 512 * 1024;

    /// <summary>Max summary length retained per feed item.</summary>
    public int FeedSummaryMaxChars { get; init; } = 320;

    /// <summary>Whether feed/status providers should block private network targets.</summary>
    public bool BlockPrivateNetworkTargets { get; init; } = true;

    /// <summary>Max concurrent outbound requests per provider.</summary>
    public int MaxConcurrentRequestsPerProvider { get; init; } = 2;

    /// <summary>Minimum spacing between outbound requests per provider.</summary>
    public int MinRequestSpacingMs { get; init; } = 250;
}

public sealed record PublicApiCacheMetadata
{
    public bool Hit { get; init; }
    public int AgeSeconds { get; init; }
}

public sealed record TimezoneResolution
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string Timezone { get; init; } = "";
    public string Source { get; init; } = "unknown";
    public PublicApiCacheMetadata Cache { get; init; } = new();
}

public sealed record HolidayEntry
{
    public DateOnly Date { get; init; }
    public string LocalName { get; init; } = "";
    public string Name { get; init; } = "";
    public string CountryCode { get; init; } = "";
    public bool Global { get; init; }
    public int? LaunchYear { get; init; }
    public IReadOnlyList<string> Counties { get; init; } = [];
    public IReadOnlyList<string> Types { get; init; } = [];
}

public sealed record HolidaySetResult
{
    public string CountryCode { get; init; } = "";
    public string? RegionCode { get; init; }
    public int Year { get; init; }
    public IReadOnlyList<HolidayEntry> Holidays { get; init; } = [];
    public string Source { get; init; } = "nager-date";
    public PublicApiCacheMetadata Cache { get; init; } = new();
}

public sealed record HolidayTodayResult
{
    public string CountryCode { get; init; } = "";
    public string? RegionCode { get; init; }
    public DateOnly Date { get; init; }
    public bool IsPublicHoliday { get; init; }
    public IReadOnlyList<HolidayEntry> HolidaysToday { get; init; } = [];
    public HolidayEntry? NextHoliday { get; init; }
    public string Source { get; init; } = "nager-date";
    public PublicApiCacheMetadata Cache { get; init; } = new();
}

public sealed record HolidayNextResult
{
    public string CountryCode { get; init; } = "";
    public string? RegionCode { get; init; }
    public IReadOnlyList<HolidayEntry> Holidays { get; init; } = [];
    public string Source { get; init; } = "nager-date";
    public PublicApiCacheMetadata Cache { get; init; } = new();
}

public sealed record FeedItem
{
    public string Title { get; init; } = "";
    public string Link { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTimeOffset? PublishedAt { get; init; }
}

public sealed record FeedSnapshot
{
    public string Url { get; init; } = "";
    public string FeedTitle { get; init; } = "";
    public string Description { get; init; } = "";
    public string SourceHost { get; init; } = "";
    public string Source { get; init; } = "rss";
    public bool Truncated { get; init; }
    public IReadOnlyList<FeedItem> Items { get; init; } = [];
    public PublicApiCacheMetadata Cache { get; init; } = new();
}

public sealed record StatusProbeResult
{
    public string Url { get; init; } = "";
    public bool Reachable { get; init; }
    public int? HttpStatus { get; init; }
    public string Method { get; init; } = "";
    public int LatencyMs { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
    public string Source { get; init; } = "direct";
    public PublicApiCacheMetadata Cache { get; init; } = new();
}
