using SirThaddeus.Agent.Pipeline;

namespace SirThaddeus.Tests.Agent.Pipeline;

public class NullChatEventSinkTests
{
    [Fact]
    public async Task Every_method_completes_without_throwing()
    {
        var sink = NullChatEventSink.Instance;

        // The null sink exists precisely so tests and headless setups can
        // pass a non-null IChatEventSink without wiring transport. Each
        // method must return a completed task synchronously and without
        // side effects.
        await sink.TurnStartedAsync("t", "m");
        await sink.TurnDeltaAsync("t", "m", "chunk");
        await sink.TurnCompleteAsync("t", "m", "final", cancelled: false);
        await sink.ToolStartedAsync("a", "t", "m", "web_search", "Web", "{}");
        await sink.ToolCompletedAsync("a", "t", "m", "web_search", ok: true, durationMs: 12, resultSnippet: "ok", error: null);
        await sink.FootmanDecisionAsync("t", "m", "Chat", 0.9, false, "heuristic_chat", 5, 12, 180);
    }

    [Fact]
    public void Instance_is_a_shared_singleton()
    {
        // A stateless sink should surface as one shared instance so callers
        // don't allocate per use. Verify both references are the same
        // object, not just equal.
        Assert.Same(NullChatEventSink.Instance, NullChatEventSink.Instance);
    }

    [Fact]
    public async Task Methods_honour_already_cancelled_tokens_silently()
    {
        var sink = NullChatEventSink.Instance;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Null sink is a best-effort drop target — it must not throw on a
        // pre-cancelled token. Call sites routinely pass request-scoped
        // tokens that may be cancelled by the time late events arrive.
        await sink.TurnStartedAsync("t", "m", cts.Token);
        await sink.ToolCompletedAsync("a", "t", "m", "x", true, 1, null, null, cts.Token);
    }
}
