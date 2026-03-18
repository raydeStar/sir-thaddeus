namespace SirThaddeus.UI.Avalonia;

internal interface IMicrophoneCaptureService : IDisposable
{
    bool IsCapturing { get; }

    int DeviceNumber { get; set; }
    double InputGain { get; set; }

    Task StartCaptureAsync(CancellationToken cancellationToken);

    Task<byte[]?> StopCaptureAsync(CancellationToken cancellationToken);

    Task AbortCaptureAsync(CancellationToken cancellationToken);
}

