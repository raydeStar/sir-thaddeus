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

        var gatekeeperModel = ShouldReusePrimaryModelForGatekeeper(settings, gatekeeperUrl)
            ? settings.Llm.Model
            : settings.Llm.GatekeeperModelId;

        return new LlmClientOptions
        {
            BaseUrl = gatekeeperUrl,
            Model = gatekeeperModel,
            MaxTokens = 5,
            ContextWindowTokens = 2048,
            Temperature = 0.0
        };
    }

    internal static bool ShouldReusePrimaryModelForGatekeeper(AppSettings settings, string gatekeeperUrl)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Llm.ReusePrimaryModelForGatekeeperOnSharedEndpoint)
            return false;

        if (string.IsNullOrWhiteSpace(settings.Llm.Model) || string.IsNullOrWhiteSpace(settings.Llm.GatekeeperModelId))
            return false;

        if (string.Equals(settings.Llm.Model, settings.Llm.GatekeeperModelId, StringComparison.OrdinalIgnoreCase))
            return false;

        return UriHostsMatch(settings.Llm.BaseUrl, gatekeeperUrl);
    }

    private static bool UriHostsMatch(string left, string right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var leftUri) ||
            !Uri.TryCreate(right, UriKind.Absolute, out var rightUri))
        {
            return string.Equals(
                (left ?? string.Empty).Trim().TrimEnd('/'),
                (right ?? string.Empty).Trim().TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(leftUri.Scheme, rightUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(leftUri.Host, rightUri.Host, StringComparison.OrdinalIgnoreCase) &&
               leftUri.Port == rightUri.Port;
    }
}
