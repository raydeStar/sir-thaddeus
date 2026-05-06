using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Thaddeus.Tts.Abstractions;

namespace Thaddeus.Tts.Piper.Legacy;

/*
 * Reserve infrastructure only.
 * Piper is retired from the active TTS build, but this implementation remains
 * available for explicit fallback on low-resource machines or emergency rollback.
 */
public sealed class PiperTtsEngine : ITtsEngine
{
    private readonly PiperOptions _options;
    private bool _disposed;

    public PiperTtsEngine(PiperOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string EngineName => "piper";

    public async Task<TtsAudio> SynthesizeAsync(
        string text,
        string voiceId,
        TtsSynthesisOptions? options = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text is required.", nameof(text));
        if (!IsConfigured())
            throw new InvalidOperationException("Piper fallback is not configured.");

        var wavPath = Path.Combine(Path.GetTempPath(), $"thaddeus-piper-{Guid.NewGuid():N}.wav");
        try
        {
            await RunPiperAsync(text.Trim(), wavPath, ct).ConfigureAwait(false);
            var wav = await File.ReadAllBytesAsync(wavPath, ct).ConfigureAwait(false);
            var parsed = WavPcmReader.ReadPcm16(wav, _options.SampleRate, _options.Channels);
            return parsed;
        }
        finally
        {
            try
            {
                if (File.Exists(wavPath))
                    File.Delete(wavPath);
            }
            catch
            {
                // Best effort cleanup for legacy rollback path.
            }
        }
    }

    public async IAsyncEnumerable<TtsAudioFrame> StreamAsync(
        string text,
        string voiceId,
        TtsSynthesisOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var audio = await SynthesizeAsync(text, voiceId, options, ct).ConfigureAwait(false);
        yield return new TtsAudioFrame(audio.Pcm16, audio.SampleRate, audio.Channels, IsFinal: true);
    }

    public Task<IReadOnlyList<TtsVoiceInfo>> ListVoicesAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(_options.VoiceModelPath) || !File.Exists(_options.VoiceModelPath))
            return Task.FromResult<IReadOnlyList<TtsVoiceInfo>>(Array.Empty<TtsVoiceInfo>());

        var id = Path.GetFileNameWithoutExtension(_options.VoiceModelPath);
        if (id.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            id = Path.GetFileNameWithoutExtension(id);
        IReadOnlyList<TtsVoiceInfo> voices = ImmutableArray.Create(new TtsVoiceInfo(
            id,
            id,
            Language: "unknown",
            Gender: null,
            Style: "legacy",
            EngineName));
        return Task.FromResult(voices);
    }

    public async Task<bool> SupportsVoiceAsync(string voiceId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(voiceId))
            return false;

        var voices = await ListVoicesAsync(ct).ConfigureAwait(false);
        return voices.Any(voice => string.Equals(voice.Id, voiceId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private async Task RunPiperAsync(string text, string wavPath, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _options.BinaryPath!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add("-m");
        process.StartInfo.ArgumentList.Add(_options.VoiceModelPath!);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(wavPath);
        if (_options.SpeakerId is { } speakerId)
        {
            process.StartInfo.ArgumentList.Add("-s");
            process.StartInfo.ArgumentList.Add(speakerId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!process.Start())
            throw new InvalidOperationException("Failed to start Piper fallback process.");

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.StandardInput.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.SynthesisTimeout);
        await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask.ConfigureAwait(false);
            throw new InvalidOperationException($"Piper fallback exited with code {process.ExitCode}: {stderr}");
        }
    }

    private bool IsConfigured()
        => !string.IsNullOrWhiteSpace(_options.BinaryPath)
           && File.Exists(_options.BinaryPath)
           && !string.IsNullOrWhiteSpace(_options.VoiceModelPath)
           && File.Exists(_options.VoiceModelPath);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal static class WavPcmReader
{
    public static TtsAudio ReadPcm16(byte[] wav, int fallbackSampleRate, int fallbackChannels)
    {
        if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
        {
            return FromPcm(wav, fallbackSampleRate, fallbackChannels);
        }

        var sampleRate = BitConverter.ToInt32(wav, 24);
        var channels = BitConverter.ToInt16(wav, 22);
        var dataOffset = FindDataOffset(wav);
        if (dataOffset < 0)
            return FromPcm(wav, fallbackSampleRate, fallbackChannels);

        var dataLength = BitConverter.ToInt32(wav, dataOffset + 4);
        var pcmOffset = dataOffset + 8;
        if (pcmOffset + dataLength > wav.Length)
            dataLength = wav.Length - pcmOffset;

        var pcm = new byte[Math.Max(0, dataLength)];
        Buffer.BlockCopy(wav, pcmOffset, pcm, 0, pcm.Length);
        return FromPcm(pcm, sampleRate, channels);
    }

    private static int FindDataOffset(byte[] wav)
    {
        for (var index = 12; index + 8 <= wav.Length;)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(wav, index, 4);
            var chunkLength = BitConverter.ToInt32(wav, index + 4);
            if (string.Equals(chunkId, "data", StringComparison.Ordinal))
                return index;
            index += 8 + Math.Max(0, chunkLength);
        }

        return -1;
    }

    private static TtsAudio FromPcm(byte[] pcm, int sampleRate, int channels)
    {
        var duration = sampleRate > 0 && channels > 0
            ? TimeSpan.FromSeconds(pcm.Length / (double)(sampleRate * channels * sizeof(short)))
            : TimeSpan.Zero;
        return new TtsAudio(pcm, sampleRate, channels, duration);
    }
}
