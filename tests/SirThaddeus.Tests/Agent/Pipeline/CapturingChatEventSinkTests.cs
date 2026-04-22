using SirThaddeus.Agent.Pipeline;

namespace SirThaddeus.Tests.Agent.Pipeline;

public class CapturingChatEventSinkTests
{
    [Fact]
    public async Task Records_each_event_kind_in_order()
    {
        // Integration-style check: drive a realistic sequence (start →
        // footman → tool pair → complete) and verify it lands verbatim.
        var sink = new CapturingChatEventSink();

        await sink.TurnStartedAsync("t1", "m1");
        await sink.FootmanDecisionAsync("t1", "m1", "Chat", 0.92, false, "heuristic_chat", 5, 12, 180);
        await sink.ToolStartedAsync("a1", "t1", "m1", "web_search", "Web", "{\"q\":\"x\"}");
        await sink.ToolCompletedAsync("a1", "t1", "m1", "web_search", ok: true, 420, "snippet", null);
        await sink.TurnCompleteAsync("t1", "m1", "final", cancelled: false);

        var events = sink.Snapshot();
        Assert.Equal(new[] { "turn.start", "footman.decision", "tool.started", "tool.completed", "turn.complete" },
            events.Select(e => e.Kind));
    }

    [Fact]
    public async Task SnapshotOfKind_filters_to_the_requested_event_kind()
    {
        // Common assertion pattern: "the turn emitted exactly N tool
        // calls". Filtering inline keeps test code readable.
        var sink = new CapturingChatEventSink();

        await sink.TurnStartedAsync("t", "m");
        await sink.ToolStartedAsync("a1", "t", "m", "web_search", "Web", "{}");
        await sink.ToolStartedAsync("a2", "t", "m", "weather_geocode", "Web", "{}");
        await sink.TurnCompleteAsync("t", "m", "done", false);

        var tools = sink.SnapshotOfKind("tool.started");
        Assert.Equal(2, tools.Count);
        Assert.Equal("web_search", tools[0].Tool);
        Assert.Equal("weather_geocode", tools[1].Tool);
    }

    [Fact]
    public async Task Populates_only_fields_relevant_to_the_event_kind()
    {
        // Event record is a union over all event shapes. Unused fields
        // must stay null so consumers can match safely with "NotNull"
        // assertions on the fields they care about.
        var sink = new CapturingChatEventSink();

        await sink.TurnStartedAsync("t", "m");
        await sink.ToolCompletedAsync("a", "t", "m", "web_search", ok: false, 12, null, "timeout");

        var start = sink.Snapshot()[0];
        Assert.Null(start.Tool);
        Assert.Null(start.Ok);
        Assert.Null(start.Error);

        var completed = sink.Snapshot()[1];
        Assert.Equal("web_search", completed.Tool);
        Assert.False(completed.Ok);
        Assert.Equal("timeout", completed.Error);
        // Footman-only fields stay null on tool events.
        Assert.Null(completed.NextState);
        Assert.Null(completed.Confidence);
    }

    [Fact]
    public async Task Clear_drains_the_queue()
    {
        var sink = new CapturingChatEventSink();
        await sink.TurnStartedAsync("t", "m");
        Assert.Single(sink.Snapshot());

        sink.Clear();

        Assert.Empty(sink.Snapshot());
    }

    [Fact]
    public async Task Concurrent_writers_all_land_without_loss()
    {
        // ToolLoopStep may emit completed events from parallel async
        // continuations (model calls, tool calls). The sink must not
        // drop events under concurrent write load.
        var sink = new CapturingChatEventSink();
        var tasks = new List<Task>();
        for (var i = 0; i < 50; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                await sink.ToolStartedAsync(
                    activityId: $"a{index}",
                    threadId: "t",
                    messageId: "m",
                    tool: "x",
                    group: "Web",
                    argsPreview: "{}");
            }));
        }
        await Task.WhenAll(tasks);

        Assert.Equal(50, sink.SnapshotOfKind("tool.started").Count);
    }

    [Fact]
    public async Task Footman_decision_captures_all_fields()
    {
        // The footman event has a wide payload (state, confidence,
        // reason, kept/total, elapsed). All must be round-tripped so
        // harness can assert on any of them.
        var sink = new CapturingChatEventSink();

        await sink.FootmanDecisionAsync(
            threadId: "t",
            messageId: "m",
            nextState: "SearchFact",
            confidence: 0.82,
            abstain: false,
            reasonCode: "fact_lookup",
            toolsKept: 3,
            toolsTotal: 12,
            elapsedMs: 240);

        var evt = Assert.Single(sink.Snapshot());
        Assert.Equal("footman.decision", evt.Kind);
        Assert.Equal("SearchFact", evt.NextState);
        Assert.Equal(0.82, evt.Confidence);
        Assert.False(evt.Abstain);
        Assert.Equal("fact_lookup", evt.ReasonCode);
        Assert.Equal(3, evt.ToolsKept);
        Assert.Equal(12, evt.ToolsTotal);
        Assert.Equal(240, evt.DurationMs);
    }
}
