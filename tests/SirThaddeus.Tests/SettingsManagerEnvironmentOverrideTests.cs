using SirThaddeus.AuditLog;
using SirThaddeus.Config;

namespace SirThaddeus.Tests;

[Collection(RuntimeEnvironmentVariableCollection.Name)]
public sealed class SettingsManagerEnvironmentOverrideTests
{
    [Fact]
    public void GetSettingsPath_UsesEnvironmentOverride()
    {
        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["ST_SETTINGS_PATH"] = @"C:\temp\sandbox\settings.json"
        });

        var settingsPath = SettingsManager.GetSettingsPath();

        Assert.Equal(@"C:\temp\sandbox\settings.json", settingsPath);
        Assert.Equal(@"C:\temp\sandbox", SettingsManager.GetSettingsDirectory());
        Assert.Equal(@"C:\temp\sandbox\profiles", SettingsManager.GetPersonalityProfilesDirectory());
    }

    [Fact]
    public void AuditLogger_DefaultPath_UsesEnvironmentOverride()
    {
        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["ST_AUDIT_PATH"] = @"C:\temp\sandbox\audit.jsonl"
        });

        var auditPath = JsonLineAuditLogger.GetDefaultPath();

        Assert.Equal(@"C:\temp\sandbox\audit.jsonl", auditPath);
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