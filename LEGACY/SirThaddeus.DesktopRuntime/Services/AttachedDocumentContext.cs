using System.Text;
using System.Text.RegularExpressions;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Holds an attached document and provides simple keyword-based retrieval
/// for large files. Small files (&lt; threshold) are returned in full;
/// large files are chunked and the most relevant chunks are returned.
/// </summary>
public sealed class AttachedDocumentContext
{
    public const int InlineThreshold = 2_500;

    private const int ChunkSize = 500;
    private const int ChunkOverlap = 80;
    private const int MaxChunksToReturn = 8;
    private const int MaxContextChars = 4_000;

    public string FileName { get; }
    public string RawContent { get; }
    public bool IsSmall => RawContent.Length < InlineThreshold;

    private readonly List<string> _chunks;

    public AttachedDocumentContext(string fileName, string rawContent)
    {
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        RawContent = rawContent ?? throw new ArgumentNullException(nameof(rawContent));
        _chunks = IsSmall ? [] : BuildChunks(rawContent);
    }

    /// <summary>
    /// Builds the context block to inject into the user message.
    /// For small files, returns the full content.
    /// For large files, returns the top-scoring chunks for the query.
    /// </summary>
    public string BuildContextBlock(string userQuery)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[ATTACHED DOCUMENT: {FileName}]");

        if (IsSmall)
        {
            sb.AppendLine(RawContent);
        }
        else
        {
            sb.AppendLine($"(Large document — {RawContent.Length:N0} characters, showing most relevant excerpts)");
            sb.AppendLine();

            var ranked = RankChunks(userQuery);
            var totalChars = 0;

            foreach (var (chunk, _) in ranked)
            {
                if (totalChars + chunk.Length > MaxContextChars)
                    break;

                sb.AppendLine(chunk);
                sb.AppendLine("---");
                totalChars += chunk.Length;
            }
        }

        sb.AppendLine("[END DOCUMENT]");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────
    // Chunking
    // ─────────────────────────────────────────────────────────────────

    private static List<string> BuildChunks(string text)
    {
        var chunks = new List<string>();
        var pos = 0;

        while (pos < text.Length)
        {
            var end = Math.Min(pos + ChunkSize, text.Length);

            // Try to break on a sentence or paragraph boundary
            if (end < text.Length)
            {
                var lastBreak = text.LastIndexOfAny(['\n', '.', '!', '?'], end - 1, Math.Min(end - pos, 100));
                if (lastBreak > pos + ChunkSize / 2)
                    end = lastBreak + 1;
            }

            var chunk = text[pos..end].Trim();
            if (chunk.Length > 0)
                chunks.Add(chunk);

            pos = end - ChunkOverlap;
            if (pos < 0) pos = 0;
            if (end >= text.Length) break;
        }

        return chunks;
    }

    // ─────────────────────────────────────────────────────────────────
    // Simple keyword-based ranking (BM25-lite)
    // ─────────────────────────────────────────────────────────────────

    private IReadOnlyList<(string Chunk, double Score)> RankChunks(string query)
    {
        var queryTerms = Tokenize(query);
        if (queryTerms.Count == 0)
        {
            // No meaningful query — return first N chunks in order
            return _chunks
                .Take(MaxChunksToReturn)
                .Select(c => (c, 1.0))
                .ToList();
        }

        var scored = _chunks
            .Select(chunk =>
            {
                var chunkTerms = Tokenize(chunk);
                var score = ComputeOverlapScore(queryTerms, chunkTerms);
                return (Chunk: chunk, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .Take(MaxChunksToReturn)
            .ToList();

        // If all scores are 0, return the first chunks as fallback
        if (scored.All(s => s.Score <= 0))
        {
            return _chunks
                .Take(MaxChunksToReturn)
                .Select(c => (c, 0.0))
                .ToList();
        }

        return scored;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var words = Regex.Split(text.ToLowerInvariant(), @"\W+")
            .Where(w => w.Length > 2)
            .ToHashSet();

        // Remove very common stop words
        words.ExceptWith(StopWords);
        return words;
    }

    private static double ComputeOverlapScore(
        HashSet<string> queryTerms,
        HashSet<string> chunkTerms)
    {
        if (chunkTerms.Count == 0) return 0;

        var intersection = queryTerms.Count(t => chunkTerms.Contains(t));
        // Normalized overlap with slight length penalty for very short chunks
        return (double)intersection / queryTerms.Count
               * Math.Min(1.0, chunkTerms.Count / 10.0);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "all",
        "can", "had", "her", "was", "one", "our", "out", "has",
        "have", "been", "this", "that", "with", "from", "they",
        "will", "would", "there", "their", "what", "about", "which",
        "when", "make", "like", "than", "each", "just", "also",
        "into", "over", "such", "some", "could", "them", "then"
    };
}
