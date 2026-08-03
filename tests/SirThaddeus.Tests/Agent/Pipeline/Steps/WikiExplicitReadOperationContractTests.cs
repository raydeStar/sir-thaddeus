using System.Text.Json;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public sealed class WikiExplicitReadOperationContractTests
{
    [Fact]
    public void Explicit_page_read_projects_exactly_one_payload_only_tool()
    {
        var projection = WikiExplicitReadOperationContract.Project(
            PageTarget(),
            [Tool("wiki_roots_list"), Tool("wiki_page_read"), Tool("web_search")]);

        Assert.True(projection.Active);
        Assert.True(projection.ToolAvailable);
        Assert.Equal("wiki_page_read", projection.ToolName);
        var read = Assert.Single(projection.Tools);
        var schema = JsonSerializer.Serialize(read.Function.Parameters);
        Assert.Contains("maxChars", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("pageId", schema, StringComparison.Ordinal);
        Assert.Contains("Project / Plan", read.Function.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_replaces_untrusted_identity_and_bounds_optional_size()
    {
        var binding = WikiExplicitReadOperationContract.Bind(
            PageTarget(),
            "wiki_page_read",
            "{\"pageId\":\"made-up\",\"maxChars\":999999}");

        Assert.True(binding.Active);
        Assert.True(binding.Allowed);
        using var arguments = JsonDocument.Parse(binding.Arguments);
        Assert.Equal("page-opaque-1", arguments.RootElement.GetProperty("pageId").GetString());
        Assert.Equal(60000, arguments.RootElement.GetProperty("maxChars").GetInt32());
    }

    [Fact]
    public void Context_only_page_write_operation_root_and_other_tool_are_inactive_or_blocked()
    {
        var tools = new[] { Tool("wiki_page_read") };
        var contextOnly = PageTarget() with { Operation = null };
        var write = PageTarget() with { Operation = WikiMutationOperation.PageUpdate };
        var root = new WikiMutationTarget(
            WikiMutationTargetKind.Root,
            "root-1",
            "Project",
            Operation: WikiMutationOperation.PageCreate);

        Assert.False(WikiExplicitReadOperationContract.Project(contextOnly, tools).Active);
        Assert.False(WikiExplicitReadOperationContract.Project(write, tools).Active);
        Assert.False(WikiExplicitReadOperationContract.Project(root, tools).Active);
        var wrongTool = WikiExplicitReadOperationContract.Bind(PageTarget(), "web_search", "{}");
        Assert.True(wrongTool.Active);
        Assert.False(wrongTool.Allowed);
    }

    [Fact]
    public void Verified_receipt_requires_success_matching_page_and_nonblank_markdown()
    {
        const string success =
            "{\"ok\":true,\"document\":{\"page\":{\"id\":\"page-opaque-1\"},\"markdown\":\"Launch code: CIRRUS\"}}";

        Assert.True(WikiExplicitReadOperationContract.TryBuildVerifiedReceipt(
            PageTarget(), "wiki_page_read", true, success, out var receipt));
        Assert.Contains("Project / Plan", receipt, StringComparison.Ordinal);
        Assert.Contains("Launch code: CIRRUS", receipt, StringComparison.Ordinal);

        Assert.False(WikiExplicitReadOperationContract.TryBuildVerifiedReceipt(
            PageTarget(), "wiki_page_read", true,
            "{\"ok\":true,\"document\":{\"page\":{\"id\":\"other\"},\"markdown\":\"CIRRUS\"}}",
            out _));
        Assert.False(WikiExplicitReadOperationContract.TryBuildVerifiedReceipt(
            PageTarget(), "wiki_page_read", false, success, out _));
        Assert.False(WikiExplicitReadOperationContract.TryBuildVerifiedReceipt(
            PageTarget(), "wiki_page_read", true, "not-json", out _));
    }

    private static WikiMutationTarget PageTarget() => new(
        WikiMutationTargetKind.Page,
        "root-1",
        "Project",
        "page-opaque-1",
        "Plan",
        WikiMutationOperation.PageRead);

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
