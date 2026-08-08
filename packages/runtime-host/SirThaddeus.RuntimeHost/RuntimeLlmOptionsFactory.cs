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
            Provider = settings.Llm.Provider,
            BaseUrl = settings.Llm.BaseUrl,
            ApiKey = settings.Llm.ApiKey,
            ChatCompletionPath = settings.Llm.ChatCompletionPath,
            Model = settings.Llm.Model,
            ForcedToolChoiceMode = ParseForcedToolChoiceMode(settings.Llm.ForcedToolChoiceMode),
            CodexCliPath = settings.Llm.CodexCliPath,
            CodexReasoningEffort = settings.Llm.CodexReasoningEffort,
            PreloadModelKey = settings.Llm.PreloadModelKey,
            EnableStartupWarmup = settings.Llm.EnableStartupWarmup,
            EnableKeepWarm = settings.Llm.EnableKeepWarm,
            ContextLength = settings.Llm.ContextLength,
            FlashAttention = settings.Llm.FlashAttention,
            OffloadKvCacheToGpu = settings.Llm.OffloadKvCacheToGpu,
            MaxConcurrentLlmRequests = settings.Llm.MaxConcurrentLlmRequests,
            WarmupTimeoutSeconds = settings.Llm.WarmupTimeoutSeconds,
            KeepWarmIntervalMinutes = settings.Llm.KeepWarmIntervalMinutes,
            MaxInputTokensSoftCap = settings.Llm.MaxInputTokensSoftCap,
            MaxOutputTokensDefault = settings.Llm.MaxOutputTokensDefault,
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
            Provider = settings.Llm.Provider,
            BaseUrl = gatekeeperUrl,
            ApiKey = settings.Llm.ApiKey,
            Model = gatekeeperModel,
            CodexCliPath = settings.Llm.CodexCliPath,
            CodexReasoningEffort = settings.Llm.CodexReasoningEffort,
            EnableStartupWarmup = false,
            EnableKeepWarm = false,
            MaxConcurrentLlmRequests = settings.Llm.MaxConcurrentLlmRequests,
            MaxInputTokensSoftCap = Math.Min(settings.Llm.MaxInputTokensSoftCap, 2048),
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

        if (string.IsNullOrWhiteSpace(settings.Llm.Model))
            return false;

        if (string.IsNullOrWhiteSpace(settings.Llm.GatekeeperModelId))
            return UriHostsMatch(settings.Llm.BaseUrl, gatekeeperUrl);

        return UriHostsMatch(settings.Llm.BaseUrl, gatekeeperUrl) &&
               string.Equals(settings.Llm.Model, settings.Llm.GatekeeperModelId, StringComparison.OrdinalIgnoreCase);
    }

    internal static ForcedToolChoiceMode ParseForcedToolChoiceMode(string? mode) =>
        string.Equals(mode?.Trim(), "auto", StringComparison.OrdinalIgnoreCase)
            ? ForcedToolChoiceMode.Auto
            : ForcedToolChoiceMode.Required;

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
