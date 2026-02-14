using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SirThaddeus.VoiceHost.Backends;

public sealed class YouTubeJobsProxyBackend : IYouTubeJobsBackend
{
    private readonly HttpClient _httpClient;
    private readonly VoiceHostRuntimeOptions _options;

    public YouTubeJobsProxyBackend(HttpClient httpClient, VoiceHostRuntimeOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ProxyJsonResult> StartJobAsync(
        object payload,
        string requestId,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildBackendUri("/api/youtube/transcribe"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
        return await SendAsync(request, cancellationToken);
    }

    public async Task<ProxyJsonResult> GetJobAsync(
        string jobId,
        string requestId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildBackendUri($"/api/jobs/{Uri.EscapeDataString(jobId)}"));
        request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
        return await SendAsync(request, cancellationToken);
    }

    public async Task<ProxyJsonResult> CancelJobAsync(
        string jobId,
        string requestId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildBackendUri($"/api/jobs/{Uri.EscapeDataString(jobId)}/cancel"));
        request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return await SendAsync(request, cancellationToken);
    }

    private async Task<ProxyJsonResult> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType))
            mediaType = "application/json";
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            // Keep proxy contract JSON for frontend parsing stability.
            body = JsonSerializer.Serialize(new
            {
                error = "Unexpected upstream content type.",
                errorCode = "upstream_content_type_invalid",
                contentType = mediaType,
                payload = body
            });
            mediaType = "application/json";
        }
        return new ProxyJsonResult((int)response.StatusCode, mediaType, body);
    }

    private Uri BuildBackendUri(string relativePath)
    {
        var upstream = _options.AsrUpstreamUri;
        var builder = new UriBuilder(upstream.Scheme, upstream.Host, upstream.Port, relativePath);
        return builder.Uri;
    }
}
