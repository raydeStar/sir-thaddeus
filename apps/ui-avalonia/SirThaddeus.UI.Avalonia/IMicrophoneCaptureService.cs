namespace SirThaddeus.UI.Avalonia;

internal interface IMicrophoneCaptureService : IDisposable
{
    bool IsCapturing { get; }

    Task StartCaptureAsync(CancellationToken cancellationToken);

    Task<byte[]?> StopCaptureAsync(CancellationToken cancellationToken);

    Task AbortCaptureAsync(CancellationToken cancellationToken);
}
