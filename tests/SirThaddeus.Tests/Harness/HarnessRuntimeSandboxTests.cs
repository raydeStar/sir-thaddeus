using System.Text.Json;
using SirThaddeus.Config;
using SirThaddeus.Harness.Execution;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Tests;

public sealed class HarnessRuntimeSandboxTests
{
    [Fact]
    public void Create_ClearsInheritedRuntimeSafetyFlags()
    {
        var baseSettings = new AppSettings
        {
            RuntimeSafety = new RuntimeSafetySettings
            {
                PanicMode = true,
                SafeMode = true,
                SafeModeReason = "host_state",
                SafeModeSinceUtc = "2026-04-01T00:00:00.0000000Z",
                StrictHandshake = true,
                RequiredProtocolVersion = "2024-11-05",
                RequiredServerContractVersion = "1.0"
            }
        };

        var test = new HarnessTestCase
        {
            Id = "quality_weather_clarity",
            Name = "Quality - Weather response clarity",
            UserMessage = "Use weather tools for Denver and provide a concise, useful plan for the day."
        };

        using var sandbox = HarnessRuntimeSandbox.Create(baseSettings, test);

        Assert.False(sandbox.Settings.RuntimeSafety.SafeMode);
        Assert.False(sandbox.Settings.RuntimeSafety.PanicMode);
        Assert.Equal(string.Empty, sandbox.Settings.RuntimeSafety.SafeModeReason);
        Assert.Equal(string.Empty, sandbox.Settings.RuntimeSafety.SafeModeSinceUtc);

        var persisted = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(sandbox.SettingsPath));
        Assert.NotNull(persisted);
        Assert.False(persisted!.RuntimeSafety.SafeMode);
        Assert.False(persisted.RuntimeSafety.PanicMode);
        Assert.Equal(string.Empty, persisted.RuntimeSafety.SafeModeReason);
        Assert.Equal(string.Empty, persisted.RuntimeSafety.SafeModeSinceUtc);
        Assert.Equal(sandbox.SettingsPath, sandbox.Environment["ST_SETTINGS_PATH"]);
    }
}