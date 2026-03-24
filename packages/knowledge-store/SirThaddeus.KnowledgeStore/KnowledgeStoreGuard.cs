namespace SirThaddeus.KnowledgeStore;

/// <summary>
/// Validates all file operations before execution.
/// Trust but verify — especially when the LLM is holding the pen.
/// </summary>
public sealed class KnowledgeStoreGuard
{
    private readonly StorePolicy _policy;

    public KnowledgeStoreGuard(StorePolicy policy)
    {
        _policy = policy;
    }

    /// <summary>
    /// Validate a file operation against the root's access level and store policy.
    /// </summary>
    public ValidationResult Validate(
        WorkspaceRoot root,
        string operation,
        string relativePath,
        string? content = null)
    {
        // 1. Resolve and jail-check
        var fullPath = Path.GetFullPath(
            Path.Combine(root.AbsolutePath, relativePath));

        var normalizedRoot = Path.GetFullPath(root.AbsolutePath);
        if (!IsPathUnderRoot(normalizedRoot, fullPath))
            return ValidationResult.Denied("Path escapes the workspace root. Blocked.");

        // 2. Symlink escape check
        if (IsSymlinkEscape(fullPath, normalizedRoot))
            return ValidationResult.Denied("Symlink resolves outside the workspace root. Blocked.");

        // 3. Write operations against read-only roots
        if (root.AccessLevel == WorkspaceAccessLevel.ReferenceReadOnly
            && operation is "create" or "append" or "update"
                or "replace" or "write_instruction" or "write_instruction_confirmed")
        {
            return ValidationResult.Denied(
                "This is a read-only Reference Root. No writes allowed.");
        }

        // 4. Instruction file protection
        var isInstructionFile = Path.GetFileName(relativePath)
            .Equals("_instructions.md", StringComparison.OrdinalIgnoreCase);
        if (isInstructionFile && operation is not ("read" or "write_instruction_confirmed"))
        {
            return ValidationResult.Denied(
                "Instruction edits require the confirmation flow. " +
                "Use ProposeInstructionEditAsync.");
        }

        if (operation == "write_instruction_confirmed" && !isInstructionFile)
        {
            return ValidationResult.Denied(
                "Confirmed instruction writes may only target _instructions.md files.");
        }

        // 5. No delete operations
        if (operation == "delete")
            return ValidationResult.Denied("File deletion is manual only — safety measure.");

        // 6. Extension check
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        if (operation is "create" or "append" or "update" or "replace")
        {
            if (!_policy.AllowedWriteExtensions.Contains(ext))
                return ValidationResult.Denied(
                    $"Only {string.Join(", ", _policy.AllowedWriteExtensions)} " +
                    $"files can be written. Got: {ext}");
        }
        else if (operation == "read")
        {
            if (!_policy.AllowedReadExtensions.Contains(ext))
                return ValidationResult.Denied($"Cannot read {ext} files.");
        }

        // 7. Folder depth check
        var depth = relativePath.Count(c => c is '/' or '\\');
        if (depth > _policy.MaxFolderDepth)
        {
            return ValidationResult.Denied(
                $"Max folder depth is {_policy.MaxFolderDepth}. Keep it flat.");
        }

        // 8. File size check for writes
        if (content is not null && content.Length > _policy.MaxFileSizeBytes)
        {
            return ValidationResult.Denied(
                $"Content exceeds {_policy.MaxFileSizeBytes / 1024}KB limit. " +
                "Split into multiple files.");
        }

        // 9. File count check for creates
        if (operation == "create")
        {
            var folder = Path.GetDirectoryName(fullPath) ?? normalizedRoot;
            if (Directory.Exists(folder))
            {
                var count = Directory.GetFiles(folder, "*.md")
                    .Count(f => !Path.GetFileName(f).StartsWith('_'));
                if (count >= _policy.MaxFilesPerFolder)
                {
                    return ValidationResult.Denied(
                        $"Folder has {count} files (max {_policy.MaxFilesPerFolder}). " +
                        "Archive before creating new ones.");
                }
            }
        }

        // 10. Total root size check for writes
        if (operation is "create" or "append" or "update" && content is not null)
        {
            var currentSize = GetDirectorySizeBytes(normalizedRoot);
            if (currentSize + content.Length > _policy.MaxRootSizeBytes)
            {
                return ValidationResult.Denied(
                    $"Root would exceed {_policy.MaxRootSizeBytes / (1024 * 1024)}MB limit.");
            }
        }

        return ValidationResult.Allowed;
    }

    private static bool IsSymlinkEscape(string resolvedPath, string rootPath)
    {
        // Walk up the path checking for symlinks that escape the root.
        try
        {
            var info = new FileInfo(resolvedPath);
            if (info.LinkTarget is not null)
            {
                var linkTarget = Path.GetFullPath(
                    info.LinkTarget,
                    Path.GetDirectoryName(resolvedPath) ?? resolvedPath);
                return !linkTarget.StartsWith(
                    rootPath, StringComparison.OrdinalIgnoreCase);
            }

            // Also check directories in the path
            var dir = Path.GetDirectoryName(resolvedPath);
            while (dir is not null && dir.Length >= rootPath.Length)
            {
                var dirInfo = new DirectoryInfo(dir);
                if (dirInfo.LinkTarget is not null)
                {
                    var linkTarget = Path.GetFullPath(dirInfo.LinkTarget);
                    if (!linkTarget.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch
        {
            // If we can't check, assume it's safe (path may not exist yet for creates).
        }

        return false;
    }

    private static bool IsPathUnderRoot(string rootPath, string candidatePath)
    {
        var relative = Path.GetRelativePath(rootPath, candidatePath);
        return !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static long GetDirectorySizeBytes(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        return new DirectoryInfo(path)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length);
    }
}
