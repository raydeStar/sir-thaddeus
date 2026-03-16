using System.Text;
using System.Text.RegularExpressions;

namespace SirThaddeus.UI.Avalonia;

internal sealed class AttachedDocumentContext
{
    public const int InlineThreshold = 2500;

    private const int ChunkSize = 500;
    private const int ChunkOverlap = 80;
    private const int MaxChunksToReturn = 8;
    private const int MaxContextChars = 4000;

    private static readonly HashSet<string> StopWords =
    [
        "the", "and", "for", "are", "but", "not", "you", "all",
        "can", "had", "her", "was", "one", "our", "out", "has",
        "have", "been", "this", "that", "with", "from", "they",
        "will", "would", "there", "their", "what", "about", "which",
        "when", "make", "like", "than", "each", "just", "also",
        "into", "over", "such", "some", "could", "them", "then"
    ];

    private readonly IReadOnlyList<string> _chunks;

    public AttachedDocumentContext(string fileName, string rawContent)
    {
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        RawContent = rawContent ?? throw new ArgumentNullException(nameof(rawContent));
        _chunks = IsSmall ? [] : BuildChunks(rawContent);
    }

    public string FileName { get; }

    public string RawContent { get; }

    public bool IsSmall => RawContent.Length < InlineThreshold;

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
            sb.AppendLine($"(Large document - {RawContent.Length:N0} characters, showing relevant excerpts)");
            sb.AppendLine();

            var totalChars = 0;
            foreach (var (chunk, _) in RankChunks(userQuery))
            {
                if (totalChars + chunk.Length > MaxContextChars)
                {
                    break;
                }

                sb.AppendLine(chunk);
                sb.AppendLine("---");
                totalChars += chunk.Length;
            }
        }

        sb.AppendLine("[END DOCUMENT]");
        return sb.ToString();
    }

    private static IReadOnlyList<string> BuildChunks(string text)
    {
        var chunks = new List<string>();
        var pos = 0;

        while (pos < text.Length)
        {
            var end = Math.Min(pos + ChunkSize, text.Length);

            if (end < text.Length)
            {
                var span = Math.Min(end - pos, 100);
                var breakIndex = text.LastIndexOfAny(['\n', '.', '!', '?'], end - 1, span);
                if (breakIndex > pos + (ChunkSize / 2))
                {
                    end = breakIndex + 1;
                }
            }

            var chunk = text[pos..end].Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            pos = Math.Max(0, end - ChunkOverlap);
            if (end >= text.Length)
            {
                break;
            }
        }

        return chunks;
    }

    private IReadOnlyList<(string Chunk, double Score)> RankChunks(string query)
    {
        var queryTerms = Tokenize(query);
        if (queryTerms.Count == 0)
        {
            return _chunks
                .Take(MaxChunksToReturn)
                .Select(chunk => (chunk, 1.0))
                .ToArray();
        }

        var ranked = _chunks
            .Select(chunk =>
            {
                var terms = Tokenize(chunk);
                return (Chunk: chunk, Score: ComputeOverlapScore(queryTerms, terms));
            })
            .OrderByDescending(item => item.Score)
            .Take(MaxChunksToReturn)
            .ToArray();

        if (ranked.All(item => item.Score <= 0))
        {
            return _chunks
                .Take(MaxChunksToReturn)
                .Select(chunk => (chunk, 0.0))
                .ToArray();
        }

        return ranked;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var words = Regex.Split((text ?? string.Empty).ToLowerInvariant(), "\\W+")
            .Where(word => word.Length > 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        words.ExceptWith(StopWords);
        return words;
    }

    private static double ComputeOverlapScore(HashSet<string> queryTerms, HashSet<string> chunkTerms)
    {
        if (chunkTerms.Count == 0 || queryTerms.Count == 0)
        {
            return 0;
        }

        var overlap = queryTerms.Count(term => chunkTerms.Contains(term));
        return (double)overlap / queryTerms.Count * Math.Min(1.0, chunkTerms.Count / 10.0);
    }
}
