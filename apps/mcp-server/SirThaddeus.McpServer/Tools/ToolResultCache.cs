using SirThaddeus.Core.Caching;

namespace SirThaddeus.McpServer.Tools;

/// <summary>
/// Internal caching wrapper that checks <see cref="SirThaddeus.Core.Caching.IResultCache"/>
/// before executing tool calls, with environment-based enable/disable.
/// </summary>
internal static class ToolResultCache
{
    private static readonly Lazy<IResultCache> Cache = new(CreateCache);

    public static bool Enabled => ParseBoolEnv("ST_CACHE_ENABLED", fallback: true);

    public static async Task<T?> GetAsync<T>(string toolName, object? args)
    {
        if (!Enabled)
            return default;

        var key = CacheKeyBuilder.Build(toolName, args);
        return await Cache.Value.GetAsync<T>(key);
    }

    public static Task SetAsync<T>(string toolName, object? args, T value, TimeSpan ttl)
    {
        if (!Enabled)
            return Task.CompletedTask;

        var key = CacheKeyBuilder.Build(toolName, args);
        return Cache.Value.SetAsync(key, value, ttl);
    }

    public static TimeSpan ResolveWebSearchTtl() =>
        TimeSpan.FromMinutes(ParseIntEnv("ST_CACHE_WEBSEARCH_TTL_MINUTES", fallback: 15, min: 1, max: 120));

    public static TimeSpan ResolveWeatherTtl() =>
        TimeSpan.FromMinutes(ParseIntEnv("ST_CACHE_WEATHER_TTL_MINUTES", fallback: 60, min: 1, max: 720));

    public static TimeSpan ResolvePlacesAndHolidaysTtl() =>
        TimeSpan.FromHours(ParseIntEnv("ST_CACHE_PLACES_HOLIDAYS_TTL_HOURS", fallback: 24, min: 1, max: 720));

    private static IResultCache CreateCache()
    {
        var maxEntries = ParseIntEnv("ST_CACHE_MAX_ENTRIES", fallback: 500, min: 50, max: 5_000);
        return new InMemoryResultCache(maxEntries);
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

    private static int ParseIntEnv(string key, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (!int.TryParse(raw, out var parsed))
            return fallback;

        return Math.Clamp(parsed, min, max);
    }
}
