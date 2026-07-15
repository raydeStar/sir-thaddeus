using System.ComponentModel;
using ModelContextProtocol.Server;
using SirThaddeus.Wiki;
using SirThaddeus.Wiki.Storage;

namespace SirThaddeus.McpServer.Tools;

public static partial class WikiMcpTools
{
    [McpServerTool(
        Name = "wiki_page_create_by_name",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Create a Markdown page by exact Wiki root name, avoiding opaque root ids. Fails when the root name is missing or ambiguous.")]
    public static Task<string> WikiPageCreateByName(
        [Description("Exact Wiki root display name.")] string rootName,
        [Description("Page title.")] string title,
        [Description("Markdown page body.")] string markdown,
        [Description("Optional exact folder display name inside the root. Omit to create at the root.")] string? folderName = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var rootResolution = await ResolveUniqueRootAsync(store, rootName, ct).ConfigureAwait(false);
            if (rootResolution.Failure is not null)
                return rootResolution.Failure;

            var folderResolution = await ResolveOptionalUniqueFolderAsync(
                store,
                rootResolution.Root!,
                folderName,
                ct).ConfigureAwait(false);
            if (folderResolution.Failure is not null)
                return folderResolution.Failure;

            var document = await store.CreatePageAsync(
                rootResolution.Root!.Id,
                folderResolution.Folder?.Id,
                title,
                markdown ?? string.Empty,
                ct).ConfigureAwait(false);
            return new { Ok = true, Document = document };
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_page_update_by_name",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Replace a Wiki page body by exact root name and current page title. Resolves the durable id and current version atomically inside the tool and fails on missing or ambiguous names.")]
    public static Task<string> WikiPageUpdateByName(
        [Description("Exact Wiki root display name.")] string rootName,
        [Description("Exact current page title.")] string pageTitle,
        [Description("Full replacement Markdown body.")] string markdown,
        [Description("Optional short revision summary.")] string? summary = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var resolution = await ResolveUniquePageAsync(store, rootName, pageTitle, ct).ConfigureAwait(false);
            if (resolution.Failure is not null)
                return resolution.Failure;

            var page = resolution.Page!;
            var document = await store.UpdatePageAsync(
                page.Id,
                markdown ?? string.Empty,
                page.Version,
                "agent",
                NormalizeOptional(summary),
                ct).ConfigureAwait(false);
            return document is null
                ? Fail($"Wiki page '{pageTitle}' was no longer available.")
                : new { Ok = true, Document = document };
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_page_rename_by_name",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Rename a Wiki page by exact root name and current page title. Resolves the durable id and current version inside the tool and fails on missing or ambiguous names.")]
    public static Task<string> WikiPageRenameByName(
        [Description("Exact Wiki root display name.")] string rootName,
        [Description("Exact current page title.")] string pageTitle,
        [Description("New page title.")] string newTitle,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var resolution = await ResolveUniquePageAsync(store, rootName, pageTitle, ct).ConfigureAwait(false);
            if (resolution.Failure is not null)
                return resolution.Failure;

            var page = resolution.Page!;
            var document = await store.RenamePageAsync(page.Id, newTitle, page.Version, ct).ConfigureAwait(false);
            return document is null
                ? Fail($"Wiki page '{pageTitle}' was no longer available.")
                : new { Ok = true, Document = document };
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_page_delete_by_name",
        ReadOnly = false,
        Idempotent = false,
        Destructive = true,
        OpenWorld = false),
     Description("Delete a Wiki page by exact root name and page title. Resolves the durable id inside the tool and fails on missing or ambiguous names.")]
    public static Task<string> WikiPageDeleteByName(
        [Description("Exact Wiki root display name.")] string rootName,
        [Description("Exact page title.")] string pageTitle,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var resolution = await ResolveUniquePageAsync(store, rootName, pageTitle, ct).ConfigureAwait(false);
            if (resolution.Failure is not null)
                return resolution.Failure;

            var page = resolution.Page!;
            var deleted = await store.DeletePageAsync(page.Id, ct).ConfigureAwait(false);
            return deleted
                ? new { Ok = true, Deleted = true, PageId = page.Id }
                : Fail($"Wiki page '{pageTitle}' was no longer available.");
        }, cancellationToken);
    }

    [McpServerTool(
        Name = "wiki_page_patch_selection_by_name",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = false),
     Description("Replace one exact text passage in a Wiki page selected by root name and page title. Fails on ambiguous targets or ambiguous text selections and preserves all other page content.")]
    public static Task<string> WikiPagePatchSelectionByName(
        [Description("Exact Wiki root display name.")] string rootName,
        [Description("Exact page title.")] string pageTitle,
        [Description("Exact selected text to replace. It must appear exactly once.")] string selectedText,
        [Description("Replacement text for the selected passage only.")] string replacementText,
        [Description("Optional short revision summary.")] string? summary = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async (store, ct) =>
        {
            var resolution = await ResolveUniquePageAsync(store, rootName, pageTitle, ct).ConfigureAwait(false);
            if (resolution.Failure is not null)
                return resolution.Failure;

            var page = resolution.Page!;
            var document = await store.GetPageAsync(page.Id, ct).ConfigureAwait(false);
            if (document is null)
                return Fail($"Wiki page '{pageTitle}' was no longer available.");

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
                page.Id,
                markdown,
                document.Page.Version,
                "agent",
                NormalizeOptional(summary) ?? "Selection patch",
                ct).ConfigureAwait(false);
            return updated is null
                ? Fail($"Wiki page '{pageTitle}' was no longer available.")
                : new { Ok = true, Document = updated };
        }, cancellationToken);
    }

    private static async Task<RootResolution> ResolveUniqueRootAsync(
        LocalWikiStore store,
        string? rootName,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeOptional(rootName);
        if (normalized is null)
            return new(null, Fail("Wiki root name is required."));

        var roots = await store.ListRootsAsync(cancellationToken).ConfigureAwait(false);
        var matches = roots
            .Where(root => string.Equals(root.Name, normalized, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            1 => new(matches[0], null),
            0 => new(null, Fail($"No Wiki root is named '{normalized}'.")),
            _ => new(null, Fail($"More than one Wiki root is named '{normalized}'; use an unambiguous target."))
        };
    }

    private static async Task<PageResolution> ResolveUniquePageAsync(
        LocalWikiStore store,
        string? rootName,
        string? pageTitle,
        CancellationToken cancellationToken)
    {
        var rootResolution = await ResolveUniqueRootAsync(store, rootName, cancellationToken).ConfigureAwait(false);
        if (rootResolution.Failure is not null)
            return new(null, rootResolution.Failure);

        var normalizedTitle = NormalizeOptional(pageTitle);
        if (normalizedTitle is null)
            return new(null, Fail("Wiki page title is required."));

        var tree = await store.GetTreeAsync(rootResolution.Root!.Id, cancellationToken).ConfigureAwait(false);
        if (tree is null)
            return new(null, Fail($"Wiki root '{rootResolution.Root.Name}' was no longer available."));

        var matches = tree.Pages
            .Where(page => string.Equals(page.Title, normalizedTitle, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            1 => new(matches[0], null),
            0 => new(null, Fail($"No page titled '{normalizedTitle}' exists in Wiki root '{rootResolution.Root.Name}'.")),
            _ => new(null, Fail($"More than one page is titled '{normalizedTitle}' in Wiki root '{rootResolution.Root.Name}'; use an unambiguous target."))
        };
    }

    private static async Task<FolderResolution> ResolveOptionalUniqueFolderAsync(
        LocalWikiStore store,
        WikiRoot root,
        string? folderName,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeOptional(folderName);
        if (normalized is null)
            return new(null, null);

        var tree = await store.GetTreeAsync(root.Id, cancellationToken).ConfigureAwait(false);
        if (tree is null)
            return new(null, Fail($"Wiki root '{root.Name}' was no longer available."));

        var matches = tree.Folders
            .Where(folder => string.Equals(folder.Name, normalized, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            1 => new(matches[0], null),
            0 => new(null, Fail($"No folder named '{normalized}' exists in Wiki root '{root.Name}'.")),
            _ => new(null, Fail($"More than one folder is named '{normalized}' in Wiki root '{root.Name}'; use an unambiguous target."))
        };
    }

    private sealed record RootResolution(WikiRoot? Root, object? Failure);
    private sealed record PageResolution(WikiPage? Page, object? Failure);
    private sealed record FolderResolution(WikiFolder? Folder, object? Failure);
}
