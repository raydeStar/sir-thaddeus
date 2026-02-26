namespace SirThaddeus.Agent.Orchestration;

/// <summary>
/// A fast text embedder interface. Can be backed by ONNX/ML.NET or an API.
/// </summary>
public interface ITextEmbedder
{
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// The Tier-2 Neural Network intent classifier.
/// Uses nearest-neighbor similarity against the <see cref="IntentExemplarBank"/>.
/// </summary>
public sealed class NnIntentClassifier
{
    private readonly ITextEmbedder _embedder;
    private Dictionary<string, float[][]>? _exemplarEmbeddings;

    public NnIntentClassifier(ITextEmbedder embedder)
    {
        _embedder = embedder;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_exemplarEmbeddings != null) return;

        _exemplarEmbeddings = new Dictionary<string, float[][]>();
        
        foreach (var kvp in IntentExemplarBank.ExemplarsByIntent)
        {
            var embeddings = new List<float[]>();
            foreach (var exemplar in kvp.Value)
            {
                embeddings.Add(await _embedder.EmbedAsync(exemplar, cancellationToken));
            }
            _exemplarEmbeddings[kvp.Key] = embeddings.ToArray();
        }
    }

    public async Task<(string Intent, float Confidence)?> ClassifyAsync(string text, CancellationToken cancellationToken)
    {
        if (_exemplarEmbeddings == null)
            await InitializeAsync(cancellationToken);

        var queryEmbedding = await _embedder.EmbedAsync(text, cancellationToken);

        string bestIntent = "GeneralTool";
        float bestScore = -1f;
        float secondBestScore = -1f;

        foreach (var kvp in _exemplarEmbeddings!)
        {
            foreach (var exemplar in kvp.Value)
            {
                var score = CosineSimilarity(queryEmbedding, exemplar);
                if (score > bestScore)
                {
                    secondBestScore = bestScore;
                    bestScore = score;
                    bestIntent = kvp.Key;
                }
                else if (score > secondBestScore)
                {
                    secondBestScore = score;
                }
            }
        }

        // If the score is too low or the top two intents are too close, return null to force LLM fallback
        if (bestScore < 0.65f || (bestScore - secondBestScore) < 0.05f)
        {
            return null;
        }

        return (bestIntent, bestScore);
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
