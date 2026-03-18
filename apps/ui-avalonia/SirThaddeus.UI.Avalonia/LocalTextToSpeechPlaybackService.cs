using System.Net;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using SirThaddeus.Config;

namespace SirThaddeus.UI.Avalonia;

/// <summary>
/// TTS playback service that calls the VoiceHost /tts HTTP endpoint (Piper, Kokoro, etc.)
/// and plays the returned WAV audio via NAudio.
/// </summary>
internal sealed class LocalTextToSpeechPlaybackService : IDisposable
{
    private const string DefaultPiperVoiceId = "en_US-john-medium";
    private const string DefaultKokoroVoiceId = "bm_lewis";

    private readonly HttpClient _httpClient = new();
    private readonly Func<string> _baseUrlProvider;
    private readonly Func<VoiceSettings> _voiceSettingsProvider;
    private bool _disposed;

    public int OutputDeviceNumber { get; set; } = -1;

    public LocalTextToSpeechPlaybackService(
        Func<string> baseUrlProvider,
        Func<VoiceSettings> voiceSettingsProvider)
    {
        _baseUrlProvider = baseUrlProvider ?? throw new ArgumentNullException(nameof(baseUrlProvider));
        _voiceSettingsProvider = voiceSettingsProvider ?? throw new ArgumentNullException(nameof(voiceSettingsProvider));
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(text))
            return;

        var wavBytes = await SynthesizeAsync(text, cancellationToken);
        if (wavBytes is null || wavBytes.Length == 0)
            throw new InvalidOperationException("TTS returned no audio data.");

        await PlayWavBytesAsync(wavBytes, cancellationToken);
    }

    private async Task<byte[]?> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        var voiceSettings = GetVoiceSettingsSnapshot();
        var endpoint = BuildEndpointUrl();
        var engine = voiceSettings.GetNormalizedTtsEngine();
        var voiceId = ResolveEffectiveVoiceId(voiceSettings);
        var requestId = $"tts-{Guid.NewGuid():N}";

        var payload = JsonSerializer.Serialize(new
        {
            text,
            requestId,
            engine,
            modelId = voiceSettings.GetResolvedTtsModelId(),
            voiceId,
            voice = voiceId,
            format = "pcm_s16le",
            sampleRate = 24000
        });

        // Retry for startup warmup (503/502/504) up to 30 seconds.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        var attempt = 0;

        while (true)
        {
            attempt++;
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);

            try
            {
                using var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (IsRetryableStatus(response.StatusCode) && DateTimeOffset.UtcNow < deadline)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(150 * Math.Clamp(attempt, 1, 6)), cancellationToken);
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"TTS request failed ({(int)response.StatusCode}): {Truncate(errorBody, 300)}");
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                var bodyBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

                if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                    return bodyBytes;

                // Some backends return JSON with base64-encoded audio.
                return TryExtractAudioFromJson(bodyBytes) ?? bodyBytes;
            }
            catch (HttpRequestException) when (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * Math.Clamp(attempt, 1, 6)), cancellationToken);
            }
        }
    }

    private async Task PlayWavBytesAsync(byte[] wavBytes, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(wavBytes);
        using var reader = new WaveFileReader(stream);
        using var output = new WaveOutEvent { DeviceNumber = OutputDeviceNumber };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is not null)
                completion.TrySetException(args.Exception);
            else
                completion.TrySetResult();
        };

        using var reg = cancellationToken.Register(static state =>
        {
            try { ((WaveOutEvent)state!).Stop(); } catch { }
        }, output);

        output.Init(reader);
        output.Play();

        try
        {
            await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private string BuildEndpointUrl()
    {
        var baseUrl = _baseUrlProvider();
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = "http://127.0.0.1:17845";
        return baseUrl.TrimEnd('/') + "/tts";
    }

    private VoiceSettings GetVoiceSettingsSnapshot()
    {
        try { return _voiceSettingsProvider() ?? new VoiceSettings(); }
        catch { return new VoiceSettings(); }
    }

    private static string ResolveEffectiveVoiceId(VoiceSettings vs)
    {
        var resolved = vs.GetResolvedTtsVoiceId();
        if (!string.IsNullOrWhiteSpace(resolved))
            return resolved;

        var engine = vs.GetNormalizedTtsEngine();
        return engine switch
        {
            "kokoro" => DefaultKokoroVoiceId,
            "piper" => DefaultPiperVoiceId,
            _ => DefaultPiperVoiceId
        };
    }

    private static bool IsRetryableStatus(HttpStatusCode code)
        => code is HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.BadGateway
            or HttpStatusCode.GatewayTimeout;

    private static byte[]? TryExtractAudioFromJson(byte[] body)
    {
        try
        {
            var json = Encoding.UTF8.GetString(body);
            if (!json.TrimStart().StartsWith('{'))
                return null;

            using var doc = JsonDocument.Parse(json);
            foreach (var prop in new[] { "audioBase64", "audio", "data" })
            {
                if (doc.RootElement.TryGetProperty(prop, out var el) &&
                    el.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(el.GetString()))
                {
                    var bytes = Convert.FromBase64String(el.GetString()!);
                    if (bytes.Length > 0) return bytes;
                }
            }
        }
        catch { }

        return null;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
    }
}
