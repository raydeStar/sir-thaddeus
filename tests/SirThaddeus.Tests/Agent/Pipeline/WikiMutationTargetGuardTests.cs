using System.Text.Json;
using SirThaddeus.Agent.Pipeline;

namespace SirThaddeus.Tests.Agent.Pipeline;

public sealed class WikiMutationTargetGuardTests
{
    private static readonly WikiMutationTarget PageTarget = new(
        WikiMutationTargetKind.Page,
        "root-7",
        "Harbor Notes",
        "page-9",
        "Launch Plan");

    private static readonly WikiMutationTarget RootTarget = new(
        WikiMutationTargetKind.Root,
        "root-7",
        "Harbor Notes");

    [Theory]
    [InlineData("wiki_page_update", "{\"pageId\":\"page-9\",\"markdown\":\"x\",\"expectedVersion\":1}")]
    [InlineData("wiki_page_rename", "{\"page_id\":\"page-9\",\"title\":\"Final\",\"expectedVersion\":1}")]
    [InlineData("wiki_page_delete", "{\"pageId\":\"page-9\"}")]
    [InlineData("wiki_page_revision_restore", "{\"pageId\":\"page-9\",\"revisionId\":\"rev-2\",\"expectedVersion\":2}")]
    [InlineData("wiki_page_update_by_name", "{\"rootName\":\"Harbor Notes\",\"pageTitle\":\"Launch Plan\",\"markdown\":\"x\"}")]
    [InlineData("WikiPagePatchSelectionByName", "{\"root_name\":\"harbor notes\",\"page_title\":\" launch plan \",\"selectedText\":\"a\",\"replacementText\":\"b\"}")]
    public void Page_target_allows_only_exact_id_or_name_pairs(string toolName, string args)
    {
        var decision = WikiMutationTargetGuard.Evaluate(PageTarget, toolName, args);

        Assert.True(decision.Active);
        Assert.True(decision.Allowed);
        Assert.Equal("exact-target", decision.Reason);
    }

    [Theory]
    [InlineData("wiki_page_update", "{\"pageId\":\"page-10\",\"markdown\":\"x\",\"expectedVersion\":1}", "page-id-mismatch")]
    [InlineData("wiki_page_update_by_name", "{\"rootName\":\"Harbor Notes Archive\",\"pageTitle\":\"Launch Plan\",\"markdown\":\"x\"}", "page-name-mismatch")]
    [InlineData("wiki_page_update_by_name", "{\"rootName\":\"Harbor Notes\",\"pageTitle\":\"Overview\",\"markdown\":\"x\"}", "page-name-mismatch")]
    [InlineData("wiki_page_create", "{\"rootId\":\"root-7\",\"title\":\"New\",\"markdown\":\"x\"}", "tool-outside-page-target")]
    [InlineData("wiki_root_remove", "{\"rootId\":\"root-7\"}", "tool-outside-page-target")]
    [InlineData("wiki_page_delete", "not-json", "invalid-target-arguments")]
    public void Page_target_blocks_substitution_or_scope_expansion(
        string toolName,
        string args,
        string reason)
    {
        var decision = WikiMutationTargetGuard.Evaluate(PageTarget, toolName, args);

        Assert.True(decision.Active);
        Assert.False(decision.Allowed);
        Assert.Equal(reason, decision.Reason);
    }

    [Theory]
    [InlineData("wiki_root_rename", "{\"rootId\":\"root-7\",\"name\":\"New Name\"}")]
    [InlineData("wiki_page_create", "{\"rootId\":\"root-7\",\"title\":\"New\",\"markdown\":\"x\"}")]
    [InlineData("wiki_page_create_by_name", "{\"rootName\":\"harbor notes\",\"title\":\"New\",\"markdown\":\"x\"}")]
    [InlineData("wiki_folder_create", "{\"rootId\":\"root-7\",\"name\":\"Plans\"}")]
    [InlineData("wiki_page_update_by_name", "{\"rootName\":\"Harbor Notes\",\"pageTitle\":\"Plan\",\"markdown\":\"x\"}")]
    public void Root_target_allows_only_provable_root_scoped_mutations(string toolName, string args)
    {
        var decision = WikiMutationTargetGuard.Evaluate(RootTarget, toolName, args);

        Assert.True(decision.Active);
        Assert.True(decision.Allowed);
    }

    [Theory]
    [InlineData("wiki_root_remove", "{\"rootId\":\"archive-root\"}")]
    [InlineData("wiki_page_create_by_name", "{\"rootName\":\"Harbor Notes Archive\",\"title\":\"New\",\"markdown\":\"x\"}")]
    [InlineData("wiki_page_update", "{\"pageId\":\"page-9\",\"markdown\":\"x\",\"expectedVersion\":1}")]
    [InlineData("wiki_root_create", "{\"name\":\"Another Root\"}")]
    public void Root_target_blocks_off_root_or_unprovable_mutations(string toolName, string args)
    {
        var decision = WikiMutationTargetGuard.Evaluate(RootTarget, toolName, args);

        Assert.True(decision.Active);
        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Guard_is_inactive_without_target_or_for_non_wiki_write()
    {
        Assert.False(WikiMutationTargetGuard.Evaluate(null, "wiki_page_delete", "{}").Active);
        Assert.False(WikiMutationTargetGuard.Evaluate(PageTarget, "wiki_page_read", "{}").Active);
        Assert.False(WikiMutationTargetGuard.Evaluate(PageTarget, "web_search", "{}").Active);
    }

    [Fact]
    public void Blocked_result_is_structured_and_does_not_expose_opaque_ids()
    {
        var payload = WikiMutationTargetGuard.BuildBlockedResult(PageTarget);
        using var document = JsonDocument.Parse(payload);

        Assert.Equal(
            "wiki_mutation_target_mismatch",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("Harbor Notes / Launch Plan", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("root-7", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("page-9", payload, StringComparison.Ordinal);
    }
}
