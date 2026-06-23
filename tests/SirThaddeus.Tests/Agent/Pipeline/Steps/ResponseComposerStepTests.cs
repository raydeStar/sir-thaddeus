using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class ResponseComposerStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("ResponseComposer", new ResponseComposerStep().Name);

    [Fact]
    public async Task Terminates_with_AssistantDraft_as_final_text()
    {
        var step = new ResponseComposerStep();
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "hi",
            AssistantDraft = "hello back!",
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Equal("hello back!", term.Response.Text);
        Assert.True(term.Response.Success);
    }

    [Fact]
    public async Task Carries_ToolCallsMade_through_to_response()
    {
        var step = new ResponseComposerStep();
        var calls = new[]
        {
            new ToolCallRecord { ToolName = "web_search", Arguments = "{}", Result = "ok", Success = true },
            new ToolCallRecord { ToolName = "weather_geocode", Arguments = "{}", Result = "ok", Success = true },
        };
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "hi",
            AssistantDraft = "summary",
            ToolCallsMade = calls,
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Same(calls, term.Response.ToolCallsMade);
    }

    [Fact]
    public async Task Replaces_released_product_existence_draft_from_tool_evidence()
    {
        var step = new ResponseComposerStep();
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "Does Vendor Z1 exist as a released product?",
            AssistantDraft = "Vendor Z1 is probably real, based on future-model chatter.",
            ToolCallsMade =
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"Vendor Z1 release date\"}",
                    Result = """
                        [search: 1 result(s) returned]

                        <!-- SOURCES_JSON -->
                        {"sources":[{"url":"https://vendor.example/z1","title":"Vendor Z1 - Tech Specs","domain":"vendor.example","snippet":"Year introduced: 2024. Vendor Z1 tech specs."}]}
                        """,
                    Success = true
                }
            ]
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.StartsWith("Yes", term.Response.Text);
        Assert.Contains("Vendor Z1 exists as a released product", term.Response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2024", term.Response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("future-model chatter", term.Response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Falls_back_to_deterministic_empty_reply_marker_when_draft_blank(string? draft)
    {
        // The pipeline must never surface a silent empty string — it
        // confuses the UI and leaves users without feedback. Use a
        // stable marker so downstream can match / redact it.
        var step = new ResponseComposerStep();
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "hi",
            AssistantDraft = draft,
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Equal("(The model returned an empty response.)", term.Response.Text);
    }

    [Fact]
    public async Task Honours_pre_cancelled_token()
    {
        var step = new ResponseComposerStep();
        var ctx = new TurnContext { ThreadId = "t1", MessageId = "m1", UserText = "hi", AssistantDraft = "x" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(ctx, cts.Token));
    }
}
