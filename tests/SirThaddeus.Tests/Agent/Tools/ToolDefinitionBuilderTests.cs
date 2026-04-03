using SirThaddeus.Agent;
using SirThaddeus.Agent.Tools;

namespace SirThaddeus.Tests;

public sealed class ToolDefinitionBuilderTests
{
    [Fact]
    public void FilterHarnessAllowedTools_KeepsOnlyExplicitlyAllowedTools_AndAliases()
    {
        IReadOnlyList<McpToolInfo> tools =
        [
            new() { Name = "web_search", Description = "web", InputSchema = new { type = "object" } },
            new() { Name = "WebSearch", Description = "web alias", InputSchema = new { type = "object" } },
            new() { Name = "browser_navigate", Description = "browser", InputSchema = new { type = "object" } },
            new() { Name = "file_list", Description = "files", InputSchema = new { type = "object" } }
        ];

        var filtered = ToolDefinitionBuilder.FilterHarnessAllowedTools(tools, ["web_search", "browser_navigate"]);

        Assert.Contains(filtered, tool => tool.Name == "web_search");
        Assert.Contains(filtered, tool => tool.Name == "WebSearch");
        Assert.Contains(filtered, tool => tool.Name == "browser_navigate");
        Assert.DoesNotContain(filtered, tool => tool.Name == "file_list");
    }

    [Fact]
    public void FilterHarnessAllowedTools_EmptyOverride_ReturnsOriginalToolList()
    {
        IReadOnlyList<McpToolInfo> tools =
        [
            new() { Name = "web_search", Description = "web", InputSchema = new { type = "object" } },
            new() { Name = "file_list", Description = "files", InputSchema = new { type = "object" } }
        ];

        var filtered = ToolDefinitionBuilder.FilterHarnessAllowedTools(tools, []);

        Assert.Equal(tools, filtered);
    }
}