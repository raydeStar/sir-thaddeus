using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Events;
using Thaddeus.SharedTypes;
using Xunit;

namespace Thaddeus.Runtime.Tests;

public class StubAssistantTests : IDisposable
{
    private readonly string _root;

    public StubAssistantTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "thaddeus-assistant-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }

    private (JsonFileThreadStore store, EventBus bus, StubAssistant assistant, List<RuntimeEvent<object?>> captured)
        NewSut()
    {
        var store = new JsonFileThreadStore(_root, NullLogger<JsonFileThreadStore>.Instance);
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var publisher = new ChatTurnPublisher(bus);
        var assistant = new StubAssistant(store, publisher, NullLogger<StubAssistant>.Instance)
        {
            DeltaDelay = TimeSpan.Zero,
        };
        var captured = new List<RuntimeEvent<object?>>();
        bus.Subscribe((evt, _) => { captured.Add(evt); return Task.CompletedTask; });
        return (store, bus, assistant, captured);
    }

    [Fact]
    public async Task RespondAsync_emits_start_then_deltas_then_complete()
    {
        var (store, _, assistant, captured) = NewSut();
        var thread = await store.CreateAsync("t", CancellationToken.None);

        var msg = await assistant.RespondAsync(thread.Id, "hello there", CancellationToken.None);

        Assert.NotEmpty(captured);
        Assert.Equal(ChatTurnEvents.Start, captured[0].Type);
        Assert.Equal(ChatTurnEvents.Complete, captured[^1].Type);
        Assert.Contains(captured.Skip(1).Take(captured.Count - 2), e => e.Type == ChatTurnEvents.Delta);

        var assembled = string.Concat(captured
            .Where(e => e.Type == ChatTurnEvents.Delta)
            .Select(e => ((ChatTurnDelta)e.Payload!).Text));
        Assert.Equal(msg.Text, assembled);
    }

    [Fact]
    public async Task RespondAsync_persists_assistant_message_to_thread()
    {
        var (store, _, assistant, _) = NewSut();
        var thread = await store.CreateAsync("t", CancellationToken.None);

        var msg = await assistant.RespondAsync(thread.Id, "ping", CancellationToken.None);

        var refreshed = await store.GetAsync(thread.Id, CancellationToken.None);
        Assert.NotNull(refreshed);
        Assert.Single(refreshed!.Messages);
        Assert.Equal(ChatRole.Assistant, refreshed.Messages[0].Role);
        Assert.Equal(msg.Id, refreshed.Messages[0].Id);
        Assert.Contains("ping", refreshed.Messages[0].Text);
    }

    [Fact]
    public async Task RespondAsync_marks_complete_event_cancelled_when_token_fires()
    {
        var (store, _, assistant, captured) = NewSut();
        // Slow the deltas down so we can reliably cancel mid-stream.
        var slow = new StubAssistant(store, new ChatTurnPublisher(new EventBus(NullLogger<EventBus>.Instance)),
            NullLogger<StubAssistant>.Instance);
        // Re-wire with our captured bus so we observe events.
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var capturedSlow = new List<RuntimeEvent<object?>>();
        bus.Subscribe((evt, _) => { capturedSlow.Add(evt); return Task.CompletedTask; });
        var slowAssistant = new StubAssistant(store, new ChatTurnPublisher(bus), NullLogger<StubAssistant>.Instance)
        {
            DeltaDelay = TimeSpan.FromMilliseconds(50),
        };
        var thread = await store.CreateAsync("t", CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await slowAssistant.RespondAsync(thread.Id, "this should be cancelled mid-flight", cts.Token);

        var complete = capturedSlow.Single(e => e.Type == ChatTurnEvents.Complete);
        Assert.True(((ChatTurnComplete)complete.Payload!).Cancelled);
    }

    [Fact]
    public async Task BuildReply_quotes_user_text()
    {
        var (store, _, assistant, _) = NewSut();
        var thread = await store.CreateAsync("t", CancellationToken.None);

        var msg = await assistant.RespondAsync(thread.Id, "what is up", CancellationToken.None);

        Assert.Contains("\"what is up\"", msg.Text);
        Assert.Contains("stubbed reply", msg.Text);
    }
}
