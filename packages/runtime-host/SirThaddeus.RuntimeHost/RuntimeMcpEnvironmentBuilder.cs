using SirThaddeus.AuditLog;
using SirThaddeus.Config;

namespace SirThaddeus.RuntimeHost;

public static class RuntimeMcpEnvironmentBuilder
{
    public static Dictionary<string, string> Build(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ST_ACTIVE_PROFILE_ID"] = settings.ActiveProfileId ?? "",
            ["ST_ACTIVE_PERSONALITY_ID"] = settings.ActivePersonalityId ?? "",
            ["ST_SETTINGS_PATH"] = ResolveInheritedOrDefault(
                "ST_SETTINGS_PATH",
                SettingsManager.GetSettingsPath()),
            ["ST_AUDIT_PATH"] = ResolveInheritedOrDefault(
                "ST_AUDIT_PATH",
                JsonLineAuditLogger.GetDefaultPath())
        };

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDataDir = Path.Combine(localAppData, "SirThaddeus");
        env["ST_CHAT_HISTORY_PATH"] = Path.Combine(appDataDir, "chat-history.json");
        env["ST_BRIEFING_HISTORY_PATH"] = Path.Combine(appDataDir, "briefing-history.json");

        if (settings.Memory.Enabled)
        {
            env["ST_MEMORY_DB_PATH"] = ResolveMemoryDbPath(settings.Memory.DbPath);
            env["ST_LLM_BASEURL"] = settings.Llm.BaseUrl;

            if (settings.Memory.UseEmbeddings)
            {
                var embModel = string.IsNullOrWhiteSpace(settings.Memory.EmbeddingsModel)
                    ? settings.Llm.Model
                    : settings.Memory.EmbeddingsModel;
                env["ST_LLM_EMBEDDINGS_MODEL"] = embModel;
            }
        }

        env["ST_WEATHER_PROVIDER_MODE"] = settings.Weather.ProviderMode;
        env["ST_WEATHER_FORECAST_CACHE_MINUTES"] =
            Math.Clamp(settings.Weather.ForecastCacheMinutes, 10, 30).ToString();
        env["ST_WEATHER_GEOCODE_CACHE_MINUTES"] =
            Math.Max(60, settings.Weather.GeocodeCacheMinutes).ToString();
        env["ST_WEATHER_PLACE_MEMORY_ENABLED"] =
            settings.Weather.PlaceMemoryEnabled ? "true" : "false";
        env["ST_WEATHER_PLACE_MEMORY_PATH"] = ResolveWeatherPlaceMemoryPath(settings.Weather.PlaceMemoryPath);
        env["ST_WEATHER_USER_AGENT"] =
            string.IsNullOrWhiteSpace(settings.Weather.UserAgent)
                ? "SirThaddeusCopilot/1.0 (contact: local-runtime@localhost)"
                : settings.Weather.UserAgent.Trim();

        var webModeRaw = (settings.WebSearch.Mode ?? "auto").Trim().ToLowerInvariant();
        var webMode = webModeRaw switch
        {
            "api" => "search_api",
            "search_api" => "search_api",
            "auto" or "searxng" or "ddg_html" or "google_news" or "manual" => webModeRaw,
            _ => "auto"
        };
        env["WEBSEARCH_MODE"] = webMode;
        env["WEBSEARCH_SEARXNG_URL"] = string.IsNullOrWhiteSpace(settings.WebSearch.SearxngBaseUrl)
            ? "http://localhost:8080"
            : settings.WebSearch.SearxngBaseUrl.Trim();
        if (!string.IsNullOrWhiteSpace(settings.WebSearch.SearchApiProvider))
            env["WEBSEARCH_API_PROVIDER"] = settings.WebSearch.SearchApiProvider.Trim();
        if (!string.IsNullOrWhiteSpace(settings.WebSearch.SearchApiKey))
            env["WEBSEARCH_API_KEY"] = settings.WebSearch.SearchApiKey.Trim();
        if (!string.IsNullOrWhiteSpace(settings.WebSearch.SearchApiBaseUrl))
            env["WEBSEARCH_API_BASE_URL"] = settings.WebSearch.SearchApiBaseUrl.Trim();
        if (!string.IsNullOrWhiteSpace(settings.WebSearch.SearchApiEngine))
            env["WEBSEARCH_API_ENGINE"] = settings.WebSearch.SearchApiEngine.Trim();
        env["WEBSEARCH_TIMEOUT_MS"] = Math.Clamp(settings.WebSearch.TimeoutMs, 2_000, 30_000).ToString();
        env["WEBSEARCH_MAX_RESULTS"] = Math.Clamp(settings.WebSearch.MaxResults, 1, 20).ToString();

