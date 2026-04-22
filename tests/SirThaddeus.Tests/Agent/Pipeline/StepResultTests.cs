using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;

namespace SirThaddeus.Tests.Agent.Pipeline;

public class StepResultTests
{
    [Fact]
    public void Continue_carries_the_next_context()
    {
        var ctx = new TurnContext { ThreadId = "t1", MessageId = "m1", UserText = "hi" };

        StepResult result = new StepResult.Continue(ctx);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
    }

    [Fact]
    public void Terminate_carries_the_final_response()
    {
        var response = new AgentResponse { Text = "hello back" };

        StepResult result = new StepResult.Terminate(response);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Same(response, term.Response);
    }

    [Fact]
    public void Pattern_matching_exhausts_both_variants()
    {
        var ctx = new TurnContext { ThreadId = "t1", MessageId = "m1", UserText = "hi" };
        var response = new AgentResponse { Text = "hello back" };

        string Describe(StepResult r) => r switch
        {
            StepResult.Continue c => $"continue-with-user={c.Next.UserText}",
            StepResult.Terminate t => $"terminate-with-text={t.Response.Text}",
            _ => throw new InvalidOperationException("StepResult has only two nested variants — this should be unreachable."),
        };

        Assert.Equal("continue-with-user=hi", Describe(new StepResult.Continue(ctx)));
        Assert.Equal("terminate-with-text=hello back", Describe(new StepResult.Terminate(response)));
    }

    [Fact]
    public void Failure_path_uses_Terminate_with_FromError()
    {
        var failure = AgentResponse.FromError("mcp transport offline");

        StepResult result = new StepResult.Terminate(failure);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.False(term.Response.Success);
        Assert.Equal("mcp transport offline", term.Response.Error);
        Assert.Equal("mcp transport offline", term.Response.Text);
    }

    [Fact]
    public void StepResult_hierarchy_is_sealed_at_the_two_variants()
    {
        // Both concrete variants are sealed records. Surface check — the
        // point is to make exhaustive switching safe; a third variant
        // would require editing this test AND every switch expression.
        Assert.True(typeof(StepResult.Continue).IsSealed);
        Assert.True(typeof(StepResult.Terminate).IsSealed);
    }
}
