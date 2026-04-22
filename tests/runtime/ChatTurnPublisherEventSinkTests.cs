using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Events;
using Thaddeus.SharedTypes;
using Xunit;

namespace Thaddeus.Runtime.Tests;

public class ChatTurnPublisherEventSinkTests
{
    private static (EventBus bus, ChatTurnPublisherEventSink sink) NewSut()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var publisher = new ChatTurnPublisher(bus);
        var sink = new ChatTurnPublisherEventSink(
            publisher,
            NullLogger<ChatTurnPublisherEventSink>.Instance);
        return (bus, sink);
    }

    [Fact]
    public async Task TurnStartedAsync_forwards_to_publisher_as_start_event()
    {
        var (bus, sink) = NewSut();
        var captured = new List<RuntimeEvent<object?>>();
        using var sub = bus.Subscribe((evt, _) => { captured.Add(evt); return Task.CompletedTask; });

        await sink.TurnStartedAsync("t1", "m1");

        var evt = Assert.Single(captured);
        Assert.Equal(ChatTurnEvents.Start, evt.Type);
        var payload = Assert.IsType<ChatTurnStart>(evt.Payload);
        Assert.Equal("t1", payload.ThreadId);
        Assert.Equal("m1", payload.MessageId);
    }

    [Fact]
    public async Task TurnDeltaAsync_forwards_text_chunk_verbatim()
    {
        var (bus, sink) = NewSut();
        var captured = new List<RuntimeEvent<object?>>();
        using var sub = bus.Subscribe((evt, _) => { captured.Add(evt); return Task.CompletedTask; });

        await sink.TurnDeltaAsync("t1", "m1", "hello ");
        await sink.TurnDeltaAsync("t1", "m1", "world");

        Assert.Equal(2, captured.Count);
        Assert.Equal(ChatTurnEvents.Delta, captured[0].Type);
        Assert.Equal("hello ", ((ChatTurnDelta)captured[0].Payload!).Text);
        Assert.Equal("world", ((ChatTurnDelta)captured[1].Payload!).Text);
    }

    [Fact]
    public async Task TurnCompleteAsync_propagates_cancelled_flag()
    {
        var (bus, sink) = NewSut();
        var captured = new List<RuntimeEvent<object?>>();
        using var sub = bus.Subscribe((evt, _) => { captured.Add(evt); return Task.CompletedTask; });

        await sink.TurnCompleteAsync("t1", "m1", "partial", cancelled: true);

        var evt = Assert.Single(captured);
        Assert.Equal(ChatTurnEvents.Complete, evt.Type);
        var payload = Assert.IsType<ChatTurnComplete>(evt.Payload);
        Assert.True(payload.Cancelled);
        Assert.Equal("partial", payload.FinalText);
    }

    [Fact]
    public async Task ToolStartedAsync_and_ToolCompletedAsync_fire_matching_events()
    {
        var (bus, sink) = NewSut();
        var captured = new List<RuntimeEvent<object?>>();
        using var sub = bus.Subscribe((evt, _) => { captured.Add(evt); return Task.CompletedTask; });

        await sink.ToolStartedAsync("a1", "t1", "m1", "web_search", "Web", "{\"q\":\"hi\"}");
        await sink.ToolCompletedAsync("a1", "t1", "m1", "web_search", ok: true, durationMs: 42, resultSnippet: "ok", error: null);

        Assert.Equal(2, captured.Count);

        var started = Assert.IsType<ChatToolStarted>(captured[0].Payload);
        Assert.Equal("a1", started.ActivityId);
        Assert.Equal("web_search", started.Tool);
        Assert.Equal("Web", started.Group);

        var completed = Assert.IsType<ChatToolCompleted>(captured[1].Payload);
        Assert.Equal("a1", completed.ActivityId);
        Assert.True(completed.Ok);
        Assert.Equal(42, completed.DurationMs);
    }

    [Fact]
    public async Task FootmanDecisionAsync_carries_kept_total_and_reason()
    {
        var (bus, sink) = NewSut();
        var captured = new List<RuntimeEvent<object?>>();
        using var sub = bus.Subscribe((evt, _) => { captured.Add(evt); return Task.CompletedTask; });

        await sink.FootmanDecisionAsync(
            threadId: "t1",
            messageId: "m1",
            nextState: "Chat",
            confidence: 0.92,
            abstain: false,
            reasonCode: "heuristic_chat",
            toolsKept: 9,
            toolsTotal: 45,
            elapsedMs: 120);

        var evt = Assert.Single(captured);
        Assert.Equal(ChatTurnEvents.FootmanDecision, evt.Type);
        var payload = Assert.IsType<ChatFootmanDecision>(evt.Payload);
        Assert.Equal("Chat", payload.NextState);
        Assert.Equal(0.92, payload.Confidence);
        Assert.False(payload.Abstain);
        Assert.Equal("heuristic_chat", payload.ReasonCode);
        Assert.Equal(9, payload.ToolsKept);
        Assert.Equal(45, payload.ToolsTotal);
        Assert.Equal(120, payload.ElapsedMs);
    }

    [Fact]
    public async Task Adapter_constructor_rejects_null_dependencies()
    {
        // Ports are best-effort at call time but must be built with real
        // collaborators — a null publisher or logger is always a bug and
        // deserves an eager failure.
        Assert.Throws<ArgumentNullException>(() =>
            new ChatTurnPublisherEventSink(null!, NullLogger<ChatTurnPublisherEventSink>.Instance));

        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var publisher = new ChatTurnPublisher(bus);
        Assert.Throws<ArgumentNullException>(() =>
            new ChatTurnPublisherEventSink(publisher, null!));

        // Keep the task non-async compiler-happy.
        await Task.CompletedTask;
    }
}
