namespace SirThaddeus.VoiceHost.Backends;

public interface IYouTubeJobsBackend
{
    Task<ProxyJsonResult> StartJobAsync(
        object payload,
        string requestId,
        CancellationToken cancellationToken);

    Task<ProxyJsonResult> GetJobAsync(
        string jobId,
        string requestId,
        CancellationToken cancellationToken);

    Task<ProxyJsonResult> CancelJobAsync(
        string jobId,
        string requestId,
        CancellationToken cancellationToken);
}

public sealed record ProxyJsonResult(
    int StatusCode,
    string ContentType,
    string Body);
