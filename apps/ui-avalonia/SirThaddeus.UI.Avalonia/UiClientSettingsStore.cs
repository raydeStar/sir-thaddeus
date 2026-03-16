using System.Text.Json;

namespace SirThaddeus.UI.Avalonia;

internal sealed class UiClientSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public UiClientSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? GetDefaultPath();
    }

    public UiClientSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new UiClientSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<UiClientSettings>(json, JsonOptions) ?? new UiClientSettings();
        }
        catch
        {
            return new UiClientSettings();
        }
    }

    public void Save(UiClientSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static string GetDefaultPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "SirThaddeus", "ui-client-settings.json");
    }
}

internal sealed record UiClientSettings
{
    public string RuntimeUrl { get; init; } = "http://127.0.0.1:5378";
    public bool AutoConnectOnLaunch { get; init; } = true;
    public bool AutoStartRuntime { get; init; } = true;
    public bool SendOnEnter { get; init; } = true;
    public bool AutoSwitchToPermissions { get; init; } = true;
    public bool MinimizeToTray { get; init; } = true;
}
