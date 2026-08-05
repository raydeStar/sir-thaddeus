using SirThaddeus.Config;
using SirThaddeus.LlmClient;
using SirThaddeus.RuntimeHost;

namespace SirThaddeus.Tests;

public sealed class RuntimeLlmOptionsFactoryTests
{
    [Fact]
    public void DefaultSettings_do_not_select_a_model_specific_gatekeeper()
    {
        Assert.Equal(string.Empty, new LlmSettings().GatekeeperModelId);
    }

    [Fact]
    public void BuildPrimary_PreservesCodexCliConfiguration()
    {
        var settings = new AppSettings
        {
            Llm = new LlmSettings
            {
                Provider = "codex-cli",
                Model = "gpt-5.6-luna",
                CodexCliPath = "C:\\Tools\\codex.exe",
                CodexReasoningEffort = "high"
            }
        };

        var options = RuntimeLlmOptionsFactory.BuildPrimary(settings);

        Assert.Equal("codex-cli", options.Provider);
        Assert.Equal("gpt-5.6-luna", options.Model);
        Assert.Equal("C:\\Tools\\codex.exe", options.CodexCliPath);
        Assert.Equal("high", options.CodexReasoningEffort);
    }

    [Theory]
    [InlineData(null, ForcedToolChoiceMode.Required)]
    [InlineData("required", ForcedToolChoiceMode.Required)]
    [InlineData("unknown", ForcedToolChoiceMode.Required)]
    [InlineData(" AUTO ", ForcedToolChoiceMode.Auto)]
    public void BuildPrimary_NormalizesForcedToolChoiceMode(
        string? configured,
        ForcedToolChoiceMode expected)
    {
        var settings = new AppSettings
        {
            Llm = new LlmSettings { ForcedToolChoiceMode = configured! }
        };

        Assert.Equal(expected, RuntimeLlmOptionsFactory.BuildPrimary(settings).ForcedToolChoiceMode);
    }

    [Fact]
    public void BuildGatekeeper_UsesDedicatedModel_OnSharedEndpointEvenWhenReuseFlagSet()
    {
        var settings = new AppSettings
        {
            Llm = new LlmSettings
            {
                BaseUrl = "http://localhost:1234",
                Model = "main-model",
                GatekeeperBaseUrl = "",
                GatekeeperModelId = "small-footman-model",
                ReusePrimaryModelForGatekeeperOnSharedEndpoint = true
            }
        };

        var options = RuntimeLlmOptionsFactory.BuildGatekeeper(settings);

        Assert.Equal("small-footman-model", options.Model);
        Assert.Equal("http://localhost:1234", options.BaseUrl);
        Assert.Equal(5, options.MaxTokens);
        Assert.Equal(0.0, options.Temperature);
    }

    [Fact]
    public void BuildGatekeeper_UsesDedicatedModel_WhenSharedEndpointReuseDisabled()
    {
        var settings = new AppSettings
        {
            Llm = new LlmSettings
            {
                BaseUrl = "http://localhost:1234",
                Model = "main-model",
                GatekeeperBaseUrl = "http://localhost:1234",
                GatekeeperModelId = "small-footman-model",
                ReusePrimaryModelForGatekeeperOnSharedEndpoint = false
            }
        };

        var options = RuntimeLlmOptionsFactory.BuildGatekeeper(settings);

        Assert.Equal("small-footman-model", options.Model);
    }

    [Fact]
    public void BuildGatekeeper_UsesDedicatedModel_OnSeparateEndpoint()
    {
        var settings = new AppSettings
        {
            Llm = new LlmSettings
            {
                BaseUrl = "http://localhost:1234",
                Model = "main-model",
                GatekeeperBaseUrl = "http://localhost:2234",
                GatekeeperModelId = "small-footman-model"
            }
        };

        var options = RuntimeLlmOptionsFactory.BuildGatekeeper(settings);

        Assert.Equal("small-footman-model", options.Model);
        Assert.Equal("http://localhost:2234", options.BaseUrl);
    }
}
