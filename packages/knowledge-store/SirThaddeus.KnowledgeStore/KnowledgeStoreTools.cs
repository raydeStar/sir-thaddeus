using SirThaddeus.AuditLog;

namespace SirThaddeus.KnowledgeStore;

/// <summary>
/// Implementation of knowledge store file operations.
/// Every call passes through the KnowledgeStoreGuard before execution.
/// </summary>
public sealed class KnowledgeStoreTools : IKnowledgeStoreTools
{
    private readonly IReadOnlyList<WorkspaceRoot> _roots;
    private readonly KnowledgeStoreGuard _guard;
    private readonly FileConflictResolver _conflictResolver;
    private readonly TaggingQueue _taggingQueue;
    private readonly IAuditLogger _audit;

    public KnowledgeStoreTools(
        IReadOnlyList<WorkspaceRoot> roots,
        KnowledgeStoreGuard guard,
        FileConflictResolver conflictResolver,
        TaggingQueue taggingQueue,
        IAuditLogger audit)
    {
        _roots = roots;
        _guard = guard;
        _conflictResolver = conflictResolver;
        _taggingQueue = taggingQueue;
        _audit = audit;
    }

    public async Task<KnowledgeToolResult> AppendToFileAsync(
        string rootId, string relativePath, string content)
    {
        var root = FindRoot(rootId);
        if (root is null)
            return Fail("append", rootId, relativePath, "Unknown root.");

        var validation = _guard.Validate(root, "append", relativePath, content);
        if (!validation.IsAllowed)
            return Fail("append", rootId, relativePath, validation.Reason!);

        var fullPath = ResolvePath(root, relativePath);

        if (!File.Exists(fullPath))
            return Fail("append", rootId, relativePath, "File does not exist. Use CreateFileAsync to create it first.");

        var action = _conflictResolver.ResolveConflict(fullPath, content);
        if (action == WriteAction.SkipDuplicate)
        {
            LogAudit("KNOWLEDGE_APPEND_SKIP", root, relativePath, "ok", "Duplicate content skipped.");
            return KnowledgeToolResult.Ok("Content already exists in file. Skipped.", filePath: relativePath);
        }

        await File.AppendAllTextAsync(fullPath, "\n" + content);
        _taggingQueue.Enqueue(relativePath);

        LogAudit("KNOWLEDGE_APPEND", root, relativePath, "ok");
        return KnowledgeToolResult.Ok($"Appended to {relativePath}.", filePath: relativePath);
    }

    public async Task<KnowledgeToolResult> UpdateSectionAsync(
        string rootId, string relativePath,
        string oldContent, string newContent)
    {
        var root = FindRoot(rootId);
        if (root is null)
            return Fail("update", rootId, relativePath, "Unknown root.");

        var validation = _guard.Validate(root, "update", relativePath, newContent);
        if (!validation.IsAllowed)
            return Fail("update", rootId, relativePath, validation.Reason!);

        var fullPath = ResolvePath(root, relativePath);

        if (!File.Exists(fullPath))
            return Fail("update", rootId, relativePath, "File does not exist.");

        var existing = await File.ReadAllTextAsync(fullPath);
        if (!existing.Contains(oldContent, StringComparison.Ordinal))
            return Fail("update", rootId, relativePath, "Could not find the section to replace.");

        var updated = existing.Replace(oldContent, newContent, StringComparison.Ordinal);
        await File.WriteAllTextAsync(fullPath, updated);
        _taggingQueue.Enqueue(relativePath);

        LogAudit("KNOWLEDGE_UPDATE", root, relativePath, "ok");
        return KnowledgeToolResult.Ok($"Updated section in {relativePath}.", filePath: relativePath);
    }

    public async Task<KnowledgeToolResult> CreateFileAsync(
        string rootId, string relativePath, string content)
    {
        var root = FindRoot(rootId);
        if (root is null)
            return Fail("create", rootId, relativePath, "Unknown root.");

        var validation = _guard.Validate(root, "create", relativePath, content);
        if (!validation.IsAllowed)
            return Fail("create", rootId, relativePath, validation.Reason!);

        var fullPath = ResolvePath(root, relativePath);

        // Check for near-duplicates
        var folder = Path.GetDirectoryName(fullPath);
        if (folder is not null)
        {
            var similar = _conflictResolver.FindSimilarFile(
                folder, Path.GetFileName(fullPath));
            if (similar is not null)
            {
                return KnowledgeToolResult.Fail(
                    $"A similar file already exists: {similar}. " +
                    "Append to it instead, or choose a different name.");
            }
        }

        var action = _conflictResolver.ResolveConflict(fullPath, content);
        if (action == WriteAction.SkipDuplicate)
        {
            LogAudit("KNOWLEDGE_CREATE_SKIP", root, relativePath, "ok", "Duplicate content.");
            return KnowledgeToolResult.Ok("File with this content already exists. Skipped.", filePath: relativePath);
        }

        if (action == WriteAction.Append)
        {
            // File exists with different content; delegate to append.
            return await AppendToFileAsync(rootId, relativePath, content);
        }

        // Ensure directory exists
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(fullPath, content);
        _taggingQueue.Enqueue(relativePath);

        LogAudit("KNOWLEDGE_CREATE", root, relativePath, "ok");
        return KnowledgeToolResult.Ok($"Created {relativePath}.", filePath: relativePath);
    }

