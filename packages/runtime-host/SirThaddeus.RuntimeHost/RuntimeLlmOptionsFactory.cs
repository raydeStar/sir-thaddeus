using SirThaddeus.Config;
using SirThaddeus.LlmClient;

namespace SirThaddeus.RuntimeHost;

public static class RuntimeLlmOptionsFactory
{
    public static LlmClientOptions BuildPrimary(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new LlmClientOptions
        {
            BaseUrl = settings.Llm.BaseUrl,
            Model = settings.Llm.Model,
            MaxTokens = settings.Llm.MaxTokens,
            ContextWindowTokens = settings.Llm.ContextWindowTokens,
            Temperature = settings.Llm.Temperature
        };
    }

    public static LlmClientOptions BuildGatekeeper(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var gatekeeperUrl = string.IsNullOrWhiteSpace(settings.Llm.GatekeeperBaseUrl)
            ? settings.Llm.BaseUrl
            : settings.Llm.GatekeeperBaseUrl;

        return new LlmClientOptions
        {
            BaseUrl = gatekeeperUrl,
            Model = settings.Llm.GatekeeperModelId,
            MaxTokens = 5,
            ContextWindowTokens = 2048,
            Temperature = 0.0
        };
    }
}
