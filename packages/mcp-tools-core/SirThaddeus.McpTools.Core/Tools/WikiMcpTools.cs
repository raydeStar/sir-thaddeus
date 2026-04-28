using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using SirThaddeus.Config;
using SirThaddeus.Wiki;
using SirThaddeus.Wiki.Storage;

namespace SirThaddeus.McpServer.Tools;

[McpServerToolType]
public static class WikiMcpTools
{
    private const int DefaultPageReadChars = 24_000;
    private const int MaxPageReadChars = 60_000;
    private const int DefaultSearchResults = 10;
    private const int MaxSearchResults = 50;
    private const int DefaultRevisionItems = 20;
    private const int MaxRevisionItems = 100;
    private const int MaxTreeItems = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    [McpServerTool(
        Name = "wiki_roots_list",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("List local Wiki Canvas roots with ids, names, paths, and timestamps. Use before tree, page, or search calls when the root id is unknown.")]
    public static Task<string> WikiRootsList(CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) => new
        {
            Ok = true,
            LibraryDirectory = store.LibraryDirectory,
            Roots = await store.ListRootsAsync(ct).ConfigureAwait(false)
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_root_create",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Create a local Wiki Canvas root inside the configured wiki library directory. Optional path must stay inside the library.")]
    public static Task<string> WikiRootCreate(
        [Description("Display name for the wiki root.")] string name,
        [Description("Optional absolute path under the configured wiki library directory.")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) => new
        {
            Ok = true,
            Root = await store.CreateRootAsync(name, NormalizeOptional(path), ct).ConfigureAwait(false)
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_root_rename",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Rename a local Wiki Canvas root without moving its directory.")]
    public static Task<string> WikiRootRename(
        [Description("Wiki root id from wiki_roots_list.")] string rootId,
        [Description("New root display name.")] string name,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var root = await store.RenameRootAsync(rootId, name, ct).ConfigureAwait(false);
            return root is null
                ? Fail($"Wiki root '{rootId}' not found.")
                : new { Ok = true, Root = root };
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_tree_get",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("Get folders and pages for one Wiki Canvas root. Returns page metadata, not page markdown bodies.")]
    public static Task<string> WikiTreeGet(
        [Description("Wiki root id from wiki_roots_list.")] string rootId,
        [Description("Maximum folders/pages to return, clamped to 500.")] int maxItems = 200,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var tree = await store.GetTreeAsync(rootId, ct).ConfigureAwait(false);
            if (tree is null)
                return Fail($"Wiki root '{rootId}' not found.");

            var limit = Clamp(maxItems, 1, MaxTreeItems);
            return new
            {
                Ok = true,
                Tree = new
                {
                    tree.Root,
                    FolderCount = tree.Folders.Count,
                    PageCount = tree.Pages.Count,
                    Folders = tree.Folders.Take(limit),
                    Pages = tree.Pages.Take(limit),
                    Truncated = tree.Folders.Count > limit || tree.Pages.Count > limit
                }
            };
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_folder_create",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Create a folder inside a Wiki Canvas root. Parent folder id is optional.")]
    public static Task<string> WikiFolderCreate(
        [Description("Wiki root id from wiki_roots_list.")] string rootId,
        [Description("Folder display name.")] string name,
        [Description("Optional parent folder id from wiki_tree_get.")] string? parentFolderId = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) => new
        {
            Ok = true,
            Folder = await store.CreateFolderAsync(rootId, name, NormalizeOptional(parentFolderId), ct).ConfigureAwait(false)
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_folder_rename",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Rename a Wiki Canvas folder and update descendant page paths.")]
    public static Task<string> WikiFolderRename(
        [Description("Wiki root id from wiki_roots_list.")] string rootId,
        [Description("Folder id from wiki_tree_get.")] string folderId,
        [Description("New folder display name.")] string name,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var folder = await store.RenameFolderAsync(rootId, folderId, name, ct).ConfigureAwait(false);
            return folder is null
                ? Fail($"Wiki folder '{folderId}' not found.")
                : new { Ok = true, Folder = folder };
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_folder_move",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Move a Wiki Canvas folder to another parent folder or to the root, rejecting cycles.")]
    public static Task<string> WikiFolderMove(
        [Description("Wiki root id from wiki_roots_list.")] string rootId,
        [Description("Folder id from wiki_tree_get.")] string folderId,
        [Description("Optional destination parent folder id from wiki_tree_get. Omit or blank to move to root.")] string? parentFolderId = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var folder = await store.MoveFolderAsync(rootId, folderId, NormalizeOptional(parentFolderId), ct).ConfigureAwait(false);
            return folder is null
                ? Fail($"Wiki folder '{folderId}' not found.")
                : new { Ok = true, Folder = folder };
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_folder_delete",
        ReadOnly = false,
        Idempotent = false,
        Destructive = true,
        OpenWorld = false),
     Description("Delete a Wiki Canvas folder and every descendant folder, page, page file, and revision.")]
    public static Task<string> WikiFolderDelete(
        [Description("Wiki root id from wiki_roots_list.")] string rootId,
        [Description("Folder id from wiki_tree_get.")] string folderId,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var deleted = await store.DeleteFolderAsync(rootId, folderId, ct).ConfigureAwait(false);
            return deleted
                ? new { Ok = true, Deleted = true, FolderId = folderId }
                : Fail($"Wiki folder '{folderId}' not found.");
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_page_create",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Create a Markdown page in a Wiki Canvas root or folder. Markdown is persisted as the canonical page body.")]
    public static Task<string> WikiPageCreate(
        [Description("Wiki root id from wiki_roots_list.")] string rootId,
        [Description("Page title.")] string title,
        [Description("Markdown page body.")] string markdown,
        [Description("Optional folder id from wiki_tree_get.")] string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) => new
        {
            Ok = true,
            Document = await store.CreatePageAsync(rootId, NormalizeOptional(folderId), title, markdown ?? string.Empty, ct).ConfigureAwait(false)
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_page_read",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("Read one Wiki Canvas page by id, including bounded Markdown body and current version for safe updates.")]
    public static Task<string> WikiPageRead(
        [Description("Wiki page id from wiki_tree_get or wiki_search.")] string pageId,
        [Description("Maximum Markdown body characters to return, clamped to 60000.")] int maxChars = DefaultPageReadChars,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var document = await store.GetPageAsync(pageId, ct).ConfigureAwait(false);
            if (document is null)
                return Fail($"Wiki page '{pageId}' not found.");

            var markdown = Bound(document.Markdown, maxChars, out var truncated);
            return new
            {
                Ok = true,
                Document = new
                {
                    document.Page,
                    Markdown = markdown,
                    Truncated = truncated,
                    MarkdownLength = document.Markdown.Length
                }
            };
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_page_update",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Replace a Wiki Canvas page Markdown body. Requires the current version from wiki_page_read to avoid overwriting newer edits.")]
    public static Task<string> WikiPageUpdate(
        [Description("Wiki page id from wiki_page_read.")] string pageId,
        [Description("Full replacement Markdown body.")] string markdown,
        [Description("Current page version from wiki_page_read.")] long expectedVersion,
        [Description("Optional short revision summary.")] string? summary = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var document = await store.UpdatePageAsync(
                pageId,
                markdown ?? string.Empty,
                expectedVersion,
                "agent",
                NormalizeOptional(summary),
                ct).ConfigureAwait(false);

            return document is null
                ? Fail($"Wiki page '{pageId}' not found.")
                : new { Ok = true, Document = document };
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_page_rename",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Rename a Wiki Canvas page using expected-version concurrency and create a revision.")]
    public static Task<string> WikiPageRename(
        [Description("Wiki page id from wiki_page_read.")] string pageId,
        [Description("New page title.")] string title,
        [Description("Current page version from wiki_page_read.")] long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var document = await store.RenamePageAsync(pageId, title, expectedVersion, ct).ConfigureAwait(false);
            return document is null
                ? Fail($"Wiki page '{pageId}' not found.")
                : new { Ok = true, Document = document };
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_page_move",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Move a Wiki Canvas page to another folder or to the root using expected-version concurrency.")]
    public static Task<string> WikiPageMove(
        [Description("Wiki page id from wiki_page_read.")] string pageId,
        [Description("Optional destination folder id from wiki_tree_get. Omit or blank to move to root.")] string? folderId,
        [Description("Current page version from wiki_page_read.")] long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var document = await store.MovePageAsync(pageId, NormalizeOptional(folderId), expectedVersion, ct).ConfigureAwait(false);
            return document is null
                ? Fail($"Wiki page '{pageId}' not found.")
                : new { Ok = true, Document = document };
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_page_delete",
        ReadOnly = false,
        Idempotent = false,
        Destructive = true,
        OpenWorld = false),
     Description("Delete a Wiki Canvas page, its Markdown file, and all revisions. Optional expected version prevents deleting stale content.")]
    public static Task<string> WikiPageDelete(
        [Description("Wiki page id from wiki_page_read.")] string pageId,
        [Description("Optional current page version from wiki_page_read. When provided, stale versions are rejected.")] long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var document = await store.GetPageAsync(pageId, ct).ConfigureAwait(false);
            if (document is null)
                return Fail($"Wiki page '{pageId}' not found.");

            if (expectedVersion.HasValue && expectedVersion.Value != document.Page.Version)
                return Fail($"Wiki page '{pageId}' is at version {document.Page.Version}, not {expectedVersion.Value}.");

            var deleted = await store.DeletePageAsync(pageId, ct).ConfigureAwait(false);
            return deleted
                ? new { Ok = true, Deleted = true, PageId = pageId }
                : Fail($"Wiki page '{pageId}' not found.");
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_page_patch_selection",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Replace exactly one selected text passage in a Wiki Canvas page. Requires current version and rejects missing or ambiguous selections.")]
    public static Task<string> WikiPagePatchSelection(
        [Description("Wiki page id from wiki_page_read.")] string pageId,
        [Description("Exact selected text to replace. Must appear exactly once in the Markdown body.")] string selectedText,
        [Description("Replacement text for the selected passage only.")] string replacementText,
        [Description("Current page version from wiki_page_read.")] long expectedVersion,
        [Description("Optional short revision summary.")] string? summary = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var document = await store.GetPageAsync(pageId, ct).ConfigureAwait(false);
            if (document is null)
                return Fail($"Wiki page '{pageId}' not found.");

            if (expectedVersion != document.Page.Version)
                return Fail($"Wiki page '{pageId}' is at version {document.Page.Version}, not {expectedVersion}.");

            var selection = selectedText?.Trim();
            if (string.IsNullOrWhiteSpace(selection))
                return Fail("Selected text is required.");

            var occurrenceCount = CountOccurrences(document.Markdown, selection);
            if (occurrenceCount != 1)
            {
                return Fail(occurrenceCount == 0
                    ? "Selected text no longer matches this page."
                    : "Selected text appears more than once; provide a more specific passage.");
            }

            var markdown = ReplaceFirst(document.Markdown, selection, replacementText ?? string.Empty);
            var updated = await store.UpdatePageAsync(
                pageId,
                markdown,
                expectedVersion,
                "agent",
                NormalizeOptional(summary) ?? "Selection patch",
                ct).ConfigureAwait(false);

            return updated is null
                ? Fail($"Wiki page '{pageId}' not found.")
                : new { Ok = true, Document = updated };
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_page_revisions_list",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("List revisions for a Wiki Canvas page, returning bounded Markdown bodies for inspection before restore.")]
    public static Task<string> WikiPageRevisionsList(
        [Description("Wiki page id from wiki_page_read.")] string pageId,
        [Description("Maximum revisions to return, clamped to 100.")] int maxItems = DefaultRevisionItems,
        [Description("Maximum Markdown characters per revision, clamped to 60000.")] int maxMarkdownChars = 4_000,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var document = await store.GetPageAsync(pageId, ct).ConfigureAwait(false);
            if (document is null)
                return Fail($"Wiki page '{pageId}' not found.");

            var revisions = await store.ListRevisionsAsync(pageId, ct).ConfigureAwait(false);
            var limit = Clamp(maxItems, 1, MaxRevisionItems);
            var boundedRevisions = revisions.Take(limit).Select(revision =>
            {
                var markdown = Bound(revision.Markdown, maxMarkdownChars, out var truncated);
                return new
                {
                    revision.Id,
                    revision.PageId,
                    revision.Version,
                    revision.Source,
                    revision.CreatedAt,
                    revision.Summary,
                    Markdown = markdown,
                    MarkdownLength = revision.Markdown.Length,
                    Truncated = truncated
                };
            });

            return new
            {
                Ok = true,
                Page = document.Page,
                RevisionCount = revisions.Count,
                Revisions = boundedRevisions,
                Truncated = revisions.Count > limit
            };
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_page_revision_restore",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Restore one Wiki Canvas page revision using expected-version concurrency and create a new restore revision.")]
    public static Task<string> WikiPageRevisionRestore(
        [Description("Wiki page id from wiki_page_read.")] string pageId,
        [Description("Revision id from wiki_page_revisions_list.")] string revisionId,
        [Description("Current page version from wiki_page_read.")] long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var document = await store.RestoreRevisionAsync(pageId, revisionId, expectedVersion, ct).ConfigureAwait(false);
            return document is null
                ? Fail($"Wiki page '{pageId}' or revision '{revisionId}' not found.")
                : new { Ok = true, Document = document };
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_search",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false),
     Description("Search local Wiki Canvas pages by title, excerpt, or Markdown body. Optional root id narrows the search.")]
    public static Task<string> WikiSearch(
        [Description("Search query text.")] string query,
        [Description("Optional wiki root id from wiki_roots_list.")] string? rootId = null,
        [Description("Maximum results to return, clamped to 50.")] int maxResults = DefaultSearchResults,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            if (string.IsNullOrWhiteSpace(query))
                return Fail("A non-empty query is required.");

            var limit = Clamp(maxResults, 1, MaxSearchResults);
            var results = await store.SearchAsync(NormalizeOptional(rootId), query.Trim(), ct).ConfigureAwait(false);
            return new
            {
                Ok = true,
                Query = query.Trim(),
                RootId = NormalizeOptional(rootId),
                Count = results.Count,
                Results = results.Take(limit),
                Truncated = results.Count > limit
            };
        }, cancellationToken);
    }

    private static async Task<string> ExecuteAsync(
        Func<LocalWikiStore, CancellationToken, Task<object>> action,
        CancellationToken cancellationToken)
    {
        using var store = CreateStore();
        try
        {
            var payload = await action(store, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return JsonSerializer.Serialize(payload, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                Ok = false,
                Message = ex.Message,
                Error = ex.GetType().Name
            }, JsonOptions);
        }
    }

    private static LocalWikiStore CreateStore()
        => new(ResolveLibraryDirectory(), NullLogger<LocalWikiStore>.Instance);

    private static string ResolveLibraryDirectory()
    {
        var explicitPath = Environment.GetEnvironmentVariable("ST_WIKI_LIBRARY_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(explicitPath.Trim()));

        var settingsPath = ResolveSettingsPath();
        if (!string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath))
        {
            var configured = TryReadLibraryDirectory(settingsPath);
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
            return Path.Combine(documents, "Sir Thaddeus Wiki");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var basePath = string.IsNullOrWhiteSpace(localAppData) ? Path.GetTempPath() : localAppData;
        return Path.Combine(basePath, "SirThaddeus", "wiki-library");
    }

    private static string ResolveSettingsPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("ST_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath.Trim();

        return SettingsManager.GetSettingsPath();
    }

    private static string? TryReadLibraryDirectory(string settingsPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (TryGetProperty(root, "Wiki:LibraryDirectory", out var colonValue))
                return StringValue(colonValue);

            if (!TryGetProperty(root, "Wiki", out var wiki) || wiki.ValueKind != JsonValueKind.Object)
                return null;

            return TryGetProperty(wiki, "LibraryDirectory", out var value)
                ? StringValue(value)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? StringValue(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static object Fail(string message) => new
    {
        Ok = false,
        Message = message
    };

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value <= 0 ? min : value, min), max);

    private static string Bound(string value, int maxChars, out bool truncated)
    {
        var limit = Clamp(maxChars, 1_000, MaxPageReadChars);
        truncated = value.Length > limit;
        return truncated ? value[..limit] : value;
    }

    private static int CountOccurrences(string source, string value)
    {
        if (source.Length == 0 || value.Length == 0) return 0;
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string source, string value, string replacement)
    {
        var index = source.IndexOf(value, StringComparison.Ordinal);
        return index < 0
            ? source
            : string.Concat(source.AsSpan(0, index), replacement, source.AsSpan(index + value.Length));
    }
}