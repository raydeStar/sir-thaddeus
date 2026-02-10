namespace SirThaddeus.WebSearch;

/// <summary>
/// Probes URL reachability using lightweight HTTP requests.
/// </summary>
public interface IStatusProbe
{
    Task<StatusProbeResult> CheckAsync(
        string url,
        CancellationToken cancellationToken = default);
}
