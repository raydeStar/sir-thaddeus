using System.Net.Http;
using System.Text;
using System.Text.Json;
using SirThaddeus.VoiceHost.Models;

namespace SirThaddeus.VoiceHost.Backends;

public sealed class TtsProxyBackend : ITtsBackend
{
    private readonly HttpClient _httpClient;
    private readonly VoiceHostRuntimeOptions _options;

    public TtsProxyBackend(HttpClient httpClient, VoiceHostRuntimeOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<BackendReadiness> GetReadinessAsync(CancellationToken cancellationToken)
        => BackendHealthProbe.ProbeAsync(_httpClient, _options.TtsUpstreamUri, cancellationToken);

    public async Task StreamSynthesisAsync(
        VoiceHostTtsRequest payload,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            text = payload.Text,
            requestId = payload.RequestId,
            engine = string.IsNullOrWhiteSpace(payload.Engine) ? _options.TtsEngine : payload.Engine,
            modelId = string.IsNullOrWhiteSpace(payload.ModelId) ? _options.TtsModelId : payload.ModelId,
            voiceId = string.IsNullOrWhiteSpace(payload.VoiceId) ? payload.Voice : payload.VoiceId,
            voice = payload.Voice,
            format = payload.Format,
            sampleRate = payload.SampleRate
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TtsUpstreamUri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(payload.RequestId))
            request.Headers.TryAddWithoutValidation("X-Request-Id", payload.RequestId);

        using var upstreamResponse = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!upstreamResponse.IsSuccessStatusCode)
        {
            var errorText = await upstreamResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new BackendProxyException(
                $"TTS upstream failed ({(int)upstreamResponse.StatusCode}): {errorText}");
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "audio/wav";
        CopyMetadataHeaders(upstreamResponse, response, payload);

        await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
        await upstreamStream.CopyToAsync(response.Body, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static void CopyMetadataHeaders(
        HttpResponseMessage upstream,
        HttpResponse response,
        VoiceHostTtsRequest payload)
    {
        if (upstream.Headers.TryGetValues("X-Sample-Rate", out var sampleRateValues))
            response.Headers["X-Sample-Rate"] = sampleRateValues.ToArray();
        else
            response.Headers["X-Sample-Rate"] = payload.SampleRate.ToString();

        if (upstream.Headers.TryGetValues("X-Channels", out var channelValues))
            response.Headers["X-Channels"] = channelValues.ToArray();
        else
            response.Headers["X-Channels"] = "1";

        if (upstream.Headers.TryGetValues("X-Format", out var formatValues))
            response.Headers["X-Format"] = formatValues.ToArray();
        else
            response.Headers["X-Format"] = payload.Format;

        if (upstream.Headers.TryGetValues("X-Request-Id", out var requestIdValues))
            response.Headers["X-Request-Id"] = requestIdValues.ToArray();
        else if (!string.IsNullOrWhiteSpace(payload.RequestId))
            response.Headers["X-Request-Id"] = payload.RequestId;
    }
}
