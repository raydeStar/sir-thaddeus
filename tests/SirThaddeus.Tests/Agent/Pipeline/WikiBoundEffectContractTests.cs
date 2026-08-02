using System.Text.Json;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline;

public sealed class WikiBoundEffectContractTests
{
    [Theory]
    [InlineData(WikiMutationOperation.PageRename, "wiki_page_rename_by_name", "newTitle")]
    [InlineData(WikiMutationOperation.PageUpdate, "wiki_page_update_by_name", "markdown")]
    [InlineData(WikiMutationOperation.PageDelete, "wiki_page_delete_by_name", null)]
    public void Page_projection_exposes_only_operation_payload(
        WikiMutationOperation operation,
        string toolName,
        string? expectedProperty)
    {
        var target = PageTarget(operation);
        var projection = WikiBoundEffectContract.Project(target, [Tool(toolName), Tool("wiki_page_read")]);

        Assert.True(projection.Active);
        Assert.True(projection.ToolAvailable);
        Assert.Equal(toolName, projection.ToolName);
        var tool = Assert.Single(projection.Tools);
        var json = JsonSerializer.Serialize(tool.Function!.Parameters);
        if (expectedProperty is not null)
            Assert.Contains(expectedProperty, json, StringComparison.Ordinal);
        Assert.DoesNotContain("rootName", json, StringComparison.Ordinal);
        Assert.DoesNotContain("pageTitle", json, StringComparison.Ordinal);
        Assert.DoesNotContain("pageId", json, StringComparison.Ordinal);
        Assert.Contains("must never be copied", tool.Function.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_rename_binding_restores_exact_runtime_owned_names()
    {
        var target = PageTarget(WikiMutationOperation.PageRename);
        var binding = WikiBoundEffectContract.Bind(
            target,
            "wiki_page_rename_by_name",
            "{\"newTitle\":\"Launch Final\"}");

        Assert.True(binding.Active);
        Assert.True(binding.Allowed);
        using var document = JsonDocument.Parse(binding.Arguments);
        Assert.Equal("Harbor Notes", document.RootElement.GetProperty("rootName").GetString());
        Assert.Equal("Launch Plan", document.RootElement.GetProperty("pageTitle").GetString());
        Assert.Equal("Launch Final", document.RootElement.GetProperty("newTitle").GetString());
    }

    [Fact]
    public void Root_page_create_binding_restores_only_root_identity()
    {
        var target = new WikiMutationTarget(
            WikiMutationTargetKind.Root,
            "root-7",
            "Harbor Notes",
            Operation: WikiMutationOperation.PageCreate);
        var binding = WikiBoundEffectContract.Bind(
            target,
            "wiki_page_create",
            "{\"title\":\"Decision\",\"markdown\":\"Decision: GO\"}");

        Assert.True(binding.Allowed);
        using var document = JsonDocument.Parse(binding.Arguments);
        Assert.Equal("root-7", document.RootElement.GetProperty("rootId").GetString());
        Assert.Equal("Decision", document.RootElement.GetProperty("title").GetString());
        Assert.False(document.RootElement.TryGetProperty("folderId", out _));
    }

    [Theory]
    [InlineData("{\"newTitle\":\"Final\",\"pageTitle\":\"Other\"}")]
    [InlineData("{\"newTitle\":\"\"}")]
    [InlineData("not-json")]
    public void Binding_rejects_target_leakage_or_invalid_payload(string arguments)
    {
        var binding = WikiBoundEffectContract.Bind(
            PageTarget(WikiMutationOperation.PageRename),
            "wiki_page_rename_by_name",
            arguments);

        Assert.True(binding.Active);
        Assert.False(binding.Allowed);
        Assert.Equal("{}", binding.Arguments);
    }

    [Fact]
    public void Binding_removes_runtime_owned_work_plan_suffix_from_payload()
    {
        var binding = WikiBoundEffectContract.Bind(
            PageTarget(WikiMutationOperation.PageUpdate),
            "wiki_page_update_by_name",
            "{\"markdown\":\"# Shift Brief\\n- All checks passed\\n[USER-APPROVED WORK PLAN]\\n1. Internal step\"}");

        Assert.True(binding.Allowed);
        using var document = JsonDocument.Parse(binding.Arguments);
        Assert.Equal(
            "# Shift Brief\n- All checks passed",
            document.RootElement.GetProperty("markdown").GetString());
    }

    [Fact]
    public void Binding_rejects_required_payload_that_contains_only_runtime_metadata()
    {
        var binding = WikiBoundEffectContract.Bind(
            PageTarget(WikiMutationOperation.PageUpdate),
            "wiki_page_update_by_name",
            "{\"markdown\":\"[USER-APPROVED WORK PLAN]\\n1. Internal step\"}");

        Assert.False(binding.Allowed);
        Assert.Equal("invalid-payload", binding.Reason);
    }

    [Fact]
    public void Contract_is_inactive_without_approved_operation_and_fails_closed_when_tool_is_missing()
    {
        var inactive = WikiBoundEffectContract.Project(PageTarget(null), [Tool("wiki_page_read")]);
        Assert.False(inactive.Active);

        var unavailable = WikiBoundEffectContract.Project(
            PageTarget(WikiMutationOperation.PageRename),
            [Tool("wiki_page_read")]);
        Assert.True(unavailable.Active);
        Assert.False(unavailable.ToolAvailable);
        Assert.Empty(unavailable.Tools);
    }

    [Theory]
    [InlineData("page_rename", WikiMutationOperation.PageRename)]
    [InlineData("page-rename", WikiMutationOperation.PageRename)]
    [InlineData("RootRename", WikiMutationOperation.RootRename)]
    public void Operation_parser_accepts_public_spellings(string value, WikiMutationOperation expected)
    {
        Assert.True(WikiBoundEffectContract.TryParseOperation(value, out var actual));
        Assert.Equal(expected, actual);
    }

    private static WikiMutationTarget PageTarget(WikiMutationOperation? operation) => new(
        WikiMutationTargetKind.Page,
        "root-7",
        "Harbor Notes",
        "page-9",
        "Launch Plan",
        operation);

    private static ToolDefinition Tool(string name) => new()
    {
        Function = new FunctionDefinition
        {
            Name = name,
            Description = "test",
            Parameters = new Dictionary<string, object>(),
        },
    };
}
