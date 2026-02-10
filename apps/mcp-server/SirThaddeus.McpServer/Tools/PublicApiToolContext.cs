using SirThaddeus.WebSearch;

namespace SirThaddeus.McpServer.Tools;

internal static class PublicApiToolContext
{
    private static readonly Lazy<PublicApiServiceOptions> Options = new(CreateOptions);
    private static readonly Lazy<HttpClient> SharedHttp = new(CreateHttpClient);

    public static readonly Lazy<ITimezoneProvider> TimezoneProvider = new(
        () => new TimezoneProvider(Options.Value, SharedHttp.Value));

    public static readonly Lazy<IHolidaysProvider> HolidaysProvider = new(
        () => new NagerDateHolidaysProvider(Options.Value, SharedHttp.Value));

    public static readonly Lazy<IFeedProvider> FeedProvider = new(
        () => new FeedProvider(Options.Value, SharedHttp.Value));

    public static readonly Lazy<IStatusProbe> StatusProbe = new(
        () => new StatusProbe(Options.Value, SharedHttp.Value));

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient();
        // Long outer timeout; each provider applies tight linked CTS timeouts.
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }

    private static PublicApiServiceOptions CreateOptions()
    {
        var userAgent = Environment.GetEnvironmentVariable("ST_PUBLIC_API_USER_AGENT");
        return new PublicApiServiceOptions
        {
            UserAgent = string.IsNullOrWhiteSpace(userAgent)
                ? "SirThaddeusCopilot/1.0 (contact: local-runtime@localhost)"
                : userAgent.Trim(),
            RequestTimeoutMs = ParseIntEnv("ST_PUBLIC_TIMEOUT_MS", fallback: 8_000, min: 1_500, max: 25_000),
            TimezoneCacheMinutes = ParseIntEnv("ST_PUBLIC_TIMEZONE_CACHE_MINUTES", fallback: 10_080, min: 60, max: 43_200),
            HolidaysCacheMinutes = ParseIntEnv("ST_PUBLIC_HOLIDAYS_CACHE_MINUTES", fallback: 720, min: 30, max: 10_080),
            FeedCacheMinutes = ParseIntEnv("ST_PUBLIC_FEED_CACHE_MINUTES", fallback: 5, min: 1, max: 120),
            StatusCacheSeconds = ParseIntEnv("ST_PUBLIC_STATUS_CACHE_SECONDS", fallback: 45, min: 5, max: 600),
            FeedMaxBytes = ParseIntEnv("ST_PUBLIC_FEED_MAX_BYTES", fallback: 512 * 1024, min: 8 * 1024, max: 2 * 1024 * 1024),
            FeedSummaryMaxChars = ParseIntEnv("ST_PUBLIC_FEED_SUMMARY_MAX_CHARS", fallback: 320, min: 120, max: 1_200),
            BlockPrivateNetworkTargets = ParseBoolEnv("ST_PUBLIC_BLOCK_PRIVATE_TARGETS", fallback: true),
            MaxConcurrentRequestsPerProvider = ParseIntEnv("ST_PUBLIC_MAX_CONCURRENCY", fallback: 2, min: 1, max: 8),
            MinRequestSpacingMs = ParseIntEnv("ST_PUBLIC_MIN_REQUEST_SPACING_MS", fallback: 250, min: 0, max: 5_000)
        };
    }

    private static int ParseIntEnv(string key, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (!int.TryParse(raw, out var parsed))
            return fallback;
        return Math.Clamp(parsed, min, max);
    }

    private static bool ParseBoolEnv(string key, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return raw?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };
    }
}
