using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline;

public class TurnContextTests
{
    [Fact]
    public void Construction_requires_ThreadId_MessageId_UserText()
    {
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "hi",
        };

        Assert.Equal("t1", ctx.ThreadId);
        Assert.Equal("m1", ctx.MessageId);
        Assert.Equal("hi", ctx.UserText);
        Assert.False(ctx.IsAutomationRun);
        Assert.Null(ctx.Features);
        Assert.Empty(ctx.LlmMessages);
        Assert.Empty(ctx.ToolDefs);
        Assert.Null(ctx.AssistantDraft);
        Assert.Empty(ctx.ToolCallsMade);
    }

    [Fact]
    public void With_expression_produces_new_instance_leaving_original_untouched()
    {
        var original = new TurnContext { ThreadId = "t1", MessageId = "m1", UserText = "hi" };
        var features = RoutingFeatures.Extract("hi");

        var updated = original with { Features = features };

        // Updated has the new field.
        Assert.Same(features, updated.Features);
        // Original is unchanged (record immutability).
        Assert.Null(original.Features);
        // Untouched fields carry over.
        Assert.Equal(original.ThreadId, updated.ThreadId);
        Assert.Equal(original.MessageId, updated.MessageId);
        Assert.Equal(original.UserText, updated.UserText);
    }

    [Fact]
    public void Record_equality_is_by_value_across_fields()
    {
        var a = new TurnContext { ThreadId = "t1", MessageId = "m1", UserText = "hi" };
        var b = new TurnContext { ThreadId = "t1", MessageId = "m1", UserText = "hi" };

        // Same field values → equal even though they're distinct references.
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // Divergence in any field breaks equality.
        var c = a with { UserText = "bye" };
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void LlmMessages_and_ToolDefs_grow_via_with_without_mutation()
    {
        var ctx = new TurnContext { ThreadId = "t1", MessageId = "m1", UserText = "hi" };
        var withSystem = ctx with
        {
            LlmMessages = new[] { ChatMessage.System("you are a bot") },
        };
        var withSystemAndUser = withSystem with
        {
            LlmMessages = [.. withSystem.LlmMessages, ChatMessage.User("hi")],
        };

        Assert.Empty(ctx.LlmMessages);
        Assert.Single(withSystem.LlmMessages);
        Assert.Equal(2, withSystemAndUser.LlmMessages.Count);
    }

    [Fact]
    public void ToolCallsMade_defaults_to_empty_and_is_append_only_via_with()
    {
        var ctx = new TurnContext { ThreadId = "t1", MessageId = "m1", UserText = "hi" };
        var call = new ToolCallRecord
        {
            ToolName = "web_search",
            Arguments = "{\"q\":\"hi\"}",
            Result = "ok",
            Success = true,
        };

        var after = ctx with { ToolCallsMade = [.. ctx.ToolCallsMade, call] };

        Assert.Empty(ctx.ToolCallsMade);
        Assert.Single(after.ToolCallsMade);
        Assert.Equal("web_search", after.ToolCallsMade[0].ToolName);
    }
}
