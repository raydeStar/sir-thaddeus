using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using SirThaddeus.DocumentReader;

namespace SirThaddeus.McpServer.Tools;

/// <summary>
/// File system tools exposed via MCP.
/// Provides read access to local files with basic safety checks.
/// </summary>
[McpServerToolType]
public static class FileTools
{
    private const int MaxWritableBytes = 1_048_576;
    private const int VerifiedReceiptContentChars = 8_192;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private sealed record FilePreview(string Tool, string FullPath, DateTimeOffset ExpiresAtUtc);

    private static readonly ConcurrentDictionary<string, FilePreview> PreviewCache = new();
    private static readonly TimeSpan PreviewTtl = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };
    private static readonly DocumentReaderFactory DocumentReaderFactory = new();

    [McpServerTool, Description(
        "Read a local file and return clean text. Auto-detects format by " +
        "extension and routes through the right reader: PDF and DOCX get " +
        "layout-aware text extraction; XLSX and CSV come back as tab-separated " +
        "rows; RTF is stripped to plain text; Markdown and .txt pass through. " +
        "Any other text-based file (JSON, YAML, source code, logs) is read " +
        "as UTF-8. Output is a small JSON envelope with format metadata + " +
        "the text content, truncated at a character cap.")]
    public static async Task<string> FileRead(
        [Description("Absolute or relative path to the file")] string path,
        [Description("Maximum characters returned before truncation (default from settings, fallback 4000)")] int maxChars = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Error: path is required.";

        if (maxChars <= 0)
            maxChars = ParseIntEnv("ST_DOCUMENT_READER_MAX_DEFAULT_CHARS", fallback: 4000, min: 100, max: 100_000);

        try
        {
            var resolvedPath = ResolveRequestedPath(
                path,
                allowUniqueFileSuffix: true,
                out var uniqueSuffixApplied,
                out var resolutionError);
            if (resolutionError is not null)
                return resolutionError;

            if (uniqueSuffixApplied)
                Console.Error.WriteLine("[file_read] unique_suffix_resolution_applied");

            var fullPath = resolvedPath!;
            var accessError = ValidatePathAccess(fullPath);
            if (accessError is not null)
                return accessError;

            if (!File.Exists(fullPath))
                return $"Error: File not found at '{fullPath}'.";

            var info = new FileInfo(fullPath);
            if (info.Length > 10_485_760) // 10 MB safety limit — covers typical docs.
                return $"Error: File is too large ({info.Length:N0} bytes). Max is 10 MB.";

            // Optional env-backed extension allowlist. When unset, every
            // extension is allowed — the reader factory falls back to a
            // plain-UTF-8 read for unknown types, which keeps source-code
            // and config-file reads working. Set the env var to constrain.
            var allowlistRaw = Environment.GetEnvironmentVariable("ST_DOCUMENT_READER_ALLOWED_EXTENSIONS");
            if (!string.IsNullOrWhiteSpace(allowlistRaw))
            {
                var allowedExtensions = ParseAllowedExtensionsEnv(
                    "ST_DOCUMENT_READER_ALLOWED_EXTENSIONS",
                    // Broad defaults if the env is set but empty after parsing.
                    [".pdf", ".docx", ".xlsx", ".csv", ".rtf", ".md", ".txt",
                     ".json", ".yaml", ".yml", ".xml", ".html", ".htm",
                     ".log", ".ini", ".toml", ".env",
                     ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".rs", ".go", ".java", ".rb", ".sh", ".ps1"]);
                var extension = Path.GetExtension(fullPath).ToLowerInvariant();
                if (!allowedExtensions.Contains(extension))
                    return $"Error: Extension '{extension}' is not allowed by settings. Allowed: {string.Join(", ", allowedExtensions)}";
            }

            var content = await DocumentReaderFactory.ReadAsync(fullPath, cancellationToken);
            var truncated = DocumentTruncator.TruncateWithNotice(content.TextContent, maxChars);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                format = content.Format.ToString(),
                title = content.Title,
                author = content.Author,
                pageCount = content.PageCount,
                metadata = content.Metadata,
                textContent = truncated,
                totalChars = content.TextContent.Length
            }, JsonOpts);
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    [McpServerTool(
        Name = "file_read_preview",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("Builds a dry-run preview for file_read and returns a preview_id.")]
    public static string FileReadPreview(
        [Description("Absolute or relative path to the file")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BuildError("path_required");

        try
        {
            var resolvedPath = ResolveRequestedPath(
                path,
                allowUniqueFileSuffix: true,
                out var uniqueSuffixApplied,
                out var resolutionError);
            if (resolutionError is not null)
                return BuildError("path_resolution_failed", path);

            if (uniqueSuffixApplied)
                Console.Error.WriteLine("[file_read] unique_suffix_resolution_applied");

            var fullPath = resolvedPath!;
            var accessError = ValidatePathAccess(fullPath);
            if (accessError is not null)
                return BuildError("access_denied", fullPath);

            if (!File.Exists(fullPath))
                return BuildError("file_not_found", fullPath);

            var info = new FileInfo(fullPath);
            if (info.Length > 1_048_576)
                return BuildError("file_too_large", fullPath);

            var previewId = CreatePreview("file_read", fullPath);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                preview_id = previewId,
                tool = "file_read",
                path = fullPath,
                size_bytes = info.Length,
                expires_at_utc = DateTimeOffset.UtcNow.Add(PreviewTtl).ToString("O")
            }, JsonOpts);
        }
        catch (Exception ex)
        {
            return BuildError($"preview_failed: {ex.Message}");
        }
    }

    [McpServerTool(
        Name = "file_read_apply",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("Executes file_read for a prior file_read_preview preview_id.")]
    public static async Task<string> FileReadApply(
        [Description("Preview identifier returned by file_read_preview")] string previewId,
        CancellationToken cancellationToken)
    {
        if (!TryGetPreview(previewId, "file_read", out var preview))
            return BuildError("preview_not_found_or_expired");

        var result = await FileRead(preview!.FullPath, maxChars: 0, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            ok = !result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase),
            preview_id = previewId,
            tool = "file_read",
            result
        }, JsonOpts);
    }

    [McpServerTool, Description("List files and directories at the specified path.")]
    public static string FileList(
        [Description("Directory path to list")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Error: path is required.";

        try
        {
            var resolvedPath = ResolveRequestedPath(
                path,
                allowUniqueFileSuffix: false,
                out _,
                out var resolutionError);
            if (resolutionError is not null)
                return resolutionError;

            var fullPath = resolvedPath!;
            var accessError = ValidatePathAccess(fullPath);
            if (accessError is not null)
                return accessError;

            if (!Directory.Exists(fullPath))
                return $"Error: Directory not found at '{fullPath}'.";

            var entries = Directory.GetFileSystemEntries(fullPath)
                .Select(e =>
                {
                    var isDir = Directory.Exists(e);
                    var name = Path.GetFileName(e);
                    return isDir ? $"[DIR]  {name}" : $"[FILE] {name}";
                })
                .Take(100); // Cap at 100 entries

            return string.Join("\n", entries);
        }
        catch (Exception ex)
        {
            return $"Error listing directory: {ex.Message}";
        }
    }

    [McpServerTool(
        Name = "file_list_preview",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("Builds a dry-run preview for file_list and returns a preview_id.")]
    public static string FileListPreview(
        [Description("Directory path to list")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BuildError("path_required");

        try
        {
            var resolvedPath = ResolveRequestedPath(
                path,
                allowUniqueFileSuffix: false,
                out _,
                out var resolutionError);
            if (resolutionError is not null)
                return BuildError("path_resolution_failed", path);

            var fullPath = resolvedPath!;
            var accessError = ValidatePathAccess(fullPath);
            if (accessError is not null)
                return BuildError("access_denied", fullPath);

            if (!Directory.Exists(fullPath))
                return BuildError("directory_not_found", fullPath);

            var previewId = CreatePreview("file_list", fullPath);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                preview_id = previewId,
                tool = "file_list",
                path = fullPath,
                expires_at_utc = DateTimeOffset.UtcNow.Add(PreviewTtl).ToString("O")
            }, JsonOpts);
        }
        catch (Exception ex)
        {
            return BuildError($"preview_failed: {ex.Message}");
        }
    }

    [McpServerTool(
        Name = "file_list_apply",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("Executes file_list for a prior file_list_preview preview_id.")]
    public static string FileListApply(
        [Description("Preview identifier returned by file_list_preview")] string previewId)
    {
        if (!TryGetPreview(previewId, "file_list", out var preview))
            return BuildError("preview_not_found_or_expired");

        var result = FileList(preview!.FullPath);
        return JsonSerializer.Serialize(new
        {
            ok = !result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase),
            preview_id = previewId,
            tool = "file_list",
            result
        }, JsonOpts);
    }

    [McpServerTool(
        Name = "file_write",
        ReadOnly = false,
        Idempotent = true,
        Destructive = true,
        OpenWorld = false),
     Description(
         "Write exact UTF-8 content to a file inside a configured allowed folder. " +
         "Creates parent directories, caps the file at 1 MiB, and writes atomically. " +
         "A successful result independently rereads the file and returns verified=true, " +
         "its byte count, SHA-256, and bounded exact post-write content; no separate readback is needed.")]
    public static string FileWrite(
        [Description("Absolute path, or a path relative to the single configured allowed folder")] string path,
        [Description("Exact UTF-8 content to write; include a final newline when required")] string content)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BuildError("path_required");

        if (content is null)
            return BuildError("content_required");

        try
        {
            var fullPath = ResolveWritablePath(path, mustExist: false, out var resolutionError);
            if (resolutionError is not null)
                return resolutionError;

            var validationError = ValidateWritableTarget(fullPath!);
            if (validationError is not null)
                return validationError;

            var bytes = StrictUtf8.GetBytes(content);
            if (bytes.Length > MaxWritableBytes)
                return BuildError("file_too_large", fullPath);

            return WriteAtomicallyAndBuildReceipt(fullPath!, bytes, replacements: null);
        }
        catch (EncoderFallbackException)
        {
            return BuildError("content_is_not_valid_utf8");
        }
        catch (Exception ex)
        {
            return BuildError($"write_failed: {ex.Message}");
        }
    }

    [McpServerTool(
        Name = "file_replace",
        ReadOnly = false,
        Idempotent = false,
        Destructive = true,
        OpenWorld = false),
     Description(
         "Replace one exact text span that occurs exactly once in an existing UTF-8 file " +
         "inside a configured allowed folder. Ambiguous or absent spans fail without writing. " +
         "A successful result independently rereads the file and returns verified=true, its " +
         "byte count, SHA-256, and bounded exact post-write content; no separate readback is needed.")]
    public static string FileReplace(
        [Description("Absolute path, or an existing file path resolvable inside an allowed folder")] string path,
        [Description("Non-empty exact text that must occur exactly once")] string oldText,
        [Description("Exact replacement text, which may be empty")] string newText)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BuildError("path_required");

        if (string.IsNullOrEmpty(oldText))
            return BuildError("old_text_required");

        if (newText is null)
            return BuildError("new_text_required");

        try
        {
            var fullPath = ResolveWritablePath(path, mustExist: true, out var resolutionError);
            if (resolutionError is not null)
                return resolutionError;

            var validationError = ValidateWritableTarget(fullPath!);
            if (validationError is not null)
                return validationError;

            if (!File.Exists(fullPath))
                return BuildError("file_not_found", fullPath);

            var info = new FileInfo(fullPath);
            if (info.Length > MaxWritableBytes)
                return BuildError("file_too_large", fullPath);

            var existing = File.ReadAllText(fullPath, StrictUtf8);
            var occurrences = CountOccurrences(existing, oldText);
            if (occurrences != 1)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "old_text_must_occur_exactly_once",
                    path = fullPath,
                    occurrences
                }, JsonOpts);
            }

            var updated = existing.Replace(oldText, newText, StringComparison.Ordinal);
            var bytes = StrictUtf8.GetBytes(updated);
            if (bytes.Length > MaxWritableBytes)
                return BuildError("file_too_large", fullPath);

            return WriteAtomicallyAndBuildReceipt(fullPath, bytes, replacements: 1);
        }
        catch (DecoderFallbackException)
        {
            return BuildError("existing_file_is_not_valid_utf8");
        }
        catch (EncoderFallbackException)
        {
            return BuildError("replacement_is_not_valid_utf8");
        }
        catch (Exception ex)
        {
            return BuildError($"replace_failed: {ex.Message}");
        }
    }

    private static string? ResolveWritablePath(
        string path,
        bool mustExist,
        out string? error)
    {
        error = null;
        var trimmed = path.Trim().Trim('"', '\'');
        var allowedRoots = ParseAllowedRootsEnv("ST_DOCUMENT_READER_ALLOWED_ROOTS");

        if (allowedRoots.Count == 0)
        {
            error = BuildError("no_allowed_folders_configured");
            return null;
        }

        try
        {
            if (Path.IsPathRooted(trimmed))
                return NormalizePath(trimmed);

            if (mustExist &&
                AllowedRootFileResolver.TryResolveUniqueSuffix(trimmed, allowedRoots, out var suffixMatch))
            {
                return NormalizePath(suffixMatch!);
            }

            if (allowedRoots.Count != 1)
            {
                error = BuildError("relative_path_requires_one_allowed_folder", trimmed);
                return null;
            }

            return NormalizePath(Path.Combine(allowedRoots[0], trimmed));
        }
        catch (Exception ex)
        {
            error = BuildError($"invalid_path: {ex.Message}", path);
            return null;
        }
    }

    private static string? ValidateWritableTarget(string fullPath)
    {
        if (ParseBooleanEnv("ST_DOCUMENT_READER_DISABLE_FILE_ACCESS"))
            return BuildError("file_access_disabled", fullPath);

        var allowedRoots = ParseAllowedRootsEnv("ST_DOCUMENT_READER_ALLOWED_ROOTS");
        var allowedRoot = allowedRoots.FirstOrDefault(root => IsPathUnderAnyRoot(fullPath, [root]));
        if (allowedRoot is null)
            return BuildError("access_denied", fullPath);

        if (string.Equals(NormalizePath(fullPath), NormalizePath(allowedRoot), StringComparison.OrdinalIgnoreCase))
            return BuildError("target_must_be_a_file", fullPath);

        if (Directory.Exists(fullPath))
            return BuildError("target_is_directory", fullPath);

        var extensionError = ValidateWritableExtension(fullPath);
        if (extensionError is not null)
            return extensionError;

        var rootInfo = new DirectoryInfo(allowedRoot);
        if (rootInfo.Exists && rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return BuildError("reparse_point_not_allowed", allowedRoot);

        var relative = Path.GetRelativePath(allowedRoot, fullPath);
        var current = allowedRoot;
        foreach (var part in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;

            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return BuildError("reparse_point_not_allowed", current);
        }

        return null;
    }

    private static string? ValidateWritableExtension(string fullPath)
    {
        var allowedExtensions = ParseAllowedExtensionsEnv(
            "ST_DOCUMENT_READER_ALLOWED_EXTENSIONS",
            [".json", ".yaml", ".yml", ".toml", ".ini", ".env", ".md", ".txt"]);
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        return allowedExtensions.Contains(extension)
            ? null
            : BuildError("extension_not_allowed", fullPath);
    }

    private static string WriteAtomicallyAndBuildReceipt(
        string fullPath,
        byte[] bytes,
        int? replacements)
    {
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
            return BuildError("parent_directory_required", fullPath);

        Directory.CreateDirectory(parent);
        var validationError = ValidateWritableTarget(fullPath);
        if (validationError is not null)
            return validationError;

        var existed = File.Exists(fullPath);
        var originalBytes = existed ? File.ReadAllBytes(fullPath) : null;
        var tempPath = Path.Combine(parent, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var committed = false;
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, fullPath, overwrite: true);
            committed = true;

            var observed = File.ReadAllBytes(fullPath);
            if (!observed.AsSpan().SequenceEqual(bytes))
            {
                RestoreOriginalFile(fullPath, originalBytes);
                committed = false;
                return BuildError("post_write_verification_failed", fullPath);
            }

            var content = StrictUtf8.GetString(observed);
            var receipt = JsonSerializer.Serialize(new
            {
                ok = true,
                verified = true,
                path = fullPath,
                bytes = observed.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(observed)).ToLowerInvariant(),
                post_content = content[..Math.Min(content.Length, VerifiedReceiptContentChars)],
                post_content_truncated = content.Length > VerifiedReceiptContentChars,
                replacements
            }, JsonOpts);
            committed = false;
            return receipt;
        }
        catch
        {
            if (committed)
                RestoreOriginalFile(fullPath, originalBytes);

            throw;
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void RestoreOriginalFile(string fullPath, byte[]? originalBytes)
    {
        if (originalBytes is null)
        {
            File.Delete(fullPath);
            return;
        }

        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Cannot restore a file without a parent directory.");
        var restorePath = Path.Combine(parent, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.restore");
        try
        {
            File.WriteAllBytes(restorePath, originalBytes);
            File.Move(restorePath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(restorePath))
                File.Delete(restorePath);
        }
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var offset = 0;
        while (offset <= content.Length - value.Length)
        {
            var index = content.IndexOf(value, offset, StringComparison.Ordinal);
            if (index < 0)
                break;

            count++;
            offset = index + value.Length;
        }

        return count;
    }

    private static string CreatePreview(string tool, string fullPath)
    {
        PruneExpiredPreviews();
        var previewId = $"preview-{Guid.NewGuid():N}";
        PreviewCache[previewId] = new FilePreview(
            tool,
            fullPath,
            DateTimeOffset.UtcNow.Add(PreviewTtl));
        return previewId;
    }

    private static bool TryGetPreview(string previewId, string expectedTool, out FilePreview? preview)
    {
        preview = null;
        if (string.IsNullOrWhiteSpace(previewId))
            return false;

        PruneExpiredPreviews();
        if (!PreviewCache.TryGetValue(previewId.Trim(), out var existing))
            return false;

        if (!string.Equals(existing.Tool, expectedTool, StringComparison.OrdinalIgnoreCase))
            return false;

        if (existing.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            PreviewCache.TryRemove(previewId.Trim(), out _);
            return false;
        }

        preview = existing;
        return true;
    }

    private static void PruneExpiredPreviews()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in PreviewCache)
        {
            if (pair.Value.ExpiresAtUtc < now)
                PreviewCache.TryRemove(pair.Key, out _);
        }
    }

    private static string BuildError(string code, string? path = null)
    {
        return JsonSerializer.Serialize(new
        {
            ok = false,
            error = code,
            path = path ?? ""
        }, JsonOpts);
    }

    private static string? ResolveRequestedPath(
        string path,
        bool allowUniqueFileSuffix,
        out bool uniqueSuffixApplied,
        out string? error)
    {
        uniqueSuffixApplied = false;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Error: path is required.";
            return null;
        }

        var trimmed = path.Trim().Trim('"', '\'');
        var allowedRoots = ParseAllowedRootsEnv("ST_DOCUMENT_READER_ALLOWED_ROOTS");

        if (TryResolveAllowedRootAlias(trimmed, allowedRoots, out var aliasPath, out error))
            return aliasPath;

        try
        {
            if (!Path.IsPathRooted(trimmed) && allowedRoots.Count == 1)
            {
                var candidateWithinRoot = Path.Combine(allowedRoots[0], trimmed);
                if (File.Exists(candidateWithinRoot) || Directory.Exists(candidateWithinRoot))
                    return NormalizePath(candidateWithinRoot);
            }

            if (allowUniqueFileSuffix &&
                AllowedRootFileResolver.TryResolveUniqueSuffix(trimmed, allowedRoots, out var suffixMatch))
            {
                uniqueSuffixApplied = true;
                return NormalizePath(suffixMatch!);
            }

            return NormalizePath(trimmed);
        }
        catch (Exception ex)
        {
            error = $"Error: Invalid path '{path}'. {ex.Message}";
            return null;
        }
    }

    private static bool TryResolveAllowedRootAlias(
        string requestedPath,
        IReadOnlyList<string> allowedRoots,
        out string? resolvedPath,
        out string? error)
    {
        resolvedPath = null;
        error = null;

        var normalized = requestedPath.Trim().ToLowerInvariant();
        var isAlias = normalized is "my files" or "my file" or "my folder" or "my personal folder" or "personal folder";
        if (!isAlias)
            return false;

        if (allowedRoots.Count == 1)
        {
            resolvedPath = allowedRoots[0];
            return true;
        }

        error = allowedRoots.Count == 0
            ? "Error: No allowed folders are configured. Add a folder in Settings > My Files before using file tools."
            : "Error: More than one allowed folder is configured. Please specify which folder path to use.";

        return true;
    }

    private static int ParseIntEnv(string key, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (!int.TryParse(raw, out var parsed))
            return fallback;

        return Math.Clamp(parsed, min, max);
    }

    private static string? ValidatePathAccess(string fullPath)
    {
        if (ParseBooleanEnv("ST_DOCUMENT_READER_DISABLE_FILE_ACCESS"))
            return "Error: File access is disabled in settings.";

        var allowedRoots = ParseAllowedRootsEnv("ST_DOCUMENT_READER_ALLOWED_ROOTS");
        if (allowedRoots.Count == 0)
            return "Error: No allowed folders are configured. Add a folder in Settings > My Files before using file tools.";

        return IsPathUnderAnyRoot(fullPath, allowedRoots)
            ? null
            : $"Error: Access denied. '{fullPath}' is outside the configured allowed folders.";
    }

    private static bool ParseBooleanEnv(string key)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" => true,
            "true" => true,
            "yes" => true,
            "on" => true,
            _ => false
        };
    }

    private static IReadOnlyList<string> ParseAllowedRootsEnv(string key)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var normalized = NormalizePath(candidate);
                if (seen.Add(normalized))
                    roots.Add(normalized);
            }
            catch
            {
                // Ignore invalid configured roots.
            }
        }

        return roots;
    }

    private static bool IsPathUnderAnyRoot(string fullPath, IReadOnlyList<string> allowedRoots)
    {
        var normalizedPath = NormalizePath(fullPath);
        foreach (var root in allowedRoots)
        {
            var relative = Path.GetRelativePath(root, normalizedPath);
            if (string.Equals(relative, ".", StringComparison.Ordinal) ||
                (!string.Equals(relative, "..", StringComparison.Ordinal) &&
                 !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                 !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) &&
                 !Path.IsPathRooted(relative)))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            return fullPath;

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static HashSet<string> ParseAllowedExtensionsEnv(string key, IReadOnlyList<string> fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        var source = string.IsNullOrWhiteSpace(raw)
            ? fallback
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return source
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.StartsWith('.') ? value : "." + value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
