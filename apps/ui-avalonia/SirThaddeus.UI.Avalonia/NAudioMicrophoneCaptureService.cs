using System.IO;
using NAudio.Wave;

namespace SirThaddeus.UI.Avalonia;

internal sealed class NAudioMicrophoneCaptureService : IMicrophoneCaptureService
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WaveInEvent? _waveIn;
    private MemoryStream? _wavBuffer;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<bool>? _stopCompletion;
    private bool _disposed;

    public bool IsCapturing => _waveIn is not null;

    public int DeviceNumber { get; set; } = -1;

    
    public double InputGain { get; set; } = 1.0;

    public async Task StartCaptureAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Microphone capture is currently supported on Windows only.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_waveIn is not null)
            {
                return;
            }

            _wavBuffer = new MemoryStream();
            _writer = new WaveFileWriter(new NonClosingStream(_wavBuffer), new WaveFormat(16000, 16, 1));
            _stopCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _waveIn = new WaveInEvent
            {
                DeviceNumber = DeviceNumber,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 50,
                NumberOfBuffers = 3
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;

            var startTask = Task.Run(() => _waveIn.StartRecording(), cancellationToken);
            var completed = await Task.WhenAny(startTask, Task.Delay(StartTimeout, cancellationToken));
            if (completed != startTask)
            {
                CleanupUnsafe();
                throw new TimeoutException($"Microphone driver did not start within {StartTimeout.TotalMilliseconds:0}ms.");
            }

            await startTask;
        }
        catch
        {
            CleanupUnsafe();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]?> StopCaptureAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        TaskCompletionSource<bool>? completion;
        WaveInEvent? waveIn;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_waveIn is null)
            {
                return null;
            }

            completion = _stopCompletion;
            waveIn = _waveIn;
        }
        finally
        {
            _gate.Release();
        }

        waveIn?.StopRecording();

        if (completion is not null)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(StopTimeout);
            try
            {
                await completion.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Best effort stop timeout.
            }
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _writer?.Dispose();
            var bytes = _wavBuffer?.ToArray() ?? Array.Empty<byte>();
            CleanupUnsafe();
            return bytes.Length == 0 ? null : bytes;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AbortCaptureAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_waveIn is null)
            {
                return;
            }

            try
            {
                _waveIn.StopRecording();
            }
            catch
            {
                // Best effort cancellation only.
            }

            _writer?.Dispose();
            CleanupUnsafe();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        _gate.Wait();
        try
        {
            if (_waveIn is null)
            {
                return;
            }

            var buffer = args.Buffer;
            var count = args.BytesRecorded;
            if (count > 0 && Math.Abs(InputGain - 1.0) > 0.01)
            {
                buffer = ApplyGain(args.Buffer, count, InputGain);
            }

            _writer?.Write(buffer, 0, count);
            _writer?.Flush();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static byte[] ApplyGain(byte[] source, int length, double gain)
    {
        var result = new byte[length];
        for (var i = 0; i + 1 < length; i += 2)
        {
            short sample = (short)(source[i] | (source[i + 1] << 8));
            var amplified = Math.Clamp((int)(sample * gain), short.MinValue, short.MaxValue);
            result[i] = (byte)(amplified & 0xFF);
            result[i + 1] = (byte)((amplified >> 8) & 0xFF);
        }

        return result;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        _gate.Wait();
        try
        {
            if (args.Exception is not null)
            {
                _stopCompletion?.TrySetException(args.Exception);
            }
            else
            {
                _stopCompletion?.TrySetResult(true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Wait();
        try
        {
            _writer?.Dispose();
            CleanupUnsafe();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void CleanupUnsafe()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
        }

        _waveIn = null;
        _writer = null;
        _wavBuffer?.Dispose();
        _wavBuffer = null;
        _stopCompletion = null;
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Flush();
            }
        }
    }
}