    public async Task<KnowledgeToolResult> ReadFileAsync(
        string rootId, string relativePath)
    {
        var root = FindRoot(rootId);
        if (root is null)
            return Fail("read", rootId, relativePath, "Unknown root.");

        var validation = _guard.Validate(root, "read", relativePath);
        if (!validation.IsAllowed)
            return Fail("read", rootId, relativePath, validation.Reason!);

        var fullPath = ResolvePath(root, relativePath);

        if (!File.Exists(fullPath))
            return Fail("read", rootId, relativePath, "File does not exist.");

        var content = await File.ReadAllTextAsync(fullPath);
        LogAudit("KNOWLEDGE_READ", root, relativePath, "ok");
        return KnowledgeToolResult.Ok("Read successful.", content: content, filePath: relativePath);
    }

    public Task<KnowledgeToolResult> ListFilesAsync(
        string rootId, string relativeFolderPath, string? pattern = null)
    {
        var root = FindRoot(rootId);
        if (root is null)
            return Task.FromResult(Fail("list", rootId, relativeFolderPath, "Unknown root."));

        var validation = _guard.Validate(root, "list", relativeFolderPath);
        if (!validation.IsAllowed)
            return Task.FromResult(Fail("list", rootId, relativeFolderPath, validation.Reason!));

        var fullPath = ResolvePath(root, relativeFolderPath);

        if (!Directory.Exists(fullPath))
            return Task.FromResult(
                KnowledgeToolResult.Fail($"Directory does not exist: {relativeFolderPath}"));

        var searchPattern = pattern ?? "*.md";
        var files = Directory.GetFiles(fullPath, searchPattern)
            .Select(f => Path.GetRelativePath(root.AbsolutePath, f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var subdirs = Directory.GetDirectories(fullPath)
            .Select(d => Path.GetRelativePath(root.AbsolutePath, d) + "/")
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var listing = string.Join("\n",
            subdirs.Select(d => $"  [dir]  {d}")
            .Concat(files.Select(f => $"  [file] {f}")));

        LogAudit("KNOWLEDGE_LIST", root, relativeFolderPath, "ok");
        return Task.FromResult(
            KnowledgeToolResult.Ok($"Found {files.Count} files, {subdirs.Count} folders.", content: listing));
    }

    public async Task<InstructionEditProposal> ProposeInstructionEditAsync(
        string rootId, string relativePath, string proposedContent)
    {
        var root = FindRoot(rootId);
        var fullPath = root is not null
            ? ResolvePath(root, relativePath)
            : relativePath;

        var original = File.Exists(fullPath)
            ? await File.ReadAllTextAsync(fullPath)
            : string.Empty;

        LogAudit("KNOWLEDGE_INSTRUCTION_PROPOSE", root, relativePath, "ok");

        return new InstructionEditProposal
        {
            FilePath = relativePath,
            OriginalContent = original,
            ProposedContent = proposedContent,
            ChangeDescription = "Proposed by LLM",
            Status = EditStatus.AwaitingConfirmation
        };
    }

    public async Task<KnowledgeToolResult> WriteInstructionFileAsync(
        string rootId, string relativePath, string content)
    {
        var root = FindRoot(rootId);
        if (root is null)
            return Fail("write_instruction_confirmed", rootId, relativePath, "Unknown root.");

        var validation = _guard.Validate(root, "write_instruction_confirmed", relativePath, content);
        if (!validation.IsAllowed)
            return Fail("write_instruction_confirmed", rootId, relativePath, validation.Reason!);

        var fullPath = ResolvePath(root, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(fullPath, content);
        LogAudit("KNOWLEDGE_INSTRUCTION_WRITE", root, relativePath, "ok");
        return KnowledgeToolResult.Ok($"Instruction file written: {relativePath}.", filePath: relativePath);
    }

    // ────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────

    private WorkspaceRoot? FindRoot(string rootId) =>
        _roots.FirstOrDefault(r =>
            r.Id.Equals(rootId, StringComparison.OrdinalIgnoreCase));

    private static string ResolvePath(WorkspaceRoot root, string relativePath) =>
        Path.GetFullPath(Path.Combine(root.AbsolutePath, relativePath));

    private KnowledgeToolResult Fail(
        string operation, string rootId, string relativePath, string reason)
    {
        _audit.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = $"KNOWLEDGE_{operation.ToUpperInvariant()}_DENIED",
            Target = relativePath,
            Result = "denied",
            Details = new Dictionary<string, object>
            {
                ["rootId"] = rootId,
                ["reason"] = reason
            }
        });

        return KnowledgeToolResult.Fail(reason);
    }

    private void LogAudit(
        string action, WorkspaceRoot? root, string relativePath,
        string result, string? detail = null)
    {
        var details = new Dictionary<string, object>
        {
            ["rootId"] = root?.Id ?? "unknown",
            ["path"] = relativePath
        };
        if (detail is not null)
            details["detail"] = detail;

        _audit.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = action,
            Target = relativePath,
            Result = result,
            Details = details
        });
    }
}
