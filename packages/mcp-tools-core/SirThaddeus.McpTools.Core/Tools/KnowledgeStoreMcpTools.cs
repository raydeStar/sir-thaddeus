using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using CoreKnowledgeStoreTools = SirThaddeus.KnowledgeStore.KnowledgeStoreTools;

namespace SirThaddeus.McpServer.Tools;

[McpServerToolType]
public static class KnowledgeStoreMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    [McpServerTool(
        Name = "knowledge_store_list_roots",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("List configured knowledge-store roots with ids, display names, and access levels.")]
    public static string KnowledgeStoreListRoots()
    {
        if (!TryCreateContext(out var context, out var errorJson))
            return errorJson!;

        return JsonSerializer.Serialize(new
        {
            ok = true,
            enabled = context!.Settings.KnowledgeStore.Enabled,
            roots = context.Roots.Select(root => new
            {
                id = root.Id,
                display_name = root.DisplayName,
                absolute_path = root.AbsolutePath,
                access_level = root.AccessLevel.ToString(),
                allow_indexing = root.AllowIndexing,
                confirm_writes = root.ConfirmWrites
            })
        }, JsonOptions);
    }

    [McpServerTool(
        Name = "knowledge_store_create_file",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Create a markdown file inside a configured knowledge-store root.")]
    public static Task<string> KnowledgeStoreCreateFile(
        [Description("Configured knowledge-store root id")] string rootId,
        [Description("Relative markdown path under the root")] string relativePath,
        [Description("Markdown content to write")] string content,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync((store, _) => store.CreateFileAsync(rootId, relativePath, content), cancellationToken);
    }

    [McpServerTool(
        Name = "knowledge_store_append_to_file",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Append markdown content to an existing file inside a configured knowledge-store root.")]
    public static Task<string> KnowledgeStoreAppendToFile(
        [Description("Configured knowledge-store root id")] string rootId,
        [Description("Relative markdown path under the root")] string relativePath,
        [Description("Markdown content to append")] string content,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync((store, _) => store.AppendToFileAsync(rootId, relativePath, content), cancellationToken);
    }

    [McpServerTool(
        Name = "knowledge_store_read_file",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("Read a markdown file from a configured knowledge-store root.")]
    public static Task<string> KnowledgeStoreReadFile(
        [Description("Relative markdown path under the root")] string path,
        [Description("Configured knowledge-store root id. Optional when exactly one root is configured")] string? rootId = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync((store, context) =>
        {
            var resolvedRootId = ResolveRootId(rootId, context.Roots);
            return resolvedRootId is null
                ? Task.FromResult(SirThaddeus.KnowledgeStore.KnowledgeToolResult.Fail("A rootId is required when multiple knowledge-store roots are configured."))
                : store.ReadFileAsync(resolvedRootId, path);
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "knowledge_store_list_files",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("List files and subdirectories from a folder inside a configured knowledge-store root.")]
    public static Task<string> KnowledgeStoreListFiles(
        [Description("Relative folder path under the root")] string path,
        [Description("Optional glob pattern, defaults to *.md")] string? pattern = null,
        [Description("Configured knowledge-store root id. Optional when exactly one root is configured")] string? rootId = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync((store, context) =>
        {
            var resolvedRootId = ResolveRootId(rootId, context.Roots);
            return resolvedRootId is null
                ? Task.FromResult(SirThaddeus.KnowledgeStore.KnowledgeToolResult.Fail("A rootId is required when multiple knowledge-store roots are configured."))
                : store.ListFilesAsync(resolvedRootId, path, pattern);
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "knowledge_store_journal_log_entry",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Append an entry to today's journal markdown file in a configured knowledge-store root.")]
    public static Task<string> KnowledgeStoreJournalLogEntry(
        [Description("Configured knowledge-store root id")] string rootId,
        [Description("Journal entry content to log")] string entry,
        [Description("Optional natural-language time hint such as 'after lunch' or '5 PM'")] string? timeHint,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync((store, _) =>
        {
            var journal = new SirThaddeus.KnowledgeStore.JournalHandler(store, rootId, TimeProvider.System);
            return journal.LogEntryAsync(entry, timeHint);
        }, cancellationToken);
    }

    private static async Task<string> ExecuteAsync(
        Func<CoreKnowledgeStoreTools, KnowledgeStoreRuntimeContext, Task<SirThaddeus.KnowledgeStore.KnowledgeToolResult>> action,
        CancellationToken cancellationToken)
    {
        if (!TryCreateContext(out var context, out var errorJson))
            return errorJson!;

        using var audit = new JsonLineAuditLogger(ResolveAuditPath());
        var store = new CoreKnowledgeStoreTools(
            context!.Roots,
            new SirThaddeus.KnowledgeStore.KnowledgeStoreGuard(context.Policy),
            new SirThaddeus.KnowledgeStore.FileConflictResolver(),
            new SirThaddeus.KnowledgeStore.TaggingQueue(),
            audit);

        try
        {
            var result = await action(store, context);
            cancellationToken.ThrowIfCancellationRequested();
            return SerializeResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                message = ex.Message
            }, JsonOptions);
        }
    }

    private static bool TryCreateContext(out KnowledgeStoreRuntimeContext? context, out string? errorJson)
    {
        context = null;
        errorJson = null;

        AppSettings settings;
        try
        {
            settings = LoadSettings();
        }
        catch (Exception ex)
        {
            errorJson = JsonSerializer.Serialize(new
            {
                ok = false,
                message = $"Failed to load settings: {ex.Message}"
            }, JsonOptions);
            return false;
        }

        if (!settings.KnowledgeStore.Enabled)
        {
            errorJson = JsonSerializer.Serialize(new
            {
                ok = false,
                message = "Knowledge store is disabled in settings."
            }, JsonOptions);
            return false;
        }

        var roots = settings.KnowledgeStore.Roots
            .Where(root => !string.IsNullOrWhiteSpace(root.Id) && !string.IsNullOrWhiteSpace(root.AbsolutePath))
            .Select(root => new SirThaddeus.KnowledgeStore.WorkspaceRoot
            {
                Id = root.Id,
                DisplayName = root.DisplayName,
                AbsolutePath = Path.GetFullPath(root.AbsolutePath),
                AccessLevel = ParseAccessLevel(root.AccessLevel),
                AllowIndexing = root.AllowIndexing,
                ConfirmWrites = root.ConfirmWrites
            })
            .ToArray();

        if (roots.Length == 0)
        {
            errorJson = JsonSerializer.Serialize(new
            {
                ok = false,
                message = "Knowledge store is enabled but no roots are configured."
            }, JsonOptions);
            return false;
        }

        context = new KnowledgeStoreRuntimeContext(
            settings,
            roots,
            new SirThaddeus.KnowledgeStore.StorePolicy
            {
                MaxFilesPerFolder = settings.KnowledgeStore.MaxFilesPerFolder,
                MaxFolderDepth = settings.KnowledgeStore.MaxFolderDepth,
                MaxRootSizeBytes = settings.KnowledgeStore.MaxRootSizeBytes,
                MaxFileSizeBytes = settings.KnowledgeStore.MaxFileSizeBytes
            });

        return true;
    }

    private static AppSettings LoadSettings()
    {
        var settingsPath = ResolveSettingsPath();
        if (!File.Exists(settingsPath))
            throw new FileNotFoundException("Settings file not found.", settingsPath);

        var json = File.ReadAllText(settingsPath);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }

    private static string ResolveSettingsPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("ST_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath.Trim();

        return SettingsManager.GetSettingsPath();
    }

    private static string ResolveAuditPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("ST_AUDIT_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath.Trim();

        return JsonLineAuditLogger.GetDefaultPath();
    }

    private static SirThaddeus.KnowledgeStore.WorkspaceAccessLevel ParseAccessLevel(string? value)
    {
        return Enum.TryParse<SirThaddeus.KnowledgeStore.WorkspaceAccessLevel>(value, ignoreCase: true, out var parsed)
            ? parsed
            : SirThaddeus.KnowledgeStore.WorkspaceAccessLevel.KnowledgeReadWrite;
    }

    private static string? ResolveRootId(
        string? requestedRootId,
        IReadOnlyList<SirThaddeus.KnowledgeStore.WorkspaceRoot> roots)
    {
        if (!string.IsNullOrWhiteSpace(requestedRootId))
            return requestedRootId.Trim();

        return roots.Count == 1 ? roots[0].Id : null;
    }

    private static string SerializeResult(SirThaddeus.KnowledgeStore.KnowledgeToolResult result)
    {
        return JsonSerializer.Serialize(new
        {
            ok = result.Success,
            message = result.Message,
            content = result.Content,
            file_path = result.FilePath
        }, JsonOptions);
    }

    private sealed record KnowledgeStoreRuntimeContext(
        AppSettings Settings,
        IReadOnlyList<SirThaddeus.KnowledgeStore.WorkspaceRoot> Roots,
        SirThaddeus.KnowledgeStore.StorePolicy Policy);
}