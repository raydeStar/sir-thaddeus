using System.Net;
using System.Net.Http;
using System.Text;
using SirThaddeus.AuditLog;
using SirThaddeus.DesktopRuntime.Services;

namespace SirThaddeus.Tests.Voice;

public sealed class YouTubeJobsHttpClientTests
{
    [Fact]
    public async Task StartJobAsync_ParsesStartPayload()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "requestId": "req-1",
                      "jobId": "job-1",
                      "videoId": "abc123",
                      "outputDir": "data/youtube/abc123"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        using var client = new YouTubeJobsHttpClient(
            () => "http://127.0.0.1:17845",
            new TestAuditLogger(),
            handler);

        var result = await client.StartJobAsync(
            videoUrl: "https://www.youtube.com/watch?v=abc123",
            languageHint: null,
            keepAudio: false,
            asrProvider: "qwen3asr",
            asrModel: "qwen-asr-1.6b",
            cancellationToken: CancellationToken.None);

        Assert.Equal("job-1", result.JobId);
        Assert.Equal("abc123", result.VideoId);
        Assert.Contains("/api/youtube/transcribe", handler.LastUri?.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
    }

    [Fact]
    public async Task GetJobAsync_CancelledResponse_IsTerminal()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "jobId": "job-2",
                      "status": "Cancelled",
                      "stage": "Cancelled",
                      "progress": 0.55,
                      "video": { "videoId": "xyz", "title": "x", "channel": "y", "durationSec": 10 },
                      "transcriptPath": "data/youtube/xyz/transcript.txt",
                      "summary": null,
                      "error": {
                        "code": "JOB_CANCELLED",
                        "message": "Job cancelled by user.",
                        "details": {}
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        using var client = new YouTubeJobsHttpClient(
            () => "http://127.0.0.1:17845",
            new TestAuditLogger(),
            handler);

        var snapshot = await client.GetJobAsync("job-2", CancellationToken.None);

        Assert.Equal("job-2", snapshot.JobId);
        Assert.Equal("Cancelled", snapshot.Status);
        Assert.True(snapshot.IsTerminal);
        Assert.NotNull(snapshot.Error);
        Assert.Equal("JOB_CANCELLED", snapshot.Error!.Code);
    }

    [Fact]
    public async Task StartJobAsync_ErrorResponse_ThrowsWithStatusCode()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """
                    {
                      "error": { "code": "INVALID_URL", "message": "Bad URL", "details": {} }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        using var client = new YouTubeJobsHttpClient(
            () => "http://127.0.0.1:17845",
            new TestAuditLogger(),
            handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartJobAsync(
            videoUrl: "bad-url",
            languageHint: null,
            keepAudio: false,
            asrProvider: null,
            asrModel: null,
            cancellationToken: CancellationToken.None));

        Assert.Contains("400", ex.Message);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
            => _factory = factory;

        public Uri? LastUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastMethod = request.Method;
            return Task.FromResult(_factory(request));
        }
    }

    private sealed class TestAuditLogger : IAuditLogger
    {
        public void Append(AuditEvent auditEvent)
        {
            // no-op for transport contract tests
        }

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public IReadOnlyList<AuditEvent> ReadTail(int maxEvents)
            => Array.Empty<AuditEvent>();

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int maxEvents, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AuditEvent>>(Array.Empty<AuditEvent>());
    }
}
