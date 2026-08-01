using Microsoft.Extensions.Logging;

namespace SirThaddeus.LlmClient;

/// <summary>
/// Provider-neutral construction boundary used by product and research hosts.
/// The returned handle preserves reference identity across settings refreshes,
/// so orchestration components do not depend on a specific transport class.
/// </summary>
public static class LlmClientFactory
{
    public static IConfigurableLlmClient Create(
        LlmClientOptions options,
        HttpClient? httpClient = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new LmStudioClient(
            options,
            httpClient,
            loggerFactory?.CreateLogger<LmStudioClient>());
    }
}
