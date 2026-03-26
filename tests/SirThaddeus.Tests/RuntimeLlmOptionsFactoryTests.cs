using SirThaddeus.Config;
using SirThaddeus.RuntimeHost;

namespace SirThaddeus.Tests;

public sealed class RuntimeLlmOptionsFactoryTests
{
    [Fact]
    public void BuildGatekeeper_ReusesPrimaryModel_OnSharedEndpointByDefault()
    {
        var settings = new AppSettings
        {
            Llm = new LlmSettings
            {
                BaseUrl = "http://localhost:1234",
                Model = "main-model",
                GatekeeperBaseUrl = "",
                GatekeeperModelId = "small-footman-model"
            }
        };

        var options = RuntimeLlmOptionsFactory.BuildGatekeeper(settings);

        Assert.Equal("main-model", options.Model);
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