using System.IO;
using NAudio.Wave;
using SirThaddeus.AuditLog;
using SirThaddeus.Voice;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Captures microphone input and returns WAV bytes.
/// Uses SemaphoreSlim for async coordination to prevent orchestrator hangs.
/// </summary>
public sealed class AudioCaptureService : IAudioCaptureService, IDisposable
{
    private readonly IAuditLogger _auditLogger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly TimeSpan StopRecordingTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartRecordingTimeout = TimeSpan.FromSeconds(3);

    private WaveInEvent? _waveIn;
    private MemoryStream? _buffer;
    private MemoryStream? _pcmBuffer;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<bool>? _stopCompletion;
    private string? _activeSessionId;
    private bool _firstFrameCapturedForSession;
    private volatile bool _isCapturing;
    private bool _disposed;

    public event EventHandler<AudioCaptureFirstFrameEventArgs>? FirstAudioFrameCaptured;

    public AudioCaptureService(IAuditLogger auditLogger)
    {
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
    }

    /// <summary>
    /// NAudio device index for the recording device.
    /// 0 = system default. Set before <see cref="StartCaptureAsync"/>.
    /// Safe to change between recording sessions.
    /// </summary>
    public int DeviceNumber { get; set; }

    /// <summary>
    /// Software gain multiplier applied to captured samples.
    /// 1.0 = unity, 0.0 = mute, 2.0 = double amplitude.
    /// Applied in real-time during recording via <see cref="OnDataAvailable"/>.
    /// </summary>
    public double InputGain { get; set; } = 1.0;

    public bool IsCapturing => _isCapturing;

    public async Task StartCaptureAsync(string sessionId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_waveIn is not null)
                return;

            _activeSessionId = sessionId;
            _firstFrameCapturedForSession = false;
            _buffer = new MemoryStream();
            _pcmBuffer = new MemoryStream();
            _writer = new WaveFileWriter(new NonClosingStream(_buffer), new WaveFormat(16000, 16, 1));
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

            // StartRecording can hang on some drivers/devices.
            // We enforce a strict timeout to prevent indefinite orchestration freezes.
            var startTask = Task.Run(() => _waveIn.StartRecording(), cancellationToken);
            var completed = await Task.WhenAny(startTask, Task.Delay(StartRecordingTimeout, cancellationToken));

