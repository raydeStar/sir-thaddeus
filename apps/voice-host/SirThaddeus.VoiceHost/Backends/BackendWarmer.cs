using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Thaddeus.Tts.Abstractions;

namespace SirThaddeus.VoiceHost.Backends;

/// <summary>
/// Eagerly warms the ASR and TTS pipelines once the upstream backend is up,
/// so the user's first push-to-talk doesn't pay the multi-second cost of
/// faster-whisper's lazy WhisperModel load and KokoroSharp's first ONNX
/// session creation. Failures here are non-fatal &mdash; they just mean the
/// first real request will pay the cold-start cost the way it always did.
/// </summary>
public sealed class BackendWarmer : BackgroundService
{
    private readonly VoiceBackendSupervisor _supervisor;
    private readonly TtsEngineRegistry _ttsEngines;
    private readonly VoiceHostRuntimeOptions _options;
    private readonly ILogger<BackendWarmer> _logger;
    private readonly HttpClient _httpClient;

    public BackendWarmer(
        VoiceBackendSupervisor supervisor,
        TtsEngineRegistry ttsEngines,
        VoiceHostRuntimeOptions options,
        ILogger<BackendWarmer> logger)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _ttsEngines = ttsEngines ?? throw new ArgumentNullException(nameof(ttsEngines));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Whisper cold-load on a fresh machine can be slow; give the warm-up
        // request enough head room without holding /asr requests up.
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var ensure = await _supervisor.EnsureRunningAsync(stoppingToken).ConfigureAwait(false);
            if (!ensure.Success)
            {
                _logger.LogInformation(
                    "warmup.skip reason={Code} message={Message}",
                    ensure.ErrorCode,
                    ensure.Message);
                return;
            }

            // Run both warm-ups in parallel; neither blocks the other.
            var asrTask = WarmAsrAsync(stoppingToken);
            var ttsTask = WarmTtsAsync(stoppingToken);
            await Task.WhenAll(asrTask, ttsTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down before warm-up finished.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "warmup.unexpected_failure");
        }
    }

    private async Task WarmAsrAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var wav = BuildSilenceWav(milliseconds: 250);

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.AsrUpstreamUri);
            using var payload = new MultipartFormDataContent();
            using var audioContent = new ByteArrayContent(wav);
            using var legacyAudioContent = new ByteArrayContent(wav);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            legacyAudioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

            payload.Add(audioContent, "audio", "warmup.wav");
            payload.Add(legacyAudioContent, "file", "warmup.wav");
            payload.Add(new StringContent(_options.SttEngine), "engine");
            if (!string.IsNullOrWhiteSpace(_options.SttModelId))
                payload.Add(new StringContent(_options.SttModelId), "modelId");
            if (!string.IsNullOrWhiteSpace(_options.SttLanguage))
                payload.Add(new StringContent(_options.SttLanguage), "language");
            payload.Add(new StringContent("warmup"), "requestId");
            request.Content = payload;
            request.Headers.TryAddWithoutValidation("X-Request-Id", "warmup");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            sw.Stop();
            _logger.LogInformation(
                "warmup.asr status={Status} elapsedMs={Ms}",
                (int)response.StatusCode,
                sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogInformation("warmup.asr timeout elapsedMs={Ms}", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogInformation(
                ex,
                "warmup.asr failed (non-fatal) elapsedMs={Ms}",
                sw.ElapsedMilliseconds);
        }
    }

    private async Task WarmTtsAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var engine = _ttsEngines.Resolve(_options.TtsEngine);
            // Triggering ListVoicesAsync before SynthesizeAsync ensures the
            // voice pack is loaded; KokoroSharp will reuse the cached map.
            await engine.ListVoicesAsync(ct).ConfigureAwait(false);
            await engine.SynthesizeAsync(
                "Ready.",
                _options.TtsVoiceId,
                new TtsSynthesisOptions(),
                ct).ConfigureAwait(false);
            sw.Stop();
            _logger.LogInformation(
                "warmup.tts engine={Engine} voice={Voice} elapsedMs={Ms}",
                engine.EngineName,
                _options.TtsVoiceId,
                sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogInformation(
                ex,
                "warmup.tts failed (non-fatal) elapsedMs={Ms}",
                sw.ElapsedMilliseconds);
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Builds a tiny 16kHz mono 16-bit PCM WAV containing only zeros (silence)
    /// suitable for forcing the upstream ASR backend to load its model. The
    /// payload is intentionally minimal; faster-whisper short-circuits empty
    /// audio but still completes the model load on the first call.
    /// </summary>
    private static byte[] BuildSilenceWav(int milliseconds)
    {
        const int sampleRate = 16_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        var samples = sampleRate * milliseconds / 1000;
        var dataSize = samples * channels * (bitsPerSample / 8);
        var buf = new byte[44 + dataSize];

        Encoding.ASCII.GetBytes("RIFF").CopyTo(buf, 0);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), 36 + dataSize);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(buf, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(buf, 12);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(22), channels);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(28), sampleRate * channels * (bitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(32), (short)(channels * (bitsPerSample / 8)));
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(34), bitsPerSample);
        Encoding.ASCII.GetBytes("data").CopyTo(buf, 36);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(40), dataSize);
        // Sample data is left as zeros (silence).
        return buf;
    }
}
