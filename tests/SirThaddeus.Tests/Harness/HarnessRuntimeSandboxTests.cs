using System.Text.Json;
using SirThaddeus.Agent;
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
        Assert.Equal(
            Path.Combine(sandbox.RootDirectory, "wiki-library"),
            sandbox.Environment["ST_WIKI_LIBRARY_PATH"]);
    }

    [Fact]
    public void CreateShared_Can_disable_managed_search_for_explicit_non_web_suites()
    {
        var baseSettings = new AppSettings
        {
            WebSearch = new WebSearchSettings
            {
                Mode = "auto",
                SearxngAutoStart = true
            }
        };

        using var sandbox = HarnessRuntimeSandbox.CreateShared(baseSettings, enableManagedSearch: false);

        Assert.False(sandbox.Settings.WebSearch.SearxngAutoStart);
    }

    [Fact]
    public void Host_requirements_only_start_managed_search_when_selected_tests_can_use_it()
    {
        var computeSuite = new HarnessSuite
        {
            Name = "compute",
            Tests =
            [
                new HarnessTestCase
                {
                    Id = "compute",
                    AllowedTools = ["python_eval"],
                    Assertions = new HarnessAssertions { AllowedToolsOnly = true }
                }
            ]
        };
        var webSuite = new HarnessSuite
        {
            Name = "web",
            Tests =
            [
                new HarnessTestCase
                {
                    Id = "web",
                    AllowedTools = [ToolNames.WebSearch],
                    Assertions = new HarnessAssertions { AllowedToolsOnly = true }
                }
            ]
        };
        var unrestrictedSuite = new HarnessSuite
        {
            Name = "unrestricted",
            Tests =
            [
                new HarnessTestCase
                {
                    Id = "unrestricted",
                    Assertions = new HarnessAssertions { AllowedToolsOnly = false }
                }
            ]
        };

        Assert.False(HarnessHostRequirements.FromSuites([computeSuite]).RequiresManagedSearch);
        Assert.True(HarnessHostRequirements.FromSuites([webSuite]).RequiresManagedSearch);
        Assert.True(HarnessHostRequirements.FromSuites([unrestrictedSuite]).RequiresManagedSearch);
    }
}
