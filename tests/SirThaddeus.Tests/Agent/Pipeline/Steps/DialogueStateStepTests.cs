using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class DialogueStateStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("DialogueState", new DialogueStateStep(NullDialogueStateAccessor.Instance).Name);

    [Fact]
    public async Task No_op_when_accessor_is_null()
    {
        // Runtimes that don't persist dialogue state (tests, minimal UI)
        // compose the pipeline without an accessor.
        var step = new DialogueStateStep(null);
        var ctx = WithSystem("base");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
    }

    [Fact]
    public async Task No_op_when_stored_state_has_no_usable_signals()
    {
        // Fresh conversation — accessor returns a default empty state.
        // Injecting an empty [CONVERSATION CONTEXT] block would just
        // cost tokens, so the step skips it.
        var accessor = NullDialogueStateAccessor.Instance;
        var step = new DialogueStateStep(accessor);
        var ctx = WithSystem("base");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx.LlmMessages, cont.Next.LlmMessages);
    }

    [Fact]
    public async Task Appends_continuity_block_when_topic_is_set()
    {
        var accessor = new ThreadScopedDialogueStateAccessor();
        accessor.Update("t1", new DialogueState { Topic = "quarterly report" });
        var step = new DialogueStateStep(accessor);
        var ctx = WithSystem("You are Sir Thaddeus.");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        var system = Assert.Single(cont.Next.LlmMessages.Where(m => m.Role == "system"));
        Assert.Contains("[CONVERSATION CONTEXT]", system.Content);
        Assert.Contains("Topic: quarterly report", system.Content);
    }

    [Fact]
    public async Task Appends_all_usable_signals_in_order()
    {
        // When topic + location + time scope are all set, the block
        // lists them top-down so the model can scan quickly.
        var accessor = new ThreadScopedDialogueStateAccessor();
        accessor.Update("t1", new DialogueState
        {
            Topic = "weather",
            LocationName = "Olympia, WA",
            TimeScope = "tomorrow",
        });
        var step = new DialogueStateStep(accessor);
        var ctx = WithSystem("base");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        var system = cont.Next.LlmMessages.First(m => m.Role == "system").Content!;
        var topicIdx = system.IndexOf("Topic:", StringComparison.Ordinal);
        var locIdx = system.IndexOf("Location:", StringComparison.Ordinal);
        var timeIdx = system.IndexOf("Time scope:", StringComparison.Ordinal);
        Assert.True(topicIdx > 0);
        Assert.True(locIdx > topicIdx);
        Assert.True(timeIdx > locIdx);
        Assert.Contains("Olympia, WA", system);
        Assert.Contains("tomorrow", system);
    }

    [Fact]
    public async Task Uses_context_threadId_to_fetch_from_accessor()
    {
        // Thread-scoped accessors partition state by conversation id.
        // The step must look up the correct partition for this turn.
        var accessor = new ThreadScopedDialogueStateAccessor();
        accessor.Update("t-work", new DialogueState { Topic = "quarterly report" });
        accessor.Update("t-personal", new DialogueState { Topic = "dinner ideas" });

        var step = new DialogueStateStep(accessor);
        var workCtx = WithSystem("base") with { ThreadId = "t-work" };
        var homeCtx = WithSystem("base") with { ThreadId = "t-personal" };

        var workResult = await step.ExecuteAsync(workCtx, CancellationToken.None);
        var homeResult = await step.ExecuteAsync(homeCtx, CancellationToken.None);

        var workSystem = ((StepResult.Continue)workResult).Next.LlmMessages.First(m => m.Role == "system").Content!;
        var homeSystem = ((StepResult.Continue)homeResult).Next.LlmMessages.First(m => m.Role == "system").Content!;
        Assert.Contains("quarterly report", workSystem);
        Assert.DoesNotContain("dinner", workSystem);
        Assert.Contains("dinner ideas", homeSystem);
        Assert.DoesNotContain("quarterly", homeSystem);
    }

    [Fact]
    public async Task Honours_pre_cancelled_token()
    {
        var step = new DialogueStateStep(NullDialogueStateAccessor.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(WithSystem("x"), cts.Token));
    }

    private static TurnContext WithSystem(string systemPrompt) => new()
    {
        ThreadId = "t1",
        MessageId = "m1",
        UserText = "hi",
        LlmMessages = new[] { ChatMessage.System(systemPrompt), ChatMessage.User("hi") },
    };
}
