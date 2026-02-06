using System.Net;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

// ─────────────────────────────────────────────────────────────────────────
// OpenAI-Compatible Embeddings Client Tests
//
// Validates that the embeddings client correctly parses /v1/embeddings
// responses and falls back gracefully on any failure mode. Works with
// any provider implementing the OpenAI embeddings API contract.
// ─────────────────────────────────────────────────────────────────────────

public class OpenAiEmbeddingClientTests
{
    [Fact]
    public async Task ParsesEmbeddingResponse_Successfully()
    {
        const string responseBody = """
            {
                "data": [
                    {
                        "embedding": [0.1, 0.2, 0.3, 0.4],
                        "index": 0
                    }
                ],
                "model": "test-model",
                "usage": { "prompt_tokens": 5, "total_tokens": 5 }
            }
            """;

        var handler = new SingleResponseHandler(HttpStatusCode.OK, responseBody);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        using var client = new OpenAiEmbeddingClient("http://localhost:1234", "test-model", http);

        var result = await client.EmbedAsync("hello world");

        Assert.NotNull(result);
        Assert.Equal(4, result.Length);
        Assert.Equal(0.1f, result[0], precision: 5);
        Assert.Equal(0.4f, result[3], precision: 5);
    }

    [Fact]
    public async Task ReturnsNull_OnHttpError()
    {
        var handler = new SingleResponseHandler(HttpStatusCode.InternalServerError, "error");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        using var client = new OpenAiEmbeddingClient("http://localhost:1234", "test-model", http);

        var result = await client.EmbedAsync("hello world");

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_OnMalformedResponse_NoData()
    {
        const string responseBody = """{"model": "test"}""";

        var handler = new SingleResponseHandler(HttpStatusCode.OK, responseBody);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        using var client = new OpenAiEmbeddingClient("http://localhost:1234", "test-model", http);

        var result = await client.EmbedAsync("hello world");

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_OnMalformedResponse_EmptyEmbedding()
    {
        const string responseBody = """
            {
                "data": [
                    {
                        "embedding": [],
                        "index": 0
                    }
                ]
            }
            """;

        var handler = new SingleResponseHandler(HttpStatusCode.OK, responseBody);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        using var client = new OpenAiEmbeddingClient("http://localhost:1234", "test-model", http);

        var result = await client.EmbedAsync("hello world");

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_OnEmptyInput()
    {
        var handler = new SingleResponseHandler(HttpStatusCode.OK, "{}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        using var client = new OpenAiEmbeddingClient("http://localhost:1234", "test-model", http);

        var result = await client.EmbedAsync("   ");

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_OnNetworkException()
    {
        var handler = new ThrowingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        using var client = new OpenAiEmbeddingClient("http://localhost:1234", "test-model", http);

        var result = await client.EmbedAsync("hello world");

        Assert.Null(result);
    }

    // ── Test helpers ─────────────────────────────────────────────────

    private sealed class SingleResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public SingleResponseHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body   = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body)
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Connection refused");
        }
    }
}
