namespace SirThaddeus.Agent.Search;

// ─────────────────────────────────────────────────────────────────────────
// Story Clustering — Groups News Results by Topic Similarity
//
// Internal step of the NewsPipeline. Runs after search results arrive
// and before presentation. Groups related articles into story buckets
// so the user sees "3 stories, each with 2-3 sources" instead of a
// flat list of 8 articles about 3 different topics.
//
// Algorithm: Jaccard similarity on normalized title word sets.
//   - O(n^2) pairwise comparison (fine for n <= 15 results)
//   - Greedy clustering with configurable threshold
//   - Deterministic (same input → same output)
//   - No embeddings, no external dependencies
// ─────────────────────────────────────────────────────────────────────────

public static class StoryClustering
{
    /// <summary>
    /// Minimum Jaccard similarity to merge two results into the same
    /// cluster. 0.3 is permissive enough to group "Japan earthquake
    /// kills 5" with "Earthquake hits central Japan, casualties reported"
    /// while keeping "Stock market drops" separate.
    /// </summary>
    public const double DefaultThreshold = 0.3;

    /// <summary>English stopwords to exclude from similarity computation.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "in", "on", "at", "to",
        "for", "of", "with", "by", "from", "is", "are", "was", "were",
        "be", "been", "has", "have", "had", "do", "does", "did",
        "will", "would", "could", "should", "may", "might",
        "it", "its", "he", "she", "they", "them", "his", "her",
        "this", "that", "these", "those", "not", "no", "as",
        "up", "out", "about", "into", "over", "after", "before",
        "says", "said", "new", "how", "what", "when", "where", "why",
        "who", "which", "than", "more", "most", "also", "just"
    };

    /// <summary>
    /// Groups a list of source items into story clusters.
    /// Each cluster contains related articles about the same topic.
    /// </summary>
    public static List<StoryCluster> Cluster(
        IReadOnlyList<SourceItem> items,
        double threshold = DefaultThreshold)
    {
        if (items.Count == 0)
            return [];

        // Pre-compute word sets for each item
        var wordSets = items
            .Select(item => ExtractWordSet(item.Title))
            .ToList();

        var clusters = new List<(List<int> Indices, HashSet<string> Words)>();

        for (var i = 0; i < items.Count; i++)
        {
            var bestCluster = -1;
            var bestSim     = 0.0;

            // Find the best existing cluster for this item
            for (var c = 0; c < clusters.Count; c++)
            {
                var sim = JaccardSimilarity(wordSets[i], clusters[c].Words);
                if (sim > bestSim)
                {
                    bestSim     = sim;
                    bestCluster = c;
                }
            }

            if (bestSim >= threshold && bestCluster >= 0)
            {
                // Merge into existing cluster
                clusters[bestCluster].Indices.Add(i);
                clusters[bestCluster].Words.UnionWith(wordSets[i]);
            }
            else
            {
                // Start a new cluster
                clusters.Add(([i], new HashSet<string>(wordSets[i], StringComparer.OrdinalIgnoreCase)));
            }
        }

        // Convert to StoryCluster records, sorted by cluster size (largest first)
        return clusters
            .OrderByDescending(c => c.Indices.Count)
            .Select(c => new StoryCluster
            {
                // Use the first item's title as the representative
                RepresentativeTitle = items[c.Indices[0]].Title,
                Sources = c.Indices.Select(i => items[i]).ToList()
            })
            .ToList();
    }

    /// <summary>
    /// Extracts a normalized word set from a title: lowercase, split
    /// on non-alphanumeric, remove stopwords and short tokens.
    /// </summary>
    internal static HashSet<string> ExtractWordSet(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return [];

        var words = title
            .ToLowerInvariant()
            .Split(WordSplitChars, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .Select(StemBasic);

        return new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Jaccard similarity: |A ∩ B| / |A ∪ B|.
    /// Returns 0.0 if both sets are empty.
    /// </summary>
    internal static double JaccardSimilarity(
        HashSet<string> a,
        HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
            return 0.0;

        var intersection = a.Count(x => b.Contains(x));
        var union        = a.Count + b.Count - intersection;

        return union == 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>
    /// Minimal suffix-based stemming. Handles common English endings
    /// so "earthquake" and "earthquakes" cluster together.
    /// Not a full Porter stemmer — keeps it simple and predictable.
    /// </summary>
    private static string StemBasic(string word)
    {
        if (word.Length <= 4)
            return word;

        if (word.EndsWith("ing", StringComparison.Ordinal) && word.Length > 5)
            return word[..^3];
        if (word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 4)
            return word[..^3] + "y";
        if (word.EndsWith("es", StringComparison.Ordinal) && word.Length > 4)
            return word[..^2];
        if (word.EndsWith("ed", StringComparison.Ordinal) && word.Length > 4)
            return word[..^2];
        if (word.EndsWith("ly", StringComparison.Ordinal) && word.Length > 4)
            return word[..^2];
        if (word.EndsWith("s", StringComparison.Ordinal) && word.Length > 3)
            return word[..^1];

        return word;
    }

    private static readonly char[] WordSplitChars =
        [' ', '-', '–', '—', ',', '.', ':', ';', '!', '?', '\'', '"', '(', ')', '[', ']'];
}
