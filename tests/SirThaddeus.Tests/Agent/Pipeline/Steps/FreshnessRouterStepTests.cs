using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class FreshnessRouterStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("FreshnessRouter", new FreshnessRouterStep().Name);

    // ── Positive: prompts that should force tool_choice=web_search ──────

    [Theory]
    [InlineData("Does the iPhone 15 exist?")]
    [InlineData("is the Tesla Cybertruck real yet")]
    [InlineData("was the PS6 released")]
    [InlineData("Has macOS Sequoia come out yet?")]
    [InlineData("is there a new version of CUDA")]
    [InlineData("Did Nintendo ever release a Switch 2?")]
    public async Task Forces_web_search_on_existence_shapes(string userText)
    {
        var step = new FreshnessRouterStep();
        var ctx = WithTools(userText, "web_search");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("web_search", cont.Next.ForcedTool);
    }

    [Theory]
    [InlineData("what's the latest version of CUDA")]
    [InlineData("who is the current president of Argentina")]
    [InlineData("what year was the GPT-4 model released")]
    [InlineData("when did Starfield come out")]
    [InlineData("how much does a PlayStation 5 cost now")]
    [InlineData("what is the price of Bitcoin")]
    public async Task Forces_web_search_on_recency_and_pricing_shapes(string userText)
    {
        var step = new FreshnessRouterStep();
        var ctx = WithTools(userText, "web_search");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("web_search", cont.Next.ForcedTool);
    }

    // ── Negative: prompts that must NOT force a search ──────────────────
    // These are the cases the user worried about — "willy-nilly" search
    // on casual chat, opinion, and self-referential queries.

    [Theory]
    [InlineData("hi there")]
    [InlineData("hey, how are you doing")]
    [InlineData("thanks!")]
    public async Task Does_not_fire_on_casual_chat(string userText)
    {
        var step = new FreshnessRouterStep();
        var ctx = WithTools(userText, "web_search");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Null(cont.Next.ForcedTool);
    }

    [Theory]
    [InlineData("does this code work?")]
    [InlineData("is it correct to use a HashMap here?")]
    [InlineData("does this make sense")]
    [InlineData("is this right?")]
    [InlineData("what do you think of this approach")]
    [InlineData("what's your favorite color")]
    [InlineData("what's up")]
    [InlineData("what's your name")]
    public async Task Does_not_fire_on_self_referential_or_opinion(string userText)
    {
        // Exactly the "don't run off on a random search" concern.
        var step = new FreshnessRouterStep();
        var ctx = WithTools(userText, "web_search");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Null(cont.Next.ForcedTool);
    }

    [Theory]
    [InlineData("write me a poem about the sea")]
    [InlineData("explain how TCP works")]
    [InlineData("help me debug this stack trace")]
    [InlineData("can you refactor this function")]
    [InlineData("summarize the three-body problem plot")]
    public async Task Does_not_fire_on_generative_or_tutorial(string userText)
    {
        var step = new FreshnessRouterStep();
        var ctx = WithTools(userText, "web_search");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Null(cont.Next.ForcedTool);
    }

    // ── Guards ──────────────────────────────────────────────────────────

    [Fact]
    public async Task No_op_when_web_search_not_in_tool_list()
    {
        // If the footman narrowed tools such that web_search isn't
        // available, the router must NOT force a nonexistent tool —
        // otherwise tool_choice would reference a tool that isn't
        // advertised, which most providers reject with 400.
        var step = new FreshnessRouterStep();
        var ctx = WithTools("does the iPhone 15 exist?", "weather_forecast");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Null(cont.Next.ForcedTool);
    }

    [Fact]
    public async Task Respects_preexisting_forced_tool()
    {
        // If an earlier step already set a forced tool (e.g. a future
        // specialized router), we don't overwrite its decision.
        var step = new FreshnessRouterStep();
        var ctx = WithTools("does the iPhone 15 exist?", "web_search") with
        {
            ForcedTool = "places_lookup",
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("places_lookup", cont.Next.ForcedTool);
    }

    [Fact]
    public async Task Honors_pre_cancelled_token()
    {
        var step = new FreshnessRouterStep();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(WithTools("does X exist", "web_search"), cts.Token));
    }

    private static TurnContext WithTools(string userText, params string[] toolNames)
    {
        var defs = toolNames.Select(n => new ToolDefinition
        {
            Function = new FunctionDefinition
            {
                Name = n,
                Description = $"stub {n}",
                Parameters = new { type = "object" },
            }
        }).ToArray();
        return new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = userText,
            ToolDefs = defs,
        };
    }
}
