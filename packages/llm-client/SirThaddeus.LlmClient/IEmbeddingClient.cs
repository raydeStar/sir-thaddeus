namespace SirThaddeus.LlmClient;

/// <summary>
/// Abstraction for generating text embeddings via an OpenAI-compatible
/// endpoint. Used by the memory retrieval engine for optional semantic
/// reranking.
///
/// Implementations should fail gracefully — return null on any error.
/// The caller treats null as "embeddings unavailable" and falls back
/// to BM25-only retrieval.
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>
    /// Generates an embedding vector for the given text.
    /// Returns null if the endpoint is unavailable or the request fails.
    /// </summary>
    Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
