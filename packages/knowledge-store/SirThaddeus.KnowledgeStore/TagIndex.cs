namespace SirThaddeus.KnowledgeStore;

/// <summary>
/// In-memory index of all tags, mentions, and summaries.
/// Built from frontmatter-only scans. Never loads file bodies.
/// 500 files should index in under 500ms.
/// </summary>
public sealed class TagIndex
{
    private readonly FrontmatterParser _parser;

    /// <summary>Tag → files that have this tag.</summary>
    public Dictionary<string, List<IndexEntry>> TagMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mention → files that mention this entity.</summary>
    public Dictionary<string, List<IndexEntry>> MentionMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All indexed entries for iteration/search.</summary>
    public List<IndexEntry> AllEntries { get; } = [];

    public TagIndex(FrontmatterParser parser)
    {
        _parser = parser;
    }

    /// <summary>
    /// Build the index by scanning all .md files in a root.
    /// Only reads frontmatter — stops at the closing '---'.
    /// </summary>
    public async Task BuildAsync(string rootPath)
    {
        TagMap.Clear();
        MentionMap.Clear();
        AllEntries.Clear();

        if (!Directory.Exists(rootPath))
            return;

        var files = Directory.EnumerateFiles(
                rootPath, "*.md", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith('_'));

        foreach (var filePath in files)
        {
            var frontmatter = await _parser.ReadFrontmatterOnlyAsync(filePath);
            if (frontmatter is null)
                continue;

            var relativePath = Path.GetRelativePath(rootPath, filePath);
            var entry = new IndexEntry
            {
                RelativePath = relativePath,
                Summary = frontmatter.Summary,
                Type = frontmatter.Type,
                Updated = frontmatter.Updated,
                Tags = frontmatter.Tags,
                Mentions = frontmatter.Mentions
            };

            AddEntryToMaps(entry);
        }
    }

    /// <summary>
    /// Incrementally add or update a single entry.
    /// Called after a file is created/modified + tagged.
    /// </summary>
    public void UpsertEntry(IndexEntry entry)
    {
        RemoveEntry(entry.RelativePath);
        AddEntryToMaps(entry);
    }

    /// <summary>
    /// Remove an entry by path (used before re-adding on update).
    /// </summary>
    public void RemoveEntry(string relativePath)
    {
        AllEntries.RemoveAll(e =>
            e.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

        foreach (var list in TagMap.Values)
            list.RemoveAll(e =>
                e.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

        foreach (var list in MentionMap.Values)
            list.RemoveAll(e =>
                e.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Find entries by tag name.
    /// </summary>
    public IReadOnlyList<IndexEntry> FindByTag(string tag) =>
        TagMap.TryGetValue(tag, out var entries) ? entries : [];

    /// <summary>
    /// Find entries by entity mention.
    /// </summary>
    public IReadOnlyList<IndexEntry> FindByMention(string mention) =>
        MentionMap.TryGetValue(mention, out var entries) ? entries : [];

    private void AddEntryToMaps(IndexEntry entry)
    {
        AllEntries.Add(entry);

        foreach (var tag in entry.Tags)
        {
            if (!TagMap.TryGetValue(tag, out var list))
            {
                list = [];
                TagMap[tag] = list;
            }
            list.Add(entry);
        }

        foreach (var mention in entry.Mentions)
        {
            if (!MentionMap.TryGetValue(mention, out var list))
            {
                list = [];
                MentionMap[mention] = list;
            }
            list.Add(entry);
        }
    }
}
