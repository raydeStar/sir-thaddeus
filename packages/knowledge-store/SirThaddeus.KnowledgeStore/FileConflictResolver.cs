namespace SirThaddeus.KnowledgeStore;

/// <summary>
/// Checks for conflicts before any write operation.
/// Prevents the LLM from creating duplicates or near-duplicates.
/// </summary>
public sealed class FileConflictResolver
{
    /// <summary>
    /// Determine the correct write action for a target path.
    /// </summary>
    public WriteAction ResolveConflict(string targetPath, string newContent)
    {
        if (!File.Exists(targetPath))
            return WriteAction.Create;

        var existingContent = File.ReadAllText(targetPath);

        // Exact duplicate content — skip
        if (existingContent.Contains(newContent.Trim(), StringComparison.Ordinal))
            return WriteAction.SkipDuplicate;

        // File exists, content is new — append
        return WriteAction.Append;
    }

    /// <summary>
    /// Check for near-duplicate filenames in the same folder.
    /// If 60%+ of the words in the name overlap with an existing
    /// file, flag it as a potential duplicate.
    /// </summary>
    public string? FindSimilarFile(string folder, string proposedName)
    {
        if (!Directory.Exists(folder))
            return null;

        var existing = Directory.GetFiles(folder, "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && !n.StartsWith('_'))
            .ToList();

        var proposed = Path.GetFileNameWithoutExtension(proposedName);
        if (string.IsNullOrEmpty(proposed))
            return null;

        var proposedWords = proposed.Split('-').ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in existing)
        {
            if (name is null) continue;
            var existingWords = name.Split('-').ToHashSet(StringComparer.OrdinalIgnoreCase);
            var overlap = proposedWords.Intersect(existingWords).Count();
            var total = Math.Max(proposedWords.Count, existingWords.Count);

            if (total > 0 && (double)overlap / total >= 0.6)
                return name + ".md";
        }

        return null;
    }
}
