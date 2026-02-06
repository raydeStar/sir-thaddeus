using System.Net.Http.Json;
using System.Text.Json;

namespace SirThaddeus.LlmClient;

/// <summary>
/// Calls /v1/embeddings on any OpenAI-compatible endpoint — works with
/// LM Studio, Ollama, vLLM, llama.cpp, LocalAI, or the actual OpenAI
/// API. Returns null on any failure so the caller can fall back to
/// BM25-only retrieval.
///
/// Bounds:
///   - 3-second timeout per request (local endpoints respond instantly or not at all)
///   - Single text input per call
///   - Returns float[] or null (never throws to the caller)
///   - Auto-backoff after 2 consecutive failures (5-min cooldown)
/// </summary>
public sealed class OpenAiEmbeddingClient : IEmbeddingClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly bool _ownsClient;

    // Once we detect the endpoint is down, stop hammering it.
    // Resets after the cooldown in case the user configures or
    // starts an embedding-capable server later.
    private int _consecutiveFailures;
    private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;
    private const int FailuresBeforeBackoff = 2;
    private static readonly TimeSpan BackoffDuration = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public OpenAiEmbeddingClient(
        string baseUrl, string model, HttpClient? httpClient = null)
    {
        _model      = model ?? "local-model";
        _ownsClient = httpClient is null;
        _http       = httpClient ?? new HttpClient();
        _http.BaseAddress ??= new Uri(baseUrl.TrimEnd('/'));
        _http.Timeout = RequestTimeout;
    }

    /// <inheritdoc />
    public async Task<float[]?> EmbedAsync(
        string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // If we've failed repeatedly, skip until the cooldown expires.
        // Prevents 3-second waits on every message when no embedding
        // endpoint is available.
        if (_consecutiveFailures >= FailuresBeforeBackoff &&
            DateTimeOffset.UtcNow < _backoffUntil)
            return null;

        try
        {
            var body = new { model = _model, input = new[] { text } };

            using var cts = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(RequestTimeout);

            var response = await _http.PostAsJsonAsync(
                "/v1/embeddings", body, JsonOpts, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                RecordFailure();
                return null;
            }

            var raw = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(raw);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0)
                return null;

            var first = data[0];
            if (!first.TryGetProperty("embedding", out var embeddingEl) ||
                embeddingEl.ValueKind != JsonValueKind.Array)
                return null;

            var length    = embeddingEl.GetArrayLength();
            var embedding = new float[length];

            for (var i = 0; i < length; i++)
                embedding[i] = embeddingEl[i].GetSingle();

            if (length > 0)
            {
                // Success — reset the failure counter
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                return embedding;
            }

            RecordFailure();
            return null;
        }
        catch
        {
            // Any failure = embeddings unavailable. Fall back to BM25.
            RecordFailure();
            return null;
        }
    }

    private void RecordFailure()
    {
        var count = Interlocked.Increment(ref _consecutiveFailures);
        if (count >= FailuresBeforeBackoff)
            _backoffUntil = DateTimeOffset.UtcNow + BackoffDuration;
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}
