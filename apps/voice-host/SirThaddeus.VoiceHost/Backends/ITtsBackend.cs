using SirThaddeus.VoiceHost.Models;

namespace SirThaddeus.VoiceHost.Backends;

public interface ITtsBackend
{
    Task<BackendReadiness> GetReadinessAsync(CancellationToken cancellationToken);

    Task StreamSynthesisAsync(
        VoiceHostTtsRequest payload,
        HttpResponse response,
        CancellationToken cancellationToken);
}
