namespace SirThaddeus.KnowledgeStore;

/// <summary>
/// Loads and cascades _instructions.md files from root → domain → subfolder.
/// Only applies to Knowledge Roots. Reference Roots ignore instruction files.
/// </summary>
public sealed class InstructionChainLoader
{
    private const string InstructionFileName = "_instructions.md";

    /// <summary>
    /// Load the full instruction chain for a given path within a root.
    /// Concatenates instructions from root → domain → subfolder in order.
    /// </summary>
    /// <param name="root">The workspace root.</param>
    /// <param name="relativePath">Relative path to the file or folder being operated on.</param>
    /// <returns>Concatenated instruction text, or empty if none found.</returns>
    public async Task<string> LoadChainAsync(WorkspaceRoot root, string relativePath)
    {
        if (root.AccessLevel != WorkspaceAccessLevel.KnowledgeReadWrite)
            return string.Empty;

        var rootDir = Path.GetFullPath(root.AbsolutePath);
        var segments = GetPathSegments(relativePath);
        var instructions = new List<string>();

        // Root-level _instructions.md
        var rootInstructions = await TryReadAsync(
            Path.Combine(rootDir, InstructionFileName));
        if (rootInstructions is not null)
            instructions.Add(rootInstructions);

        // Walk down the path segments, loading each _instructions.md
        var currentDir = rootDir;
        foreach (var segment in segments)
        {
            currentDir = Path.Combine(currentDir, segment);
            if (!Directory.Exists(currentDir))
                break;

            var segmentInstructions = await TryReadAsync(
                Path.Combine(currentDir, InstructionFileName));
            if (segmentInstructions is not null)
                instructions.Add(segmentInstructions);
        }

        return instructions.Count > 0
            ? string.Join("\n\n---\n\n", instructions)
            : string.Empty;
    }

    /// <summary>
    /// Get the directory segments from a relative path (excluding the filename).
    /// "journal/2026-03-19.md" → ["journal"]
    /// "projects/novel/chapter-1.md" → ["projects", "novel"]
    /// </summary>
    private static string[] GetPathSegments(string relativePath)
    {
        var dir = Path.GetDirectoryName(relativePath);
        if (string.IsNullOrEmpty(dir))
            return [];

        return dir.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task<string?> TryReadAsync(string path)
    {
        if (!File.Exists(path))
            return null;

        var content = await File.ReadAllTextAsync(path);
        return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
    }
}