            if (completed != startTask)
            {
                // Timeout logic
                CleanupUnsafe(); // Disposes _waveIn to break the hang if possible
                WriteAuditNonBlocking("VOICE_CAPTURE_START_TIMEOUT", "error", new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                    ["device"] = DeviceNumber
                });
                throw new TimeoutException($"Audio driver failed to start recording within {StartRecordingTimeout.TotalMilliseconds}ms.");
            }

            // Rethrow any exception from StartRecording
            await startTask;
            _isCapturing = true;
        }
        catch (Exception)
        {
            CleanupUnsafe();
            throw;
        }
        finally
        {
            _gate.Release();
        }

        WriteAuditNonBlocking("VOICE_CAPTURE_START", "ok", new Dictionary<string, object>
        {
            ["sessionId"] = sessionId
        });
    }

    public async Task<VoiceAudioClip?> StopCaptureAsync(string sessionId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        TaskCompletionSource<bool>? completion;
        WaveInEvent? waveIn;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_waveIn is null || !string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
                return null;

            completion = _stopCompletion;
            waveIn = _waveIn;
        }
        finally
        {
            _gate.Release();
        }

        // StopRecording is thread-safe and should be called without the lock held
        // so that OnRecordingStopped can re-acquire the lock.
        waveIn?.StopRecording();

        if (completion is not null)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(StopRecordingTimeout);
                await completion.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                WriteAuditNonBlocking("VOICE_CAPTURE_STOP_TIMEOUT", "timeout", new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                    ["timeoutMs"] = (long)StopRecordingTimeout.TotalMilliseconds
                });
            }
        }

        byte[] bytes;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _writer?.Dispose(); // finalizes WAV header
            bytes = _buffer?.ToArray() ?? Array.Empty<byte>();
            CleanupUnsafe();
        }
        finally
        {
            _gate.Release();
        }

        WriteAuditNonBlocking("VOICE_CAPTURE_STOP", "ok", new Dictionary<string, object>
        {
            ["sessionId"] = sessionId,
            ["bytes"] = bytes.Length
        });

        return new VoiceAudioClip
        {
            AudioBytes = bytes,
            ContentType = "audio/wav",
            SampleRateHz = 16000,
            Channels = 1,
            BitsPerSample = 16
        };
    }

    public async Task AbortCaptureAsync(string sessionId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = sessionId; // unused but kept for interface consistency

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_waveIn is null)
                return;

            try { _waveIn.StopRecording(); } catch { }
            _writer?.Dispose();
            CleanupUnsafe();
        }
        finally
        {
            _gate.Release();
        }

        WriteAuditNonBlocking("VOICE_CAPTURE_ABORT", "ok");
    }

    /// <summary>
    /// Produces a bounded WAV snapshot while recording for live ASR preview.
    /// Returns null when capture isn't active or not enough audio is buffered yet.
    /// </summary>
    public VoiceAudioClip? CreateLiveSnapshotClip(int maxDurationMs = 2_500)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Best-effort lock. If we can't get it immediately, skip the preview frame.
        // This prevents the UI loop from stalling if the capture service is busy.
        if (!_gate.Wait(0))
            return null;

        byte[] pcm;
        try
        {
            if (_waveIn is null || _pcmBuffer is null || _pcmBuffer.Length < 2_000)
                return null;

            pcm = _pcmBuffer.ToArray();
        }
        finally
        {
            _gate.Release();
        }

        // Keep payload bounded so preview calls stay lightweight.
        var maxBytes = Math.Max(3_200, (16_000 * 2 * Math.Max(500, maxDurationMs)) / 1_000);
        if (pcm.Length > maxBytes)
        {
            var trimmed = new byte[maxBytes];
            Buffer.BlockCopy(pcm, pcm.Length - maxBytes, trimmed, 0, maxBytes);
            pcm = trimmed;
        }

        using var wav = new MemoryStream();
        using (var writer = new WaveFileWriter(new NonClosingStream(wav), new WaveFormat(16000, 16, 1)))
        {
            writer.Write(pcm, 0, pcm.Length);
            writer.Flush();
        }

        return new VoiceAudioClip
        {
            AudioBytes = wav.ToArray(),
            ContentType = "audio/wav",
            SampleRateHz = 16000,
            Channels = 1,
            BitsPerSample = 16
        };
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // OnDataAvailable runs on a background thread managed by NAudio.
        // We must be careful not to deadlock if the main thread is holding the lock.
        string? firstFrameSessionId = null;
        DateTimeOffset? firstFrameAt = null;
        int firstFrameBytes = 0;
        _gate.Wait();
        try
        {
            if (_waveIn is null) return;

            var buffer = e.Buffer;
            var count  = e.BytesRecorded;

            if (count > 0 && Math.Abs(InputGain - 1.0) > 0.01)
            {
                buffer = ApplyGain(e.Buffer, count, InputGain);
            }

            _writer?.Write(buffer, 0, count);
            _writer?.Flush();
            _pcmBuffer?.Write(buffer, 0, count);
            _pcmBuffer?.Flush();

            if (count > 0 && !_firstFrameCapturedForSession)
            {
                _firstFrameCapturedForSession = true;
                firstFrameSessionId = _activeSessionId;
                firstFrameAt = DateTimeOffset.UtcNow;
                firstFrameBytes = count;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (firstFrameAt is { } ts && !string.IsNullOrWhiteSpace(firstFrameSessionId))
        {
            WriteAuditNonBlocking("VOICE_CAPTURE_FIRST_FRAME", "ok", new Dictionary<string, object>
            {
                ["sessionId"] = firstFrameSessionId,
                ["bytesRecorded"] = firstFrameBytes
            });

            try
            {
                FirstAudioFrameCaptured?.Invoke(
                    this,
                    new AudioCaptureFirstFrameEventArgs(firstFrameSessionId, ts, firstFrameBytes));
            }
            catch
            {
                // Timing events are best-effort and must not impact recording.
            }
        }
    }

    private static byte[] ApplyGain(byte[] source, int length, double gain)
    {
        var result = new byte[length];
        for (int i = 0; i + 1 < length; i += 2)
        {
            short sample   = (short)(source[i] | (source[i + 1] << 8));
            int   amplified = Math.Clamp((int)(sample * gain), short.MinValue, short.MaxValue);
            result[i]     = (byte)(amplified & 0xFF);
            result[i + 1] = (byte)((amplified >> 8) & 0xFF);
        }
        return result;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _gate.Wait();
        try
        {
            if (e.Exception is not null)
                _stopCompletion?.TrySetException(e.Exception);
            else
                _stopCompletion?.TrySetResult(true);
        }
        finally
        {
            _gate.Release();
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
        _isCapturing = false;
        _writer = null;
        _buffer?.Dispose();
        _buffer = null;
        _pcmBuffer?.Dispose();
        _pcmBuffer = null;
        _stopCompletion = null;
        _activeSessionId = null;
        _firstFrameCapturedForSession = false;
    }

    private void WriteAuditNonBlocking(
        string action,
        string result,
        Dictionary<string, object>? details = null)
    {
        var logger = _auditLogger;
        _ = Task.Run(() =>
        {
            try
            {
                logger.Append(new AuditEvent
                {
                    Actor = "voice",
                    Action = action,
                    Result = result,
                    Details = details
                });
            }
            catch
            {
                // Best effort only.
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;

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

    private sealed class NonClosingStream : Stream
    {
        private readonly Stream _inner;

        public NonClosingStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) => Flush();
    }
}

public sealed record AudioCaptureFirstFrameEventArgs(
    string SessionId,
    DateTimeOffset TimestampUtc,
    int BytesRecorded);
