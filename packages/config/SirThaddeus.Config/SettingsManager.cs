using System.Text.Json;

namespace SirThaddeus.Config;

/// <summary>
/// Reads and writes the application settings file.
/// Creates a default settings file on first run.
/// </summary>
public static class SettingsManager
{
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
    {
        var path = GetSettingsPath();

        if (!File.Exists(path))
        {
            var defaults = new AppSettings();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                   ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Corrupted file; recreate with defaults.
            var defaults = new AppSettings();
            Save(defaults);
            return defaults;
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

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(GetSettingsPath(), json);
    }
}
