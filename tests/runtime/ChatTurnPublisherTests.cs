using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Events;
using Thaddeus.SharedTypes;
using Xunit;

namespace Thaddeus.Runtime.Tests;

public class ChatTurnPublisherTests
{
    private static (EventBus bus, ChatTurnPublisher publisher) NewSut()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        return (bus, new ChatTurnPublisher(bus));
    }

    [Fact]
    public async Task PublishStart_emits_start_event_with_thread_and_message_ids()
    {
        var (bus, sut) = NewSut();
        var captured = new List<RuntimeEvent<object?>>();
        using var sub = bus.Subscribe((evt, _) => { captured.Add(evt); return Task.CompletedTask; });

        await sut.PublishStartAsync("th_1", "msg_a");

        var evt = Assert.Single(captured);
        Assert.Equal(ChatTurnEvents.Start, evt.Type);
        var payload = Assert.IsType<ChatTurnStart>(evt.Payload);
        Assert.Equal("th_1", payload.ThreadId);
        Assert.Equal("msg_a", payload.MessageId);
        Assert.Equal("msg_a", evt.CorrelationId);
    }

    [Fact]
    public async Task PublishDelta_carries_streamed_text_chunk()
    {
        var (bus, sut) = NewSut();
        var captured = new List<RuntimeEvent<object?>>();
        using var sub = bus.Subscribe((evt, _) => { captured.Add(evt); return Task.CompletedTask; });

        await sut.PublishDeltaAsync("th_1", "msg_a", "hello ");
        await sut.PublishDeltaAsync("th_1", "msg_a", "world");

        Assert.Equal(2, captured.Count);
        Assert.All(captured, e => Assert.Equal(ChatTurnEvents.Delta, e.Type));
        Assert.Equal("hello ", ((ChatTurnDelta)captured[0].Payload!).Text);
        Assert.Equal("world", ((ChatTurnDelta)captured[1].Payload!).Text);
    }

    [Fact]
    public async Task PublishComplete_marks_cancelled_flag()
    {
        var (bus, sut) = NewSut();
        var captured = new List<RuntimeEvent<object?>>();
        using var sub = bus.Subscribe((evt, _) => { captured.Add(evt); return Task.CompletedTask; });

        await sut.PublishCompleteAsync("th_1", "msg_a", "partial", cancelled: true);

        var evt = Assert.Single(captured);
        Assert.Equal(ChatTurnEvents.Complete, evt.Type);
        var payload = Assert.IsType<ChatTurnComplete>(evt.Payload);
        Assert.True(payload.Cancelled);
        Assert.Equal("partial", payload.FinalText);
    }
}
