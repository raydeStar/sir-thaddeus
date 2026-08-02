namespace SirThaddeus.LlmClient;

/// <summary>
/// Stable host-facing handle for a model provider whose settings can be
/// refreshed without rebuilding the orchestration graph that consumes it.
/// </summary>
public interface IConfigurableLlmClient :
    ILlmClient,
    ILlmUsageTelemetry,
    ILlmRuntimeDiagnostics,
    ILlmWarmupClient,
    IDisposable
{
    void UpdateOptions(LlmClientOptions options);
}
