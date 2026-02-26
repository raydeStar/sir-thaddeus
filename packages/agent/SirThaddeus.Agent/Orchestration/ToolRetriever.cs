using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Orchestration;

/// <summary>
/// A semantic tool retriever that selects the best tools for the job.
/// </summary>
public sealed class ToolRetriever : IToolRetriever
{
    private readonly ITextEmbedder _embedder;
    private Dictionary<string, float[]>? _toolEmbeddings;

    public ToolRetriever(ITextEmbedder embedder)
    {
        _embedder = embedder;
    }

    public async Task<IReadOnlyList<ToolDefinition>> RetrieveAsync(
        IntentDecisionV2 decision,
        string userMessage,
        IReadOnlyList<ToolDefinition> allowedTools,
        CancellationToken cancellationToken)
    {
        if (allowedTools.Count <= 5)
        {
            // If the policy gate already narrowed it down to a small handful,
            // don't bother running semantic search.
            return allowedTools;
        }

        // Ensure every tool in the current allowed set has an embedding.
        // New tools that appear on later turns are embedded on demand.
        _toolEmbeddings ??= new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in allowedTools)
        {
            if (!_toolEmbeddings.ContainsKey(tool.Function.Name))
            {
                var textToEmbed = $"{tool.Function.Name} {tool.Function.Description}";
                _toolEmbeddings[tool.Function.Name] = await _embedder.EmbedAsync(textToEmbed, cancellationToken);
            }
        }

        // Build a query combining intent context and user message
        var query = $"{decision.Intent} {userMessage}";
        var queryEmbedding = await _embedder.EmbedAsync(query, cancellationToken);

        // Score tools
        var scoredTools = new List<(ToolDefinition Tool, float Score)>();
        foreach (var tool in allowedTools)
        {
            if (_toolEmbeddings.TryGetValue(tool.Function.Name, out var embedding))
            {
                var score = CosineSimilarity(queryEmbedding, embedding);
                scoredTools.Add((tool, score));
            }
            else
            {
                // Fallback for tools missing embeddings
                scoredTools.Add((tool, 0f));
            }
        }

        // Return top 7
        return scoredTools
            .OrderByDescending(t => t.Score)
            .Take(7)
            .Select(t => t.Tool)
            .ToList();
    }

    private static float CosineSimilarity(float[] vector1, float[] vector2)
    {
        float dotProduct = 0;
        float normA = 0;
        float normB = 0;
        var len = Math.Min(vector1.Length, vector2.Length);
        for (int i = 0; i < len; i++)
        {
            dotProduct += vector1[i] * vector2[i];
            normA += vector1[i] * vector1[i];
            normB += vector2[i] * vector2[i];
        }
        if (normA == 0 || normB == 0) return 0;
        return (float)(dotProduct / (Math.Sqrt(normA) * Math.Sqrt(normB)));
    }
}
