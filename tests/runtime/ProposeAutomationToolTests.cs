using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Events;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tests;

public sealed class ProposeAutomationToolTests
{
    [Fact]
    public async Task HandleAsync_infers_daily_cron_when_model_omits_schedule()
    {
        var bus = new RecordingEventBus();
        var publisher = new ChatTurnPublisher(bus);

        var (summary, error) = await ProposeAutomationTool.HandleAsync(
            """
            {
              "name": "Daily Nintendo Switch Stock Check",
              "steps": ["Search Amazon for Nintendo Switch 2 stock"]
            }
            """,
            "th_1",
            "msg_1",
            "prop_1",
            publisher,
            "Create an automation that checks Amazon to see if a Nintendo Switch 2 is in stock every day at 9 AM.",
            CancellationToken.None);

        Assert.Null(error);
        Assert.Contains("confirmation card", summary, StringComparison.OrdinalIgnoreCase);

        var evt = Assert.Single(bus.Events);
        var payload = Assert.IsType<ChatAutomationProposed>(evt.Payload);
        Assert.NotNull(payload.Schedule);
        Assert.Equal("cron", payload.Schedule!.Kind);
        Assert.Equal("0 9 * * *", payload.Schedule.Cron);
        Assert.NotNull(payload.Schedule.NextRunAt);
    }

    [Fact]
    public async Task HandleAsync_keeps_model_schedule_when_present()
    {
        var bus = new RecordingEventBus();
        var publisher = new ChatTurnPublisher(bus);

        var (summary, error) = await ProposeAutomationTool.HandleAsync(
            """
            {
              "name": "Forecast Check",
              "steps": ["Check the weather"],
              "schedule": {
                "kind": "cron",
                "cron": "15 8 * * 1-5"
              }
            }
            """,
            "th_1",
            "msg_1",
            "prop_1",
            publisher,
            "Create an automation to check the weather every weekday at 8:15 AM.",
            CancellationToken.None);

        Assert.Null(error);
        Assert.Contains("confirmation card", summary, StringComparison.OrdinalIgnoreCase);

        var evt = Assert.Single(bus.Events);
        var payload = Assert.IsType<ChatAutomationProposed>(evt.Payload);
        Assert.NotNull(payload.Schedule);
        Assert.Equal("cron", payload.Schedule!.Kind);
        Assert.Equal("15 8 * * 1-5", payload.Schedule.Cron);
    }

        [Fact]
        public async Task HandleAsync_overrides_incompatible_model_cron_for_daily_request()
        {
                var bus = new RecordingEventBus();
                var publisher = new ChatTurnPublisher(bus);

                var (summary, error) = await ProposeAutomationTool.HandleAsync(
                        """
                        {
                            "name": "Switch 2 Check",
                            "steps": ["Search Amazon"],
                            "schedule": {
                                "kind": "cron",
                                "cron": "0 9 * * 1"
                            }
                        }
                        """,
                        "th_1",
                        "msg_1",
                        "prop_1",
                        publisher,
                        "Create an automation that checks Amazon for Nintendo Switch 2 every day at 9 AM.",
                        CancellationToken.None);

                Assert.Null(error);
                Assert.Contains("confirmation card", summary, StringComparison.OrdinalIgnoreCase);

                var evt = Assert.Single(bus.Events);
                var payload = Assert.IsType<ChatAutomationProposed>(evt.Payload);
                Assert.NotNull(payload.Schedule);
                Assert.Equal("cron", payload.Schedule!.Kind);
                Assert.Equal("0 9 * * *", payload.Schedule.Cron);
        }

    private sealed class RecordingEventBus : IEventBus
    {
        public List<RuntimeEvent<object?>> Events { get; } = new();

        public IDisposable Subscribe(Func<RuntimeEvent<object?>, CancellationToken, Task> handler)
        {
            return new NoopSubscription();
        }

        public Task PublishAsync<T>(string type, T payload, string? correlationId = null, CancellationToken ct = default)
        {
            Events.Add(new RuntimeEvent<object?>
            {
                Type = type,
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = correlationId,
                Payload = payload,
            });

            return Task.CompletedTask;
        }

        private sealed class NoopSubscription : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}