        env["ST_CACHE_ENABLED"] = settings.Cache.Enabled ? "true" : "false";
        env["ST_CACHE_WEBSEARCH_TTL_MINUTES"] =
            Math.Clamp(settings.Cache.WebSearchTtlMinutes, 1, 120).ToString();
        env["ST_CACHE_WEATHER_TTL_MINUTES"] =
            Math.Clamp(settings.Cache.WeatherTtlMinutes, 1, 720).ToString();
        env["ST_CACHE_PLACES_HOLIDAYS_TTL_HOURS"] =
            Math.Clamp(settings.Cache.PlacesAndHolidaysTtlHours, 1, 720).ToString();
        env["ST_CACHE_MAX_ENTRIES"] =
            Math.Clamp(settings.Cache.MaxEntries, 50, 5_000).ToString();

        env["ST_DOCUMENT_READER_MAX_DEFAULT_CHARS"] =
            Math.Clamp(settings.DocumentReader.MaxDefaultChars, 100, 100_000).ToString();
        env["ST_DOCUMENT_READER_DISABLE_FILE_ACCESS"] = settings.DocumentReader.DisableAllFileAccess ? "true" : "false";
        env["ST_DOCUMENT_READER_ALLOWED_ROOTS"] =
            string.Join(Path.PathSeparator, ResolveDocumentReaderAllowedRoots(settings.DocumentReader));
        env["ST_DOCUMENT_READER_ALLOWED_EXTENSIONS"] =
            string.Join(",", settings.DocumentReader.AllowedExtensions);
        env["ST_CLIPBOARD_ENABLED"] = settings.Clipboard.Enabled ? "true" : "false";

        if (!string.IsNullOrWhiteSpace(settings.DeepDive.PlacesApiKey))
            env["ST_DEEPDIVE_PLACES_API_KEY"] = settings.DeepDive.PlacesApiKey.Trim();
        env["ST_DEEPDIVE_PLACES_TIMEOUT_MS"] = Math.Clamp(settings.DeepDive.PlacesTimeoutMs, 2_000, 20_000).ToString();
        env["ST_DEEPDIVE_MAX_TOOL_CALLS"] = Math.Clamp(settings.DeepDive.MaxToolCalls, 1, 20).ToString();
        env["ST_DEEPDIVE_MAX_SOURCES"] = Math.Clamp(settings.DeepDive.MaxSources, 1, 10).ToString();
        env["ST_DEEPDIVE_REVIEW_SNIPPETS_MAX"] = Math.Clamp(settings.DeepDive.MaxReviewSnippets, 1, 5).ToString();
        env["ST_DEEPDIVE_DEFAULT_LOCALE"] = string.IsNullOrWhiteSpace(settings.DeepDive.DefaultLocale)
            ? "en-US"
            : settings.DeepDive.DefaultLocale.Trim();

        return env;
    }

    private static string ResolveInheritedOrDefault(string variableName, string defaultValue)
    {
        var inherited = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(inherited)
            ? defaultValue
            : inherited.Trim();
    }

    public static bool HasChanged(AppSettings? previous, AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (previous is null)
            return true;

        var before = Build(previous);
        var after = Build(current);

        if (before.Count != after.Count)
            return true;

        foreach (var (key, value) in before)
        {
            if (!after.TryGetValue(key, out var nextValue))
                return true;
            if (!string.Equals(value, nextValue, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static string ResolveMemoryDbPath(string dbPath)
    {
        if (!string.Equals(dbPath, "auto", StringComparison.OrdinalIgnoreCase))
            return dbPath;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "SirThaddeus", "memory.db");
    }

    public static string ResolveMemoryDbPathFromEnvironment()
    {
        var dbPath = Environment.GetEnvironmentVariable("ST_MEMORY_DB_PATH");
        return ResolveMemoryDbPath(string.IsNullOrWhiteSpace(dbPath) ? "auto" : dbPath.Trim());
    }

    public static string ResolveWeatherPlaceMemoryPath(string weatherPlaceMemoryPath)
    {
        if (!string.Equals(weatherPlaceMemoryPath, "auto", StringComparison.OrdinalIgnoreCase))
            return weatherPlaceMemoryPath;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "SirThaddeus", "weather-places.json");
    }

    public static IReadOnlyList<string> ResolveDocumentReaderAllowedRoots(DocumentReaderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.DisableAllFileAccess)
            return settings.AllowedRoots;

        if (settings.AllowedRoots.Count > 0)
            return settings.AllowedRoots;

        return [ResolveDocumentReaderTempWorkspaceRoot()];
    }

    public static string ResolveDocumentReaderTempWorkspaceRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "SirThaddeus", "file-workspace");
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }
}
