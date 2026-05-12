using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class MemoryContextStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("MemoryContext", new MemoryContextStep(provider: null).Name);

    [Fact]
    public async Task No_op_when_provider_is_null()
    {
        // No wired provider = the step is invisible. Lets runtimes without
        // a memory stack keep composing the pipeline without conditional
        // "add step only if" logic at the call site.
        var step = new MemoryContextStep(provider: null);
        var ctx = WithSystemPrompt("base");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
    }

    [Fact]
    public async Task No_op_when_provider_returns_empty_pack_text()
    {
        // Retrieval succeeded but there was nothing to say — leave the
        // system prompt untouched. Avoids injecting an empty [REMEMBERED
        // CONTEXT] block that would just cost tokens.
        var provider = new StubProvider(new MemoryContextResult { PackText = "   " });
        var step = new MemoryContextStep(provider);
        var ctx = WithSystemPrompt("base");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx.LlmMessages, cont.Next.LlmMessages);
    }

    [Fact]
    public async Task Appends_memory_pack_to_existing_system_message()
    {
        var provider = new StubProvider(new MemoryContextResult
        {
            PackText = "user name: Mark\nuser location: Olympia, WA",
        });
        var step = new MemoryContextStep(provider);
        var ctx = WithSystemPrompt("You are Sir Thaddeus.");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        var system = Assert.Single(cont.Next.LlmMessages, m => m.Role == "system");
        Assert.StartsWith("You are Sir Thaddeus.", system.Content);
        Assert.Contains("REMEMBERED CONTEXT", system.Content);
        Assert.Contains("Olympia, WA", system.Content);
    }

    [Fact]
    public async Task Inserts_system_message_when_none_seeded()
    {
        // Supports minimal pipeline configurations where the facade has
        // not yet added a system prompt (future orderings, tests).
        var provider = new StubProvider(new MemoryContextResult { PackText = "note: user prefers terse answers" });
        var step = new MemoryContextStep(provider);
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "hi",
            LlmMessages = new[] { ChatMessage.User("hi") },
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(2, cont.Next.LlmMessages.Count);
        Assert.Equal("system", cont.Next.LlmMessages[0].Role);
        Assert.Contains("REMEMBERED CONTEXT", cont.Next.LlmMessages[0].Content);
    }

    [Fact]
    public async Task Provider_exception_is_swallowed_and_turn_continues()
    {
        // Transport problems in the memory store must never abort a turn —
        // the user still gets an answer, just without remembered context.
        var provider = new ThrowingProvider(new InvalidOperationException("memory store offline"));
        var step = new MemoryContextStep(provider);
        var ctx = WithSystemPrompt("base");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx.LlmMessages, cont.Next.LlmMessages);
    }

    [Fact]
    public async Task Cancellation_from_caller_bubbles_up_even_when_provider_fails()
    {
        // Genuine cancellation (user pressed stop) must propagate, not be
        // swallowed as a memory failure.
        var provider = new ThrowingProvider(new OperationCanceledException());
        var step = new MemoryContextStep(provider);
        var ctx = WithSystemPrompt("base");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(ctx, cts.Token));
    }

    [Fact]
    public async Task Custom_request_builder_lets_caller_set_profile_and_toggle()
    {
        // Runtimes can pass extra context (active personality, memory
        // enabled flag from settings) via the builder hook.
        MemoryContextRequest? captured = null;
        var provider = new RecordingProvider(req => { captured = req; return new MemoryContextResult { PackText = "x" }; });
        var step = new MemoryContextStep(provider, ctx => new MemoryContextRequest
        {
            UserMessage = ctx.UserText ?? "",
            ConversationId = ctx.ThreadId,
            MemoryEnabled = true,
            ActiveProfileId = "butler",
            IsColdGreeting = true,
        });

        var ctx = WithSystemPrompt("base");
        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("butler", captured!.ActiveProfileId);
        Assert.True(captured.IsColdGreeting);
    }

    private static TurnContext WithSystemPrompt(string prompt) => new()
    {
        ThreadId = "t1",
        MessageId = "m1",
        UserText = "hi",
        LlmMessages = new[] { ChatMessage.System(prompt), ChatMessage.User("hi") },
    };

    private sealed class StubProvider : IMemoryContextProvider
    {
        private readonly MemoryContextResult _result;
        public StubProvider(MemoryContextResult result) { _result = result; }

        public Task<MemoryContextResult> GetContextAsync(MemoryContextRequest r, CancellationToken c = default)
            => Task.FromResult(_result);
    }

    private sealed class ThrowingProvider : IMemoryContextProvider
    {
        private readonly Exception _ex;
        public ThrowingProvider(Exception ex) { _ex = ex; }

        public Task<MemoryContextResult> GetContextAsync(MemoryContextRequest r, CancellationToken c = default)
            => Task.FromException<MemoryContextResult>(_ex);
    }

    private sealed class RecordingProvider : IMemoryContextProvider
    {
        private readonly Func<MemoryContextRequest, MemoryContextResult> _impl;
        public RecordingProvider(Func<MemoryContextRequest, MemoryContextResult> impl) { _impl = impl; }

        public Task<MemoryContextResult> GetContextAsync(MemoryContextRequest r, CancellationToken c = default)
            => Task.FromResult(_impl(r));
    }
}
