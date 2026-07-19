namespace SirThaddeus.McpServer.Tools;

/// <summary>
/// Resolves an imprecise relative file reference only when it identifies one
/// existing file inside the configured roots. This is deliberately narrower
/// than general file search: no fuzzy matching, traversal, or best guess.
/// </summary>
internal static class AllowedRootFileResolver
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    public static bool TryResolveUniqueSuffix(
        string requestedPath,
        IReadOnlyList<string> allowedRoots,
        out string? resolvedPath)
    {
        resolvedPath = null;

        if (!TryNormalizeSafeSuffix(requestedPath, out var suffix) || allowedRoots.Count == 0)
            return false;

        var normalizedSuffix = suffix!;
        var fileName = Path.GetFileName(normalizedSuffix);
        if (fileName.IndexOfAny(['*', '?']) >= 0)
            return false;

        string? uniqueMatch = null;
        try
        {
            foreach (var root in allowedRoots)
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (var file in Directory.EnumerateFiles(root, fileName, EnumerationOptions))
                {
                    var relativePath = Path.GetRelativePath(root, file);
                    if (!IsSuffixMatch(relativePath, normalizedSuffix))
                        continue;

                    if (uniqueMatch is not null)
                        return false;

                    uniqueMatch = Path.GetFullPath(file);
                }
            }
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            ArgumentException or
            NotSupportedException)
        {
            // Enumeration is optional assistance. Any uncertainty fails closed
            // and leaves the ordinary not-found/access-denied behavior intact.
            return false;
        }

        resolvedPath = uniqueMatch;
        return uniqueMatch is not null;
    }

    private static bool TryNormalizeSafeSuffix(string requestedPath, out string? suffix)
    {
        suffix = null;
        if (string.IsNullOrWhiteSpace(requestedPath) || Path.IsPathRooted(requestedPath))
            return false;

        var normalized = requestedPath.Trim().Replace('\\', '/');
        // A leading current-directory basename expresses exact relative intent.
        // A directory-qualified suffix still carries enough structure for the
        // conservative unique-match rule without turning a basename into search.
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
            if (!normalized.Contains('/', StringComparison.Ordinal))
                return false;
        }

        if (normalized.Length == 0)
            return false;

        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".."))
        {
            return false;
        }

        try
        {
            suffix = Path.Combine(segments);
            return !Path.IsPathRooted(suffix);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSuffixMatch(string relativePath, string suffix)
    {
        if (string.Equals(relativePath, suffix, StringComparison.OrdinalIgnoreCase))
            return true;

        return relativePath.EndsWith(
            Path.DirectorySeparatorChar + suffix,
            StringComparison.OrdinalIgnoreCase);
    }
}
