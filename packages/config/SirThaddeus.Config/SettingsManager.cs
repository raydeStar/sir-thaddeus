using System.Text.Json;

namespace SirThaddeus.Config;

/// <summary>
/// Reads and writes the application settings file.
/// Creates a default settings file on first run.
/// </summary>
public static class SettingsManager
{
    private const string BootstrapDefaultProfileId = "user-john-doe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Gets the default settings directory under %LOCALAPPDATA%.
    /// </summary>
    public static string GetSettingsDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "SirThaddeus");
    }

    /// <summary>
    /// Gets the full path to settings.json.
    /// </summary>
    public static string GetSettingsPath() =>
        Path.Combine(GetSettingsDirectory(), "settings.json");

    /// <summary>
    /// Canonical personality profile directory for Windows builds.
    /// </summary>
    public static string GetPersonalityProfilesDirectory() =>
        Path.Combine(GetSettingsDirectory(), "profiles");

    /// <summary>
    /// Legacy cross-platform profile directory used for migration reads.
    /// </summary>
    public static string GetLegacyPersonalityProfilesDirectory()
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userHome))
            return Path.Combine(GetSettingsDirectory(), "profiles");

        return Path.Combine(userHome, ".sir-thaddeus", "profiles");
    }

    /// <summary>
    /// Resolves the active profile directory. Empty override values
    /// resolve to the canonical local app data path.
    /// </summary>
    public static string ResolvePersonalityProfilesDirectory(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!string.IsNullOrWhiteSpace(settings.PersonalityProfilesDir))
            return settings.PersonalityProfilesDir.Trim();

        var canonical = GetPersonalityProfilesDirectory();
        var legacy = GetLegacyPersonalityProfilesDirectory();

        try
        {
            var canonicalHasFiles =
                Directory.Exists(canonical) &&
                Directory.EnumerateFiles(canonical, "*.json", SearchOption.TopDirectoryOnly).Any();

            var legacyHasFiles =
                Directory.Exists(legacy) &&
                Directory.EnumerateFiles(legacy, "*.json", SearchOption.TopDirectoryOnly).Any();

            if (!canonicalHasFiles && legacyHasFiles)
            {
                Directory.CreateDirectory(canonical);
                foreach (var file in Directory.EnumerateFiles(legacy, "*.json", SearchOption.TopDirectoryOnly))
                {
                    var target = Path.Combine(canonical, Path.GetFileName(file));
                    if (!File.Exists(target))
                        File.Copy(file, target);
                }
            }
        }
        catch
        {
            // Keep settings resolution resilient. If migration fails,
            // callers still receive the canonical path.
        }

        return canonical;
    }

    /// <summary>
    /// Loads settings from disk, creating defaults if the file doesn't exist.
    /// </summary>
    /// <returns>The loaded (or newly created) settings.</returns>
    public static AppSettings Load()
        => LoadWithDiagnostics().Settings;

    /// <summary>
    /// Loads settings with migration/corruption diagnostics for safe-mode boot.
    /// </summary>
    public static SettingsLoadResult LoadWithDiagnostics()
    {
        var path = GetSettingsPath();

        if (!File.Exists(path))
        {
            var defaults = Normalize(new AppSettings()) with
            {
                ActiveProfileId = BootstrapDefaultProfileId
            };
            Save(defaults);
            return new SettingsLoadResult
            {
                Settings = defaults,
                CreatedDefaults = true
            };
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                         ?? new AppSettings();

            var requiresMigration =
                loaded.SchemaVersion <= 0 ||
                loaded.SchemaVersion < AppSettings.CurrentSchemaVersion;

            var normalized = Normalize(loaded);
            if (requiresMigration)
            {
                // v2 → v3: Raise web-pull budget from old default (3) to new (8)
                // so the local-business enrichment pipeline has room for
                // places_lookup calls after web_search + article fetches.
                if (loaded.SchemaVersion <= 2 &&
                    normalized.ToolBudgets.MaxWebPullsPerTurn == 3)
                {
                    normalized = normalized with
                    {
                        ToolBudgets = normalized.ToolBudgets with
                        {
                            MaxWebPullsPerTurn = 8
                        }
                    };
                }

                normalized = normalized with
                {
                    SchemaVersion = AppSettings.CurrentSchemaVersion
                };
            }

            string? safeModeReason = null;
            if (loaded.SchemaVersion > AppSettings.CurrentSchemaVersion)
            {
                // Future schema detected: run fail-closed until user updates.
                safeModeReason = $"unsupported_settings_schema_v{loaded.SchemaVersion}";
                normalized = normalized with
                {
                    RuntimeSafety = normalized.RuntimeSafety with
                    {
                        SafeMode = true,
                        SafeModeReason = safeModeReason,
                        SafeModeSinceUtc = DateTimeOffset.UtcNow.ToString("O")
                    }
                };
            }

            if (requiresMigration || safeModeReason is not null)
                Save(normalized);

            return new SettingsLoadResult
            {
                Settings = normalized,
                MigratedSchema = requiresMigration,
                SafeModeReason = safeModeReason
            };
        }
        catch (JsonException)
        {
            // Corrupted file; recreate with defaults.
            var defaults = Normalize(new AppSettings()) with
            {
                ActiveProfileId = BootstrapDefaultProfileId,
                RuntimeSafety = new RuntimeSafetySettings
                {
                    SafeMode = true,
                    SafeModeReason = "settings_json_corrupt",
                    SafeModeSinceUtc = DateTimeOffset.UtcNow.ToString("O")
                }
            };
            Save(defaults);
            return new SettingsLoadResult
            {
                Settings = defaults,
                RecoveredFromCorruption = true,
                SafeModeReason = "settings_json_corrupt"
            };
        }
    }

    /// <summary>
    /// Persists settings to disk.
    /// </summary>
    public static void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var dir = GetSettingsDirectory();
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(Normalize(settings), JsonOptions);
        File.WriteAllText(GetSettingsPath(), json);
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        var defaults = new AppSettings();

        var llm = settings.Llm is null ? defaults.Llm : settings.Llm;
        var audio = settings.Audio is null ? defaults.Audio : settings.Audio;
        var voice = settings.Voice is null ? defaults.Voice : settings.Voice;
        var ui = settings.Ui is null ? defaults.Ui : settings.Ui;
        var mcp = settings.Mcp is null ? defaults.Mcp : settings.Mcp;
        var mcpPerms = mcp.Permissions is null ? defaults.Mcp.Permissions : mcp.Permissions;
        var webSearch = settings.WebSearch is null ? defaults.WebSearch : settings.WebSearch;
        var weather = settings.Weather is null ? defaults.Weather : settings.Weather;
        var deepDive = settings.DeepDive is null ? defaults.DeepDive : settings.DeepDive;
        var memory = settings.Memory is null ? defaults.Memory : settings.Memory;
        var dialogue = settings.Dialogue is null ? defaults.Dialogue : settings.Dialogue;
        var runtimeSafety = settings.RuntimeSafety is null ? defaults.RuntimeSafety : settings.RuntimeSafety;
        var toolBudgets = settings.ToolBudgets is null ? defaults.ToolBudgets : settings.ToolBudgets;
        var userProfile = settings.UserProfile is null ? defaults.UserProfile : settings.UserProfile;

        var normalizedBudgets = toolBudgets.Normalize();
        var safeReason = runtimeSafety.SafeMode
            ? (runtimeSafety.SafeModeReason ?? "").Trim()
            : "";

        var safeSince = runtimeSafety.SafeMode
            ? string.IsNullOrWhiteSpace(runtimeSafety.SafeModeSinceUtc)
                ? DateTimeOffset.UtcNow.ToString("O")
                : runtimeSafety.SafeModeSinceUtc.Trim()
            : "";

        var normalizedRootLocation = NormalizeLocation(settings.Location, defaults.Location);
        var normalizedUserLocation = NormalizeLocation(userProfile.Location, normalizedRootLocation);
        var normalizedLocationsByProfile = NormalizeLocationsByProfile(
            userProfile.LocationsByProfile,
            normalizedRootLocation);
        var normalizedAliasesByProfile = NormalizeAliasesByProfile(userProfile.AliasesByProfile);

        var normalizedActiveProfileId = string.IsNullOrWhiteSpace(settings.ActiveProfileId)
            ? null
            : settings.ActiveProfileId.Trim();

        return settings with
        {
            SchemaVersion = settings.SchemaVersion <= 0
                ? AppSettings.CurrentSchemaVersion
                : settings.SchemaVersion,
            Llm = llm with
            {
                BaseUrl = StringOrFallback(llm.BaseUrl, defaults.Llm.BaseUrl),
                Model = StringOrFallback(llm.Model, defaults.Llm.Model),
                MaxTokens = IntOrFallback(llm.MaxTokens, defaults.Llm.MaxTokens, min: 128, max: 65_536),
                ContextWindowTokens = IntOrFallback(llm.ContextWindowTokens, defaults.Llm.ContextWindowTokens, min: 1024, max: 1_000_000),
                Temperature = DoubleOrFallback(llm.Temperature, defaults.Llm.Temperature, min: 0.0, max: 2.0),
                SystemPrompt = StringOrFallback(llm.SystemPrompt, defaults.Llm.SystemPrompt)
            },
            Audio = audio with
            {
                PttKey = StringOrFallback(audio.PttKey, defaults.Audio.PttKey),
                PttChord = StringOrFallback(audio.PttChord, defaults.Audio.PttChord),
                ShutupChord = StringOrFallback(audio.ShutupChord, defaults.Audio.ShutupChord),
                InputDeviceName = OptionalString(audio.InputDeviceName),
                OutputDeviceName = OptionalString(audio.OutputDeviceName),
                InputGain = DoubleOrFallback(audio.InputGain, defaults.Audio.InputGain, min: 0.0, max: 2.0)
            },
            Voice = voice with
            {
                VoiceHostBaseUrl = StringOrFallback(voice.VoiceHostBaseUrl, defaults.Voice.VoiceHostBaseUrl),
                VoiceHostHealthPath = EnsureSlashPath(StringOrFallback(voice.VoiceHostHealthPath, defaults.Voice.VoiceHostHealthPath)),
                VoiceHostStartupTimeoutMs = IntOrFallback(
                    voice.VoiceHostStartupTimeoutMs,
                    defaults.Voice.VoiceHostStartupTimeoutMs,
                    min: 30_000,
                    max: 300_000),
                TtsEngine = NormalizeTtsEngine(voice.TtsEngine, defaults.Voice.TtsEngine),
                TtsModelId = OptionalString(voice.TtsModelId),
                TtsVoiceId = OptionalString(voice.TtsVoiceId),
                SttEngine = NormalizeSttEngine(voice.SttEngine, defaults.Voice.SttEngine),
                SttModelId = voice.GetResolvedSttModelId(),
                SttLanguage = voice.GetResolvedSttLanguage(),
                AsrEndpoint = OptionalString(voice.AsrEndpoint),
                TtsEndpoint = OptionalString(voice.TtsEndpoint),
                AsrTimeoutMs = IntOrFallback(voice.AsrTimeoutMs, defaults.Voice.AsrTimeoutMs, min: 5_000, max: 600_000),
                AgentTimeoutMs = IntOrFallback(voice.AgentTimeoutMs, defaults.Voice.AgentTimeoutMs, min: 10_000, max: 600_000),
                SpeakingTimeoutMs = IntOrFallback(voice.SpeakingTimeoutMs, defaults.Voice.SpeakingTimeoutMs, min: 10_000, max: 600_000),
                YouTubeAsrProvider = voice.GetResolvedYouTubeAsrProvider(),
                YouTubeAsrModelId = voice.GetResolvedYouTubeAsrModelId(),
                YouTubeLanguageHint = voice.GetResolvedYouTubeLanguageHint(),
                YouTubeDraftTone = voice.GetResolvedYouTubeDraftTone()
            },
            Ui = ui with
            {
                ReasoningGuardrails = NormalizeReasoningGuardrails(ui.ReasoningGuardrails, defaults.Ui.ReasoningGuardrails)
            },
            Mcp = mcp with
            {
                ServerPath = StringOrFallback(mcp.ServerPath, defaults.Mcp.ServerPath),
                Permissions = mcpPerms with
                {
                    DeveloperOverride = NormalizeDeveloperOverride(mcpPerms.DeveloperOverride),
                    Screen = NormalizePolicy(mcpPerms.Screen),
                    Files = NormalizePolicy(mcpPerms.Files),
                    System = NormalizePolicy(mcpPerms.System),
                    Web = NormalizePolicy(mcpPerms.Web),
                    MemoryRead = NormalizePolicy(mcpPerms.MemoryRead),
                    MemoryWrite = NormalizePolicy(mcpPerms.MemoryWrite)
                }
            },
            WebSearch = webSearch with
            {
                Mode = NormalizeWebSearchMode(webSearch.Mode, defaults.WebSearch.Mode),
                SearxngBaseUrl = StringOrFallback(webSearch.SearxngBaseUrl, defaults.WebSearch.SearxngBaseUrl),
                SearxngLaunchCommand = StringOrFallback(webSearch.SearxngLaunchCommand, defaults.WebSearch.SearxngLaunchCommand),
                SearxngLaunchArguments = StringOrFallback(webSearch.SearxngLaunchArguments, defaults.WebSearch.SearxngLaunchArguments),
                SearxngStartupTimeoutMs = IntOrFallback(
                    webSearch.SearxngStartupTimeoutMs,
                    defaults.WebSearch.SearxngStartupTimeoutMs,
                    min: 2_000,
                    max: 180_000),
                SearchApiProvider = NormalizeSearchApiProvider(webSearch.SearchApiProvider, defaults.WebSearch.SearchApiProvider),
                SearchApiKey = OptionalString(webSearch.SearchApiKey),
                SearchApiBaseUrl = StringOrFallback(webSearch.SearchApiBaseUrl, defaults.WebSearch.SearchApiBaseUrl),
                SearchApiEngine = NormalizeSearchApiEngine(webSearch.SearchApiEngine, defaults.WebSearch.SearchApiEngine),
                TimeoutMs = IntOrFallback(webSearch.TimeoutMs, defaults.WebSearch.TimeoutMs, min: 2_000, max: 30_000),
                MaxResults = IntOrFallback(webSearch.MaxResults, defaults.WebSearch.MaxResults, min: 1, max: 10)
            },
            Weather = weather with
            {
                ProviderMode = NormalizeWeatherProviderMode(weather.ProviderMode, defaults.Weather.ProviderMode),
                ForecastCacheMinutes = IntOrFallback(
                    weather.ForecastCacheMinutes,
                    defaults.Weather.ForecastCacheMinutes,
                    min: 10,
                    max: 30),
                GeocodeCacheMinutes = Math.Max(60, IntOrFallback(
                    weather.GeocodeCacheMinutes,
                    defaults.Weather.GeocodeCacheMinutes,
                    min: 60,
                    max: int.MaxValue)),
                PlaceMemoryPath = StringOrFallback(weather.PlaceMemoryPath, defaults.Weather.PlaceMemoryPath),
                UserAgent = StringOrFallback(weather.UserAgent, defaults.Weather.UserAgent),
                PreferredUnits = weather.GetNormalizedUnitSystem()
            },
            DeepDive = deepDive with
            {
                PlacesApiKey = OptionalString(deepDive.PlacesApiKey),
                PlacesTimeoutMs = IntOrFallback(deepDive.PlacesTimeoutMs, defaults.DeepDive.PlacesTimeoutMs, min: 2_000, max: 20_000),
                MaxToolCalls = IntOrFallback(deepDive.MaxToolCalls, defaults.DeepDive.MaxToolCalls, min: 1, max: 20),
                MaxSources = IntOrFallback(deepDive.MaxSources, defaults.DeepDive.MaxSources, min: 1, max: 10),
                MaxReviewSnippets = IntOrFallback(deepDive.MaxReviewSnippets, defaults.DeepDive.MaxReviewSnippets, min: 1, max: 5),
                DefaultLocale = StringOrFallback(deepDive.DefaultLocale, defaults.DeepDive.DefaultLocale)
            },
            Memory = memory with
            {
                DbPath = StringOrFallback(memory.DbPath, defaults.Memory.DbPath),
                EmbeddingsModel = OptionalString(memory.EmbeddingsModel)
            },
            Dialogue = dialogue with
            {
                GeocodeMismatchMode = NormalizeGeocodeMismatchMode(dialogue.GeocodeMismatchMode, defaults.Dialogue.GeocodeMismatchMode),
                PersistencePath = StringOrFallback(dialogue.PersistencePath, defaults.Dialogue.PersistencePath)
            },
            Location = normalizedRootLocation,
            UserProfile = userProfile with
            {
                Location = normalizedUserLocation,
                LocationsByProfile = normalizedLocationsByProfile,
                AliasesByProfile = normalizedAliasesByProfile
            },
            ActiveProfileId = normalizedActiveProfileId,
            ActivePersonalityId = StringOrFallback(settings.ActivePersonalityId, defaults.ActivePersonalityId),
            PersonalityProfilesDir = StringOrFallback(settings.PersonalityProfilesDir, GetPersonalityProfilesDirectory()),
            RuntimeSafety = runtimeSafety with
            {
                SafeModeReason = safeReason,
                SafeModeSinceUtc = safeSince,
                RequiredProtocolVersion = string.IsNullOrWhiteSpace(runtimeSafety.RequiredProtocolVersion)
                    ? "2024-11-05"
                    : runtimeSafety.RequiredProtocolVersion.Trim(),
                RequiredServerContractVersion = string.IsNullOrWhiteSpace(runtimeSafety.RequiredServerContractVersion)
                    ? "1.0"
                    : runtimeSafety.RequiredServerContractVersion.Trim()
            },
            ToolBudgets = normalizedBudgets
        };
    }

    private static Dictionary<string, LocationSettings> NormalizeLocationsByProfile(
        Dictionary<string, LocationSettings>? raw,
        LocationSettings fallback)
    {
        var map = new Dictionary<string, LocationSettings>(StringComparer.OrdinalIgnoreCase);

        if (raw is not null)
        {
            foreach (var (key, value) in raw)
            {
                var normalizedKey = AppSettings.NormalizeLocationProfileKey(key);
                map[normalizedKey] = NormalizeLocation(value, fallback);
            }
        }

        var defaultKey = AppSettings.DefaultLocationProfileKey;
        if (!map.ContainsKey(defaultKey))
            map[defaultKey] = fallback;

        return map;
    }

    private static LocationSettings NormalizeLocation(LocationSettings? location, LocationSettings fallback)
    {
        var src = location ?? fallback;
        var mode = src.GetNormalizedMode();
        var value = mode == "manual"
            ? (src.GetResolvedLabel() ?? "").Trim()
            : "";
        var updatedAt = (src.GetResolvedUpdatedAt() ?? "").Trim();
        var timezone = (src.GetResolvedTimezone() ?? "").Trim();

        return src with
        {
            Mode = mode,
            Value = value,
            UpdatedAt = updatedAt,
            Enabled = mode == "manual",
            Label = value,
            Timezone = timezone,
            Latitude = null,
            Longitude = null
        };
    }

    private static Dictionary<string, string> NormalizeAliasesByProfile(
        Dictionary<string, string>? raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (raw is null)
            return map;

        foreach (var (key, value) in raw)
        {
            var normalizedKey = AppSettings.NormalizeLocationProfileKey(key);
            var normalizedValue = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(normalizedValue))
                continue;

            map[normalizedKey] = normalizedValue;
        }

        return map;
    }

    private static string StringOrFallback(string? value, string fallback)
    {
        var trimmed = (value ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
            return trimmed;
        return fallback;
    }

    private static string OptionalString(string? value)
        => (value ?? "").Trim();

    private static int IntOrFallback(int value, int fallback, int min, int max)
    {
        var selected = value <= 0 ? fallback : value;
        return Math.Clamp(selected, min, max);
    }

    private static double DoubleOrFallback(double value, double fallback, double min, double max)
    {
        var selected = double.IsFinite(value) ? value : fallback;
        return Math.Clamp(selected, min, max);
    }

    private static string NormalizePolicy(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "off" => "off",
            "ask" => "ask",
            "always" => "always",
            _ => "ask"
        };
    }

    private static string NormalizeDeveloperOverride(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "ask" => "ask",
            "always" => "always",
            _ => "none"   // "off" normalizes to "none" — use per-group settings to disable individual groups
        };
    }

    private static string NormalizeReasoningGuardrails(string? value, string fallback)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "off" => "off",
            "auto" => "auto",
            "always" => "always",
            _ => fallback
        };
    }

    private static string NormalizeWebSearchMode(string? value, string fallback)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "auto" => "auto",
            "searxng" => "searxng",
            "search_api" => "search_api",
            "api" => "search_api",
            "ddg_html" => "ddg_html",
            "google_news" => "google_news",
            "manual" => "manual",
            _ => fallback
        };
    }

    private static string NormalizeSearchApiProvider(string? value, string fallback)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "" => fallback,
            "searchapi" => "searchapi",
            _ => fallback
        };
    }

    private static string NormalizeSearchApiEngine(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim().ToLowerInvariant();
    }

    private static string NormalizeWeatherProviderMode(string? value, string fallback)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "nws_us_openmeteo_fallback" => "nws_us_openmeteo_fallback",
            "openmeteo_only" => "openmeteo_only",
            "nws_only_us" => "nws_only_us",
            _ => fallback
        };
    }

    private static string NormalizeGeocodeMismatchMode(string? value, string fallback)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "fallback_previous" => "fallback_previous",
            "require_confirm" => "require_confirm",
            _ => fallback
        };
    }

    private static string NormalizeTtsEngine(string? value, string fallback)
    {
        var normalizedFallback = string.IsNullOrWhiteSpace(fallback)
            ? "kokoro"
            : fallback.Trim().ToLowerInvariant();
        var engine = (value ?? "").Trim().ToLowerInvariant();

        return engine switch
        {
            "" => normalizedFallback,
            "windows" => "windows",
            "kokoro" => "kokoro",
            _ => engine
        };
    }

    private static string NormalizeSttEngine(string? value, string fallback)
    {
        var normalizedFallback = string.IsNullOrWhiteSpace(fallback)
            ? "faster-whisper"
            : fallback.Trim().ToLowerInvariant();
        var engine = (value ?? "").Trim().ToLowerInvariant();

        return engine switch
        {
            "" => normalizedFallback,
            "whisper" => "faster-whisper",
            "faster-whisper" => "faster-whisper",
            _ => "faster-whisper"
        };
    }

    private static string EnsureSlashPath(string value)
    {
        var trimmed = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "/";
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }
}
