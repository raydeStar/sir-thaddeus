using System.ComponentModel;
using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SirThaddeus.McpServer.Tools;

/// <summary>
/// File system tools exposed via MCP.
/// Provides read access to local files with basic safety checks.
/// </summary>
[McpServerToolType]
public static class FileTools
{
    private sealed record FilePreview(string Tool, string FullPath, DateTimeOffset ExpiresAtUtc);

    private static readonly ConcurrentDictionary<string, FilePreview> PreviewCache = new();
    private static readonly TimeSpan PreviewTtl = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    [McpServerTool, Description("Read the contents of a file at the specified path.")]
    public static async Task<string> FileRead(
        [Description("Absolute or relative path to the file")] string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Error: path is required.";

        try
        {
            var fullPath = Path.GetFullPath(path);

            if (!File.Exists(fullPath))
                return $"Error: File not found at '{fullPath}'.";

            var info = new FileInfo(fullPath);
            if (info.Length > 1_048_576) // 1 MB safety limit
                return $"Error: File is too large ({info.Length:N0} bytes). Max is 1 MB.";

            var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
            return content;
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
            var fullPath = Path.GetFullPath(path);
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

        var result = await FileRead(preview!.FullPath, cancellationToken);
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
            var fullPath = Path.GetFullPath(path);

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
            var fullPath = Path.GetFullPath(path);
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
}
