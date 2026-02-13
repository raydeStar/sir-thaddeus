namespace SirThaddeus.VoiceHost.Backends;

public interface IAsrBackend
{
    Task<BackendReadiness> GetReadinessAsync(CancellationToken cancellationToken);

    Task<string> TranscribeAsync(
        IFormFile audioFile,
        string? sessionId,
        string? requestId,
        CancellationToken cancellationToken);
}
