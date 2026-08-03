using System.Text.Json;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public sealed class WikiExplicitOperationToolPolicyTests
{
    [Fact]
    public void Selected_target_without_operation_preserves_reads_and_withholds_all_writes()
    {
        var projection = WikiExplicitOperationToolPolicy.Project(
            PageTarget(operation: null),
            [
                Tool("wiki_page_read"),
                Tool("wiki_roots_list"),
                Tool("wiki_page_create"),
                Tool("wiki_page_update_by_name"),
                Tool("wiki_page_rename_by_name"),
                Tool("wiki_page_delete_by_name"),
                Tool("wiki_root_rename"),
                Tool("web_search"),
            ]);

        Assert.True(projection.Active);
        Assert.Equal(5, projection.WithheldWriteCount);
        Assert.Equal(
            ["wiki_page_read", "wiki_roots_list", "web_search"],
            projection.Tools.Select(tool => tool.Function.Name));
    }

    [Fact]
    public void Typed_operation_and_absent_target_leave_gate_inactive()
    {
        var tools = new[] { Tool("wiki_page_update_by_name") };

        Assert.False(WikiExplicitOperationToolPolicy.Project(null, tools).Active);
        Assert.False(WikiExplicitOperationToolPolicy.Project(
            PageTarget(WikiMutationOperation.PageUpdate), tools).Active);
    }

    [Fact]
    public void Explicit_read_remains_write_guarded()
    {
        var target = PageTarget(WikiMutationOperation.PageRead);
        var projection = WikiExplicitOperationToolPolicy.Project(
            target,
            [Tool("wiki_page_read"), Tool("wiki_page_update_by_name")]);
        var write = WikiExplicitOperationToolPolicy.EvaluateCall(
            target,
            "wiki_page_update_by_name");

        Assert.True(projection.Active);
        Assert.Equal(["wiki_page_read"], projection.Tools.Select(tool => tool.Function.Name));
        Assert.True(write.Active);
        Assert.False(write.Allowed);
    }

    [Fact]
    public void Execution_gate_blocks_unadvertised_write_but_allows_read()
    {
        var target = PageTarget(operation: null);

        var write = WikiExplicitOperationToolPolicy.EvaluateCall(
            target,
            "wiki_page_update_by_name");
        var read = WikiExplicitOperationToolPolicy.EvaluateCall(target, "wiki_page_read");

        Assert.True(write.Active);
        Assert.False(write.Allowed);
        Assert.True(read.Active);
        Assert.True(read.Allowed);
        using var blocked = JsonDocument.Parse(
            WikiExplicitOperationToolPolicy.BuildBlockedResult(target));
        Assert.Equal(
            "wiki_explicit_operation_required",
            blocked.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static WikiMutationTarget PageTarget(WikiMutationOperation? operation) => new(
        WikiMutationTargetKind.Page,
        "root-1",
        "Project",
        "page-1",
        "Plan",
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
