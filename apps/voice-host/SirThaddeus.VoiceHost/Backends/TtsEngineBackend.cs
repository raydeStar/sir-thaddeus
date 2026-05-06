using System.Buffers.Binary;
using SirThaddeus.VoiceHost.Models;
using Thaddeus.Tts.Abstractions;

namespace SirThaddeus.VoiceHost.Backends;

public sealed class TtsEngineBackend : ITtsBackend
{
    private readonly TtsEngineRegistry _engines;
    private readonly VoiceHostRuntimeOptions _options;

    public TtsEngineBackend(TtsEngineRegistry engines, VoiceHostRuntimeOptions options)
    {
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<BackendReadiness> GetReadinessAsync(CancellationToken cancellationToken)
    {
        try
        {
            var engine = _engines.Resolve(_options.TtsEngine);
            var voices = await engine.ListVoicesAsync(cancellationToken).ConfigureAwait(false);
            var missing = voices.Count == 0 ? new[] { "No TTS voices were found." } : Array.Empty<string>();
            var ready = voices.Count > 0;
            var status = new BackendEngineStatus(
                SchemaVersion: 1,
                Ready: ready,
                Engine: engine.EngineName,
                EngineVersion: typeof(ITtsEngine).Assembly.GetName().Version?.ToString() ?? "",
                ModelId: _options.TtsModelId,
                InstanceId: Environment.MachineName,
                TimestampUtc: DateTimeOffset.UtcNow.ToString("O"),
                Details: new BackendEngineStatusDetails(
                    Installed: ready,
                    Missing: missing,
                    LastError: ready ? "" : "No TTS voices were found."));

            return ready
                ? BackendReadiness.Ok("TTS engine ready.", status)
                : BackendReadiness.NotReady("TTS engine has no voices.", status);
        }
        catch (Exception ex)
        {
            var status = new BackendEngineStatus(
                SchemaVersion: 1,
                Ready: false,
                Engine: _engines.DefaultEngineName,
                EngineVersion: typeof(ITtsEngine).Assembly.GetName().Version?.ToString() ?? "",
                ModelId: _options.TtsModelId,
                InstanceId: Environment.MachineName,
                TimestampUtc: DateTimeOffset.UtcNow.ToString("O"),
                Details: new BackendEngineStatusDetails(
                    Installed: false,
                    Missing: new[] { ex.Message },
                    LastError: ex.Message));
            return BackendReadiness.NotReady(ex.Message, status);
        }
    }

    public async Task StreamSynthesisAsync(
        VoiceHostTtsRequest payload,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var engine = _engines.Resolve(payload.Engine);
        var voiceId = FirstNonEmpty(payload.VoiceId, payload.Voice, _options.TtsVoiceId);
        var audio = await engine.SynthesizeAsync(
            payload.Text,
            voiceId,
            new TtsSynthesisOptions(),
            cancellationToken).ConfigureAwait(false);

        response.ContentType = "audio/wav";
        response.ContentLength = 44L + audio.Pcm16.Length;
        response.Headers["X-Sample-Rate"] = audio.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        response.Headers["X-Channels"] = audio.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture);
        response.Headers["X-Format"] = "pcm_s16le";

        await WavResponseWriter.WritePcm16Async(response.Body, audio, cancellationToken).ConfigureAwait(false);
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate.Trim();
        }

        return "";
    }
}

internal static class WavResponseWriter
{
    public static async Task WritePcm16Async(Stream output, TtsAudio audio, CancellationToken cancellationToken)
    {
        var header = CreateHeader(audio.Pcm16.Length, audio.SampleRate, audio.Channels);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(audio.Pcm16, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] CreateHeader(int dataLength, int sampleRate, int channels)
    {
        var header = new byte[44];
        WriteAscii(header, 0, "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 36 + dataLength);
        WriteAscii(header, 8, "WAVE");
        WriteAscii(header, 12, "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), sampleRate * channels * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), (short)(channels * sizeof(short)));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        WriteAscii(header, 36, "data");
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), dataLength);
        return header;
    }

    private static void WriteAscii(byte[] buffer, int offset, string value)
    {
        for (var i = 0; i < value.Length; i++)
            buffer[offset + i] = (byte)value[i];
    }
}
