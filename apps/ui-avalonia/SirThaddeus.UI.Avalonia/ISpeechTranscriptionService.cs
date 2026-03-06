namespace SirThaddeus.UI.Avalonia;

internal interface ISpeechTranscriptionService
{
    Task<string> TranscribeAsync(byte[] wavBytes, string sessionId, CancellationToken cancellationToken);
}
