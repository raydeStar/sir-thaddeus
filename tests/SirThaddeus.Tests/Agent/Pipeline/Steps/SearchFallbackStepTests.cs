using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.Agent.Search;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class SearchFallbackStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("SearchFallback", new SearchFallbackStep(executor: null, _ => null).Name);

    [Fact]
    public async Task No_op_when_executor_is_null()
    {
        // Runtime opted out — step is transparent.
        var step = new SearchFallbackStep(executor: null, _ => new SearchFallbackRequest { UserMessage = "hi" });
        var ctx = NewContext("hi", draft: "I don't know");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
    }

    [Fact]
    public async Task No_op_when_request_builder_returns_null()
    {
        // Most turns don't need a fallback. Returning null from the
        // builder is the "no trigger" signal — no executor call, no
        // allocation.
        var exec = new RecordingExecutor(new AgentResponse { Text = "fallback" });
        var step = new SearchFallbackStep(exec, _ => null);
        var ctx = NewContext("hi", draft: "direct answer");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
        Assert.Equal(0, exec.CallCount);
    }

    [Fact]
    public async Task Replaces_draft_when_fallback_produces_text()
    {
        // Triggered fallback with a good result → original draft is
        // replaced, any tool calls the fallback ran are appended.
        var fallbackResponse = new AgentResponse
        {
            Text = "Based on current sources: …",
            ToolCallsMade = new[]
            {
                new ToolCallRecord { ToolName = "web_search", Arguments = "{}", Result = "{}", Success = true },
            },
        };
        var exec = new RecordingExecutor(fallbackResponse);
        var step = new SearchFallbackStep(exec, _ => new SearchFallbackRequest { UserMessage = "hi" });
        var ctx = NewContext("hi", draft: "I don't know about that.");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("Based on current sources: …", cont.Next.AssistantDraft);
        Assert.Single(cont.Next.ToolCallsMade);
        Assert.Equal("web_search", cont.Next.ToolCallsMade[0].ToolName);
    }

    [Fact]
    public async Task Preserves_original_tool_calls_and_appends_fallback_calls()
    {
        // Original tool loop made some calls; fallback made more. The
        // merged record lets the UI activity log show the full sequence.
        var original = new ToolCallRecord { ToolName = "weather_geocode", Arguments = "{}", Result = "{}", Success = true };
        var extra = new ToolCallRecord { ToolName = "web_search", Arguments = "{}", Result = "{}", Success = true };
        var exec = new RecordingExecutor(new AgentResponse
        {
            Text = "fallback text",
            ToolCallsMade = new[] { extra },
        });
        var step = new SearchFallbackStep(exec, _ => new SearchFallbackRequest { UserMessage = "x" });
        var ctx = NewContext("x", draft: "original draft") with
        {
            ToolCallsMade = new[] { original },
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(2, cont.Next.ToolCallsMade.Count);
        Assert.Equal("weather_geocode", cont.Next.ToolCallsMade[0].ToolName);
        Assert.Equal("web_search", cont.Next.ToolCallsMade[1].ToolName);
    }

    [Fact]
    public async Task Leaves_draft_untouched_when_fallback_text_is_empty()
    {
        // Empty fallback = nothing to adopt. Original draft stays.
        var exec = new RecordingExecutor(new AgentResponse { Text = "" });
        var step = new SearchFallbackStep(exec, _ => new SearchFallbackRequest { UserMessage = "x" });
        var ctx = NewContext("x", draft: "I don't know");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("I don't know", cont.Next.AssistantDraft);
    }

    [Fact]
    public async Task Executor_exception_is_swallowed_and_draft_preserved()
    {
        // Fallback is opportunistic; transport failure = keep the primary
        // draft. User never loses their answer to a bad network.
        var exec = new ThrowingExecutor(new InvalidOperationException("searx offline"));
        var step = new SearchFallbackStep(exec, _ => new SearchFallbackRequest { UserMessage = "x" });
        var ctx = NewContext("x", draft: "original");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("original", cont.Next.AssistantDraft);
    }

    [Fact]
    public async Task Cancellation_bubbles_up_even_when_executor_fails()
    {
        var exec = new ThrowingExecutor(new OperationCanceledException());
        var step = new SearchFallbackStep(exec, _ => new SearchFallbackRequest { UserMessage = "x" });
        var ctx = NewContext("x", draft: "original");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(ctx, cts.Token));
    }

    [Fact]
    public void Construction_rejects_null_request_builder()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SearchFallbackStep(executor: null, buildRequest: null!));
    }

    private static TurnContext NewContext(string userText, string draft) => new()
    {
        ThreadId = "t1",
        MessageId = "m1",
        UserText = userText,
        AssistantDraft = draft,
    };

    private sealed class RecordingExecutor : ISearchFallbackExecutor
    {
        private readonly AgentResponse _response;
        public int CallCount { get; private set; }

        public RecordingExecutor(AgentResponse response) { _response = response; }

        public Task<AgentResponse> ExecuteAsync(SearchFallbackRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_response);
        }
    }

    private sealed class ThrowingExecutor : ISearchFallbackExecutor
    {
        private readonly Exception _ex;
        public ThrowingExecutor(Exception ex) { _ex = ex; }

        public Task<AgentResponse> ExecuteAsync(SearchFallbackRequest request, CancellationToken cancellationToken = default)
            => Task.FromException<AgentResponse>(_ex);
    }
}
