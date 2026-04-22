using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Pipeline;

namespace SirThaddeus.Tests.Agent.Pipeline;

public class DialogueStateAccessorTests
{
    // ── NullDialogueStateAccessor ────────────────────────────────────

    [Fact]
    public void Null_accessor_returns_fresh_empty_state_and_ignores_writes()
    {
        // The null accessor is what runtimes pass when they don't want
        // dialogue state persistence (tests, minimal harnesses). Get
        // must always return a usable default; Update/Reset must be
        // silent no-ops.
        var sut = NullDialogueStateAccessor.Instance;

        var s1 = sut.Get("t1");
        sut.Update("t1", s1 with { Topic = "weather" });
        var s2 = sut.Get("t1");

        Assert.Equal(string.Empty, s1.Topic);
        Assert.Equal(string.Empty, s2.Topic); // Write was dropped
        Assert.NotSame(s1, s2);               // Fresh instance each call
    }

    [Fact]
    public void Null_accessor_is_a_shared_singleton()
    {
        Assert.Same(NullDialogueStateAccessor.Instance, NullDialogueStateAccessor.Instance);
    }

    // ── SingletonDialogueStateAccessor (CLI model) ──────────────────

    [Fact]
    public void Singleton_accessor_ignores_conversation_id_and_proxies_to_the_store()
    {
        // The CLI runs with one active conversation at a time and uses
        // the legacy DialogueStateStore. The singleton adapter must
        // expose the same state regardless of conversation id.
        var store = new DialogueStateStore();
        var sut = new SingletonDialogueStateAccessor(store);

        sut.Update("t1", new DialogueState { Topic = "paris trip" });

        // Different conversation id, same underlying state.
        Assert.Equal("paris trip", sut.Get("t2").Topic);
        Assert.Equal("paris trip", store.Get().Topic);
    }

    [Fact]
    public void Singleton_accessor_Reset_clears_the_store()
    {
        var store = new DialogueStateStore();
        var sut = new SingletonDialogueStateAccessor(store);
        sut.Update("t1", new DialogueState { Topic = "paris" });

        sut.Reset("anything");

        Assert.Equal(string.Empty, store.Get().Topic);
    }

    [Fact]
    public void Singleton_accessor_constructor_rejects_null_store()
    {
        Assert.Throws<ArgumentNullException>(() => new SingletonDialogueStateAccessor(null!));
    }

    // ── ThreadScopedDialogueStateAccessor (UI model) ────────────────

    [Fact]
    public void Thread_scoped_accessor_keeps_different_conversations_separate()
    {
        // The desktop UI can have multiple chat threads in flight. Each
        // thread must have its own topic / location / time-scope state
        // so switching back and forth doesn't leak context.
        var sut = new ThreadScopedDialogueStateAccessor();

        sut.Update("thread-work", new DialogueState { Topic = "quarterly report", LocationName = "Olympia, WA" });
        sut.Update("thread-personal", new DialogueState { Topic = "dinner ideas" });

        Assert.Equal("quarterly report", sut.Get("thread-work").Topic);
        Assert.Equal("Olympia, WA", sut.Get("thread-work").LocationName);
        Assert.Equal("dinner ideas", sut.Get("thread-personal").Topic);
        Assert.Null(sut.Get("thread-personal").LocationName);
    }

    [Fact]
    public void Thread_scoped_accessor_Get_returns_empty_state_for_unknown_conversation()
    {
        // Unknown thread = fresh conversation. Must return a valid
        // empty state rather than throwing or returning null.
        var sut = new ThreadScopedDialogueStateAccessor();

        var state = sut.Get("never-seen-before");

        Assert.Equal(string.Empty, state.Topic);
        Assert.Null(state.LocationName);
        Assert.False(state.ContextLocked);
    }

    [Fact]
    public void Thread_scoped_accessor_Reset_clears_only_the_named_thread()
    {
        // Resetting one thread must not affect siblings — common in the
        // UI when the user clicks "start new chat" on a specific thread.
        var sut = new ThreadScopedDialogueStateAccessor();
        sut.Update("a", new DialogueState { Topic = "alpha" });
        sut.Update("b", new DialogueState { Topic = "beta" });

        sut.Reset("a");

        Assert.Equal(string.Empty, sut.Get("a").Topic);
        Assert.Equal("beta", sut.Get("b").Topic);
    }

    [Fact]
    public void Thread_scoped_accessor_rejects_blank_conversation_ids()
    {
        var sut = new ThreadScopedDialogueStateAccessor();
        Assert.Throws<ArgumentException>(() => sut.Get(""));
        Assert.Throws<ArgumentException>(() => sut.Update("", new DialogueState()));
        Assert.Throws<ArgumentException>(() => sut.Reset(""));
    }

    [Fact]
    public void Thread_scoped_accessor_Update_rejects_null_state()
    {
        var sut = new ThreadScopedDialogueStateAccessor();
        Assert.Throws<ArgumentNullException>(() => sut.Update("t", null!));
    }
}
