using SirThaddeus.Agent.Pipeline;

namespace SirThaddeus.Tests.Agent.Pipeline;

public class StdoutChatEventSinkTests
{
    [Fact]
    public async Task Emits_turn_lifecycle_lines()
    {
        var writer = new StringWriter();
        var sink = new StdoutChatEventSink(writer);

        await sink.TurnStartedAsync("t1", "m1");
        await sink.TurnCompleteAsync("t1", "m1", "hello", cancelled: false);

        var lines = ReadLines(writer);
        Assert.Contains("[turn.start]", lines[0]);
        Assert.Contains("thread=t1", lines[0]);
        Assert.Contains("msg=m1", lines[0]);
        Assert.Contains("[turn.complete]", lines[1]);
        Assert.Contains("text_len=5", lines[1]);
        Assert.Contains("cancelled=False", lines[1]);
    }

    [Fact]
    public async Task Tool_pair_prints_start_and_ok_status_with_duration()
    {
        var writer = new StringWriter();
        var sink = new StdoutChatEventSink(writer);

        await sink.ToolStartedAsync("a1", "t1", "m1", "web_search", "Web", "{\"q\":\"cats\"}");
        await sink.ToolCompletedAsync("a1", "t1", "m1", "web_search",
            ok: true, durationMs: 420, resultSnippet: "3 results", error: null);

        var lines = ReadLines(writer);
        Assert.Contains("[tool.started]", lines[0]);
        Assert.Contains("web_search", lines[0]);
        Assert.Contains("(Web)", lines[0]);
        Assert.Contains("[tool.ok 420ms]", lines[1]);
        Assert.Contains("3 results", lines[1]);
    }

    [Fact]
    public async Task Tool_failure_prints_fail_status_with_error()
    {
        var writer = new StringWriter();
        var sink = new StdoutChatEventSink(writer);

        await sink.ToolCompletedAsync("a1", "t1", "m1", "web_search",
            ok: false, durationMs: 12, resultSnippet: null, error: "timeout");

        var line = Assert.Single(ReadLines(writer));
        Assert.Contains("[tool.fail 12ms]", line);
        Assert.Contains("error=timeout", line);
    }

    [Fact]
    public async Task Footman_decision_shows_kept_total_and_reason()
    {
        var writer = new StringWriter();
        var sink = new StdoutChatEventSink(writer);

        await sink.FootmanDecisionAsync("t1", "m1",
            nextState: "Chat", confidence: 0.92, abstain: false,
            reasonCode: "heuristic_chat", toolsKept: 5, toolsTotal: 12, elapsedMs: 180);

        var line = Assert.Single(ReadLines(writer));
        Assert.Contains("[footman Chat 0.92 heuristic_chat kept=5/12 180ms]", line);
    }

    [Fact]
    public async Task Footman_abstain_is_flagged_distinctly()
    {
        // Abstain cases are common (gatekeeper unreachable, low confidence)
        // and should be recognizable at a glance in logs.
        var writer = new StringWriter();
        var sink = new StdoutChatEventSink(writer);

        await sink.FootmanDecisionAsync("t1", "m1",
            nextState: "Fallback", confidence: 0.0, abstain: true,
            reasonCode: "footman_timeout", toolsKept: 12, toolsTotal: 12, elapsedMs: 3000);

        var line = Assert.Single(ReadLines(writer));
        Assert.Contains("abstain", line);
        Assert.Contains("footman_timeout", line);
    }

    [Fact]
    public async Task Deltas_are_suppressed_by_default_to_keep_logs_quiet()
    {
        // Scripted / log-scraping use should not get flooded with
        // per-token chunks. Opt-in via showDeltas=true.
        var writer = new StringWriter();
        var sink = new StdoutChatEventSink(writer, showDeltas: false);

        await sink.TurnDeltaAsync("t1", "m1", "hello");
        await sink.TurnDeltaAsync("t1", "m1", " world");

        Assert.Empty(writer.ToString());
    }

    [Fact]
    public async Task Deltas_are_shown_when_opted_in()
    {
        var writer = new StringWriter();
        var sink = new StdoutChatEventSink(writer, showDeltas: true);

        await sink.TurnDeltaAsync("t1", "m1", "hello");
        await sink.TurnDeltaAsync("t1", "m1", " world");

        var lines = ReadLines(writer);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("hello", lines[0]);
        Assert.EndsWith(" world", lines[1]);
    }

    [Fact]
    public async Task Writer_failure_is_swallowed_so_it_cannot_derail_a_turn()
    {
        // Broken pipe, closed stream, disposed writer — none of these
        // should throw out of the sink.
        var sink = new StdoutChatEventSink(new ThrowingWriter());

        await sink.TurnStartedAsync("t", "m");
        await sink.TurnCompleteAsync("t", "m", "", false);
        await sink.ToolStartedAsync("a", "t", "m", "x", "y", "{}");
        await sink.ToolCompletedAsync("a", "t", "m", "x", true, 1, null, null);
        await sink.FootmanDecisionAsync("t", "m", "Chat", 1.0, false, "h", 1, 1, 1);
    }

    private static string[] ReadLines(StringWriter writer)
    {
        return writer.ToString()
            .Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries);
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) => throw new IOException("pipe closed");
        public override void WriteLine(string? value) => throw new IOException("pipe closed");
        public override void Flush() => throw new IOException("pipe closed");
    }
}
