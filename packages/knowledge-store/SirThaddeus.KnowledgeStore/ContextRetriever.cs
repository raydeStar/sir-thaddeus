namespace SirThaddeus.KnowledgeStore;

/// <summary>
/// Three-tier context retrieval. Uses the minimum context necessary.
/// Scan tags first, load summaries second, load full content last.
/// </summary>
public sealed class ContextRetriever
{
    private readonly TagIndex _index;
    private readonly IKnowledgeStoreTools _store;
    private readonly string _rootId;

    /// <summary>Max full files to load in a single retrieval.</summary>
    private const int MaxTier3Files = 3;

    public ContextRetriever(TagIndex index, IKnowledgeStoreTools store, string rootId)
    {
        _index = index;
        _store = store;
        _rootId = rootId;
    }

    /// <summary>
    /// Retrieve context about an entity or topic, staying within
    /// the token budget. Returns the most useful information first.
    /// </summary>
    public async Task<RetrievedContext> RetrieveAsync(
        string query,
        int tokenBudget,
        RetrievalDepth maxDepth = RetrievalDepth.Summaries)
    {
        var result = new RetrievedContext();
        var tokensUsed = 0;

        // Extract searchable terms from the query
        var searchTerms = ExtractSearchTerms(query);

        // Tier 1: Find relevant files via tag + mention index
        var relevantFiles = new List<IndexEntry>();
        foreach (var term in searchTerms)
        {
            var tagged = _index.FindByTag(term);
            relevantFiles.AddRange(tagged);

            var mentioned = _index.FindByMention(term);
            relevantFiles.AddRange(mentioned);
        }

        // Deduplicate and rank by relevance
        relevantFiles = relevantFiles
            .DistinctBy(f => f.RelativePath)
            .OrderByDescending(f =>
                f.Tags.Count(t => searchTerms.Contains(t, StringComparer.OrdinalIgnoreCase)) +
                f.Mentions.Count(m => searchTerms.Contains(m, StringComparer.OrdinalIgnoreCase)))
            .ThenByDescending(f => f.Updated)
            .ToList();

        result.MatchedFiles = relevantFiles.Count;

        if (maxDepth == RetrievalDepth.TagsOnly || relevantFiles.Count == 0)
        {
            result.FileList = relevantFiles.Select(f => f.RelativePath).ToList();
            return result;
        }

        // Tier 2: Load summaries
        foreach (var file in relevantFiles)
        {
            var summaryCost = EstimateTokens(file.Summary);
            if (tokensUsed + summaryCost > tokenBudget * 0.6)
                break; // Reserve 40% of budget for potential Tier 3

            result.Summaries.Add(new FileSummary
            {
                Path = file.RelativePath,
                Summary = file.Summary,
                Type = file.Type,
                Tags = file.Tags
            });
            tokensUsed += summaryCost;
        }

        if (maxDepth == RetrievalDepth.Summaries)
            return result;

        // Tier 3: Load full content for top N most relevant files
        var remainingBudget = tokenBudget - tokensUsed;
        foreach (var file in relevantFiles.Take(MaxTier3Files))
        {
            var readResult = await _store.ReadFileAsync(_rootId, file.RelativePath);
            if (!readResult.Success || readResult.Content is null)
                continue;

            var contentCost = EstimateTokens(readResult.Content);
            if (contentCost > remainingBudget)
                break;

            result.FullContent.Add(new FileContent
            {
                Path = file.RelativePath,
                Content = readResult.Content
            });
            remainingBudget -= contentCost;
        }

        return result;
    }

    /// <summary>
    /// Format retrieved context for injection into the LLM prompt.
    /// </summary>
    public static string FormatForPrompt(RetrievedContext context, string query)
    {
        if (context.MatchedFiles == 0)
            return $"[Knowledge — {query}]\nNo matching files found.";

        var parts = new List<string> { $"[Knowledge — {query}]" };

        foreach (var summary in context.Summaries)
        {
            var typeLabel = string.IsNullOrEmpty(summary.Type) ? "" : $" ({summary.Type})";
            parts.Add($"From {summary.Path}{typeLabel}: {summary.Summary}");
        }

        foreach (var content in context.FullContent)
        {
            parts.Add($"--- Full content: {content.Path} ---\n{content.Content}");
        }

        if (context.FullContent.Count == 0 && context.Summaries.Count > 0)
        {
            parts.Add($"[{context.MatchedFiles} files matched. Summaries shown. " +
                       "Full content available via ReadFile tool.]");
        }

        return string.Join('\n', parts);
    }

    /// <summary>
    /// Extract searchable terms from a query by matching against known index keys.
    /// </summary>
    public List<string> ExtractSearchTerms(string query)
    {
        var words = query.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim(',', '.', '?', '!', '"', '\''))
            .ToArray();

        var knownKeys = _index.TagMap.Keys
            .Concat(_index.MentionMap.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matches = new List<string>();

        // Single word matches
        foreach (var word in words)
        {
            if (knownKeys.Contains(word))
                matches.Add(word);
        }

        // Two-word compound matches ("iron crown" → "iron-crown")
        for (int i = 0; i < words.Length - 1; i++)
        {
            var compound = $"{words[i]}-{words[i + 1]}";
            if (knownKeys.Contains(compound))
                matches.Add(compound);
        }

        // Three-word compounds
        for (int i = 0; i < words.Length - 2; i++)
        {
            var compound = $"{words[i]}-{words[i + 1]}-{words[i + 2]}";
            if (knownKeys.Contains(compound))
                matches.Add(compound);
        }

        return matches.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)(text.Split(' ').Length * 1.33);
}
