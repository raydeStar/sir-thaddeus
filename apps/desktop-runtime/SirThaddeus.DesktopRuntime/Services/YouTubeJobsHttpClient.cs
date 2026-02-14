using System.Net.Http;
using System.Text;
using System.Text.Json;
using SirThaddeus.AuditLog;

namespace SirThaddeus.DesktopRuntime.Services;

public sealed class YouTubeJobsHttpClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Func<string> _baseUrlProvider;
    private readonly IAuditLogger _auditLogger;
    private bool _disposed;

    public YouTubeJobsHttpClient(
        Func<string> baseUrlProvider,
        IAuditLogger auditLogger,
        HttpMessageHandler? handler = null)
    {
        _baseUrlProvider = baseUrlProvider ?? throw new ArgumentNullException(nameof(baseUrlProvider));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
    }

    public async Task<YouTubeStartJobResult> StartJobAsync(
        string videoUrl,
        string? languageHint,
        bool keepAudio,
        string? asrProvider,
        string? asrModel,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var requestId = $"youtube-start-{Guid.NewGuid():N}";
        var endpoint = BuildEndpoint("/api/youtube/transcribe");
        var payload = JsonSerializer.Serialize(new
        {
            videoUrl,
            languageHint,
            keepAudio,
            asrProvider,
            asrModel
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);

        _auditLogger.Append(new AuditEvent
        {
            Actor = "user",
            Action = "YOUTUBE_JOB_START_REQUESTED",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["requestId"] = requestId,
                ["endpoint"] = endpoint,
                ["videoUrl"] = RedactUrl(videoUrl),
                ["keepAudio"] = keepAudio
            }
        });

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"YouTube job start failed ({(int)response.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<YouTubeStartJobResponse>(body, JsonOptions)
                     ?? throw new InvalidOperationException("YouTube start response was empty.");
        return new YouTubeStartJobResult(
            parsed.JobId ?? "",
            parsed.VideoId ?? "",
            parsed.OutputDir ?? "",
            parsed.RequestId ?? requestId);
    }

    public async Task<YouTubeJobSnapshot> GetJobAsync(string jobId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var requestId = $"youtube-poll-{Guid.NewGuid():N}";
        var endpoint = BuildEndpoint($"/api/jobs/{Uri.EscapeDataString(jobId)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"YouTube job status failed ({(int)response.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<YouTubeJobResponse>(body, JsonOptions)
                     ?? throw new InvalidOperationException("YouTube status response was empty.");

        return ToSnapshot(parsed);
    }

    public async Task<YouTubeJobSnapshot> CancelJobAsync(string jobId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var requestId = $"youtube-cancel-{Guid.NewGuid():N}";
        var endpoint = BuildEndpoint($"/api/jobs/{Uri.EscapeDataString(jobId)}/cancel");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"YouTube job cancel failed ({(int)response.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<YouTubeJobResponse>(body, JsonOptions)
                     ?? throw new InvalidOperationException("YouTube cancel response was empty.");
        return ToSnapshot(parsed);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _httpClient.Dispose();
    }

    private string BuildEndpoint(string path)
    {
        var baseUrl = _baseUrlProvider.Invoke();
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = "http://127.0.0.1:17845";
        return baseUrl.TrimEnd('/') + path;
    }

    private static string RedactUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        try
        {
            var uri = new Uri(value);
            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
        }
        catch
        {
            return value.Length <= 64 ? value : value[..64];
        }
    }

    private static YouTubeJobSnapshot ToSnapshot(YouTubeJobResponse response)
    {
        var error = response.Error is null
            ? null
            : new YouTubeJobError(
                response.Error.Code ?? "",
                response.Error.Message ?? "",
                response.Error.Details?.ToString() ?? "");

        return new YouTubeJobSnapshot(
            JobId: response.JobId ?? "",
            Status: response.Status ?? "",
            Stage: response.Stage ?? "",
            Progress: response.Progress,
            VideoId: response.Video?.VideoId ?? "",
            Title: response.Video?.Title ?? "",
            Channel: response.Video?.Channel ?? "",
            TranscriptPath: response.TranscriptPath ?? "",
            OutputDir: response.OutputDir ?? "",
            Summary: response.Summary,
            Error: error);
    }
}

public sealed record YouTubeStartJobResult(
    string JobId,
    string VideoId,
    string OutputDir,
    string RequestId);

public sealed record YouTubeJobSnapshot(
    string JobId,
    string Status,
    string Stage,
    double Progress,
    string VideoId,
    string Title,
    string Channel,
    string TranscriptPath,
    string OutputDir,
    string? Summary,
    YouTubeJobError? Error)
{
    public bool IsTerminal =>
        string.Equals(Status, "Done", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Status, "Cancelled", StringComparison.OrdinalIgnoreCase);
}

public sealed record YouTubeJobError(string Code, string Message, string Details);

internal sealed record YouTubeStartJobResponse
{
    public string? RequestId { get; init; }
    public string? JobId { get; init; }
    public string? VideoId { get; init; }
    public string? OutputDir { get; init; }
}

internal sealed record YouTubeJobResponse
{
    public string? RequestId { get; init; }
    public string? JobId { get; init; }
    public string? Status { get; init; }
    public string? Stage { get; init; }
    public double Progress { get; init; }
    public YouTubeVideoInfo? Video { get; init; }
    public string? TranscriptPath { get; init; }
    public string? OutputDir { get; init; }
    public string? Summary { get; init; }
    public YouTubeErrorResponse? Error { get; init; }
}

internal sealed record YouTubeVideoInfo
{
    public string? VideoId { get; init; }
    public string? Title { get; init; }
    public string? Channel { get; init; }
}

internal sealed record YouTubeErrorResponse
{
    public string? Code { get; init; }
    public string? Message { get; init; }
    public JsonElement? Details { get; init; }
}
