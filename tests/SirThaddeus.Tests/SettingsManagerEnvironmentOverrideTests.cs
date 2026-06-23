using SirThaddeus.AuditLog;
using SirThaddeus.Config;

namespace SirThaddeus.Tests;

[Collection(RuntimeEnvironmentVariableCollection.Name)]
public sealed class SettingsManagerEnvironmentOverrideTests
{
    [Fact]
    public void GetSettingsPath_UsesEnvironmentOverride()
    {
        var settingsPath = CreateTempSettingsPath();
        var settingsDirectory = Path.GetDirectoryName(settingsPath)!;

        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["ST_SETTINGS_PATH"] = settingsPath
        });

        var resolvedSettingsPath = SettingsManager.GetSettingsPath();

        Assert.Equal(settingsPath, resolvedSettingsPath);
        Assert.Equal(settingsDirectory, SettingsManager.GetSettingsDirectory());
        Assert.Equal(Path.Combine(settingsDirectory, "profiles"), SettingsManager.GetPersonalityProfilesDirectory());
    }

    [Fact]
    public void AuditLogger_DefaultPath_UsesEnvironmentOverride()
    {
        var auditPath = CreateTempAuditPath();

        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["ST_AUDIT_PATH"] = auditPath
        });

        var resolvedAuditPath = JsonLineAuditLogger.GetDefaultPath();

        Assert.Equal(auditPath, resolvedAuditPath);
    }

    [Fact]
    public void LoadWithDiagnostics_CreatesBaselineDefaults_OnFirstRun()
    {
        var settingsPath = CreateTempSettingsPath();

        try
        {
            using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
            {
                ["ST_SETTINGS_PATH"] = settingsPath
            });

            var result = SettingsManager.LoadWithDiagnostics();

            Assert.True(result.CreatedDefaults);
            Assert.Equal(AppSettings.BaselineProductProfileId, result.Settings.ProductProfileId);
            Assert.False(result.Settings.Audio.TtsEnabled);
            Assert.False(result.Settings.Voice.VoiceHostEnabled);
            Assert.False(result.Settings.WebSearch.SearxngAutoStart);
            Assert.Equal("user-john-doe", result.Settings.ActiveProfileId);
            Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
            Assert.True(File.Exists(settingsPath));
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void LoadWithDiagnostics_PreservesExplicitVoiceAndSearchOptIns_WhenMigrating()
    {
        var settingsPath = CreateTempSettingsPath();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, """
                {
                  "schemaVersion": 3,
                  "audio": {
                    "ttsEnabled": true
                  },
                  "voice": {
                    "voiceHostEnabled": true
                  },
                  "webSearch": {
                    "searxngAutoStart": true
                  }
                }
                """);

            using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
            {
                ["ST_SETTINGS_PATH"] = settingsPath
            });

            var result = SettingsManager.LoadWithDiagnostics();

            Assert.True(result.MigratedSchema);
            Assert.Equal(AppSettings.BaselineProductProfileId, result.Settings.ProductProfileId);
            Assert.True(result.Settings.Audio.TtsEnabled);
            Assert.True(result.Settings.Voice.VoiceHostEnabled);
            Assert.True(result.Settings.WebSearch.SearxngAutoStart);
            Assert.False(result.Settings.IsVoiceHostEnabledEffective());
            Assert.False(result.Settings.IsManagedSearxngAutoStartEffective());
            Assert.False(result.Settings.AllowsDeepDiveBriefingsByProfile());
            Assert.False(result.Settings.AllowsAdvancedPlaceDiscoveryByProfile());
            Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void LoadWithDiagnostics_ClearsRecoveredSchemaSafeMode_WhenSchemaIsNowSupported()
    {
        var settingsPath = CreateTempSettingsPath();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, """
                {
                  "schemaVersion": 4,
                  "runtimeSafety": {
                    "safeMode": true,
                    "safeModeReason": "unsupported_settings_schema_v4",
                    "safeModeSinceUtc": "2026-04-01T13:53:48.9702863+00:00"
                  }
                }
                """);

            using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
            {
                ["ST_SETTINGS_PATH"] = settingsPath
            });

            var result = SettingsManager.LoadWithDiagnostics();

            Assert.False(result.Settings.RuntimeSafety.SafeMode);
            Assert.Equal(string.Empty, result.Settings.RuntimeSafety.SafeModeReason);
            Assert.Equal(string.Empty, result.Settings.RuntimeSafety.SafeModeSinceUtc);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void LoadWithDiagnostics_NormalizesWindowsTtsSettingsToKokoroSharp()
    {
        var settingsPath = CreateTempSettingsPath();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, """
                {
                  "schemaVersion": 4,
                  "voice": {
                    "ttsEngine": "windows",
                    "ttsVoiceId": "Microsoft David"
                  }
                }
                """);

            using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
            {
                ["ST_SETTINGS_PATH"] = settingsPath
            });

            var result = SettingsManager.LoadWithDiagnostics();

            Assert.Equal("kokoro-sharp", result.Settings.Voice.TtsEngine);
            Assert.Equal("bm_lewis", result.Settings.Voice.TtsVoiceId);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void OptionalFeatureHelpers_NonBaselineProfile_AllowExplicitOptIns()
    {
        var settings = new AppSettings
        {
            ProductProfileId = "power-user",
            Voice = new VoiceSettings
            {
                VoiceHostEnabled = true
            },
            WebSearch = new WebSearchSettings
            {
                SearxngAutoStart = true
            }
        };

        Assert.False(settings.IsBaselineProductProfile());
        Assert.True(settings.AllowsVoiceInteractionByProfile());
        Assert.True(settings.AllowsManagedSearxngAutoStartByProfile());
        Assert.True(settings.AllowsDeepDiveBriefingsByProfile());
        Assert.True(settings.AllowsAdvancedPlaceDiscoveryByProfile());
        Assert.True(settings.IsVoiceHostEnabledEffective());
        Assert.True(settings.IsManagedSearxngAutoStartEffective());
    }

    private static string CreateTempSettingsPath()
        => Path.Combine(
            Path.GetTempPath(),
            "SirThaddeusTests",
            Guid.NewGuid().ToString("N"),
            "settings.json");

    private static string CreateTempAuditPath()
        => Path.Combine(
            Path.GetTempPath(),
            "SirThaddeusTests",
            Guid.NewGuid().ToString("N"),
            "audit.jsonl");

    private static void DeleteTempSettingsPath(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _priorValues = new(StringComparer.OrdinalIgnoreCase);

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (key, value) in values)
            {
                _priorValues[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in _priorValues)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
