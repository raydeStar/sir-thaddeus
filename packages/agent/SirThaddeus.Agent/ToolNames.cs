namespace SirThaddeus.Agent;

/// <summary>
/// Canonical MCP tool name constants shared across the orchestrator,
/// utility handlers, and policy gates.
///
/// MCP stacks may register tools in snake_case or PascalCase.
/// Each tool has a primary (snake_case) and alternate (PascalCase) name.
/// </summary>
public static class ToolNames
{
    public const string WebSearch           = "web_search";
    public const string WebSearchAlt        = "WebSearch";
    public const string WeatherGeocode      = "weather_geocode";
    public const string WeatherGeocodeAlt   = "WeatherGeocode";
    public const string WeatherForecast     = "weather_forecast";
    public const string WeatherForecastAlt  = "WeatherForecast";
    public const string ResolveTimezone     = "resolve_timezone";
    public const string ResolveTimezoneAlt  = "ResolveTimezone";
    public const string HolidaysGet         = "holidays_get";
    public const string HolidaysGetAlt      = "HolidaysGet";
    public const string HolidaysNext        = "holidays_next";
    public const string HolidaysNextAlt     = "HolidaysNext";
    public const string HolidaysIsToday     = "holidays_is_today";
    public const string HolidaysIsTodayAlt  = "HolidaysIsToday";
    public const string FeedFetch           = "feed_fetch";
    public const string FeedFetchAlt        = "FeedFetch";
    public const string StatusCheck         = "status_check_url";
    public const string StatusCheckAlt      = "StatusCheckUrl";
    public const string MemoryRetrieve      = "memory_retrieve";
    public const string MemoryRetrieveAlt   = "MemoryRetrieve";
    public const string MemoryListFacts     = "memory_list_facts";
    public const string MemoryListFactsAlt  = "MemoryListFacts";
    public const string MemoryStoreFacts    = "memory_store_facts";
    public const string MemoryStoreFactsAlt = "MemoryStoreFacts";
    public const string ScreenCapture       = "screen_capture";
    public const string ScreenCaptureAlt    = "ScreenCapture";
}
