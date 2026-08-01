using System.Text.Json;

namespace SirThaddeus.Agent.Pipeline;

public enum WikiMutationTargetKind
{
    Root,
    Page,
}

/// <summary>
/// Existing Wiki state explicitly selected by the user as the mutation scope
/// for one turn. Opaque identifiers stay outside the model prompt; display
/// names let the model bind by-name tools while execution verifies either
/// representation.
/// </summary>
public sealed record WikiMutationTarget(
    WikiMutationTargetKind Kind,
    string RootId,
    string RootName,
    string? PageId = null,
    string? PageTitle = null)
{
    public string DisplayName => Kind == WikiMutationTargetKind.Page
        ? $"{RootName} / {PageTitle}"
        : RootName;
}

public sealed record WikiMutationTargetDecision(bool Active, bool Allowed, string Reason);

/// <summary>
/// Deterministic final authority for Wiki mutations when a user selected a
/// typed target. This never infers intent, repairs arguments, or chooses a
/// replacement target.
/// </summary>
public static class WikiMutationTargetGuard
{
    private static readonly HashSet<string> WikiWriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "wiki_root_create", "WikiRootCreate",
        "wiki_root_rename", "WikiRootRename",
        "wiki_root_remove", "WikiRootRemove",
        "wiki_folder_create", "WikiFolderCreate",
        "wiki_folder_rename", "WikiFolderRename",
        "wiki_folder_move", "WikiFolderMove",
        "wiki_folder_delete", "WikiFolderDelete",
        "wiki_page_create", "WikiPageCreate",
        "wiki_page_create_by_name", "WikiPageCreateByName",
        "wiki_page_update", "WikiPageUpdate",
        "wiki_page_update_by_name", "WikiPageUpdateByName",
        "wiki_page_rename", "WikiPageRename",
        "wiki_page_rename_by_name", "WikiPageRenameByName",
        "wiki_page_move", "WikiPageMove",
        "wiki_page_delete", "WikiPageDelete",
        "wiki_page_delete_by_name", "WikiPageDeleteByName",
        "wiki_page_patch_selection", "WikiPagePatchSelection",
        "wiki_page_patch_selection_by_name", "WikiPagePatchSelectionByName",
        "wiki_page_revision_restore", "WikiPageRevisionRestore",
    };

    private static readonly HashSet<string> RootIdTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "wiki_root_rename", "WikiRootRename",
        "wiki_root_remove", "WikiRootRemove",
        "wiki_folder_create", "WikiFolderCreate",
        "wiki_folder_rename", "WikiFolderRename",
        "wiki_folder_move", "WikiFolderMove",
        "wiki_folder_delete", "WikiFolderDelete",
        "wiki_page_create", "WikiPageCreate",
    };

    private static readonly HashSet<string> RootNameCreateTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "wiki_page_create_by_name", "WikiPageCreateByName",
    };

    private static readonly HashSet<string> PageIdTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "wiki_page_update", "WikiPageUpdate",
        "wiki_page_rename", "WikiPageRename",
        "wiki_page_move", "WikiPageMove",
        "wiki_page_delete", "WikiPageDelete",
        "wiki_page_patch_selection", "WikiPagePatchSelection",
        "wiki_page_revision_restore", "WikiPageRevisionRestore",
    };

    private static readonly HashSet<string> PageNameTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "wiki_page_update_by_name", "WikiPageUpdateByName",
        "wiki_page_rename_by_name", "WikiPageRenameByName",
        "wiki_page_delete_by_name", "WikiPageDeleteByName",
        "wiki_page_patch_selection_by_name", "WikiPagePatchSelectionByName",
    };

    public static WikiMutationTargetDecision Evaluate(
        WikiMutationTarget? target,
        string toolName,
        string arguments)
    {
        if (target is null || !WikiWriteTools.Contains(toolName))
            return new WikiMutationTargetDecision(false, true, "inactive");

        if (!TryParseObject(arguments, out var args))
            return Block("invalid-target-arguments");

        return target.Kind == WikiMutationTargetKind.Page
            ? EvaluatePageTarget(target, toolName, args)
            : EvaluateRootTarget(target, toolName, args);
    }

    public static string BuildBlockedResult(WikiMutationTarget target) =>
        JsonSerializer.Serialize(new
        {
            error = new
            {
                code = "wiki_mutation_target_mismatch",
                message = $"Blocked: this turn may mutate only the selected Wiki {target.Kind.ToString().ToLowerInvariant()} '{target.DisplayName}'. Stop rather than changing scope.",
            },
        });

    private static WikiMutationTargetDecision EvaluatePageTarget(
        WikiMutationTarget target,
        string toolName,
        JsonElement args)
    {
        if (PageIdTools.Contains(toolName))
            return Exact(ReadString(args, "pageId", "page_id"), target.PageId)
                ? Allow()
                : Block("page-id-mismatch");

        if (PageNameTools.Contains(toolName))
        {
            var rootMatches = Exact(ReadString(args, "rootName", "root_name"), target.RootName);
            var pageMatches = Exact(ReadString(args, "pageTitle", "page_title"), target.PageTitle);
            return rootMatches && pageMatches ? Allow() : Block("page-name-mismatch");
        }

        return Block("tool-outside-page-target");
    }

    private static WikiMutationTargetDecision EvaluateRootTarget(
        WikiMutationTarget target,
        string toolName,
        JsonElement args)
    {
        if (RootIdTools.Contains(toolName))
            return Exact(ReadString(args, "rootId", "root_id"), target.RootId)
                ? Allow()
                : Block("root-id-mismatch");

        if (RootNameCreateTools.Contains(toolName))
            return Exact(ReadString(args, "rootName", "root_name"), target.RootName)
                ? Allow()
                : Block("root-name-mismatch");

        if (PageNameTools.Contains(toolName))
            return Exact(ReadString(args, "rootName", "root_name"), target.RootName)
                ? Allow()
                : Block("root-name-mismatch");

        return Block("tool-target-not-provably-inside-root");
    }

    private static WikiMutationTargetDecision Allow() => new(true, true, "exact-target");
    private static WikiMutationTargetDecision Block(string reason) => new(true, false, reason);

    private static bool Exact(string? actual, string? expected) =>
        !string.IsNullOrWhiteSpace(actual) &&
        !string.IsNullOrWhiteSpace(expected) &&
        string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool TryParseObject(string value, out JsonElement root)
    {
        root = default;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.String)
                continue;
            return property.Value.GetString();
        }
        return null;
    }
}
