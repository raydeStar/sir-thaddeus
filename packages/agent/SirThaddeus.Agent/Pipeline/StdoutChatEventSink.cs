using System.Globalization;

namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// <see cref="IChatEventSink"/> implementation that writes human-readable
/// lines to a <see cref="TextWriter"/>. Designed for CLI / headless
/// runtimes so the "thinking cadence" (tool starting → tool done →
/// next step) is visible in terminal runs and scripted pipelines, not
/// just in the desktop UI.
///
/// <para>Line format is deliberately grep-friendly:</para>
/// <code>
/// [turn.start] thread=t_abc msg=msg_01
/// [tool.started] web_search (Web) args={"q":"..."}
/// [tool.ok      420ms] web_search
/// [footman Chat 0.95 heuristic_chat kept=5/12 180ms]
/// [turn.delta] the weather in Olympia…
/// [turn.complete] thread=t_abc msg=msg_01 cancelled=False
/// </code>
///
/// <para>Best-effort by contract. If the underlying writer throws
/// (closed stream, pipe broken), the exception is swallowed. Sinks must
/// never derail a turn.</para>
/// </summary>
public sealed class StdoutChatEventSink : IChatEventSink
{
    private readonly TextWriter _writer;
    private readonly object _writeGate = new();
    private readonly bool _showDeltas;

    /// <param name="writer">Destination stream. Defaults to
    /// <see cref="Console.Out"/>.</param>
    /// <param name="showDeltas">If false, per-token delta events are
    /// suppressed (the final <c>turn.complete</c> still prints the full
    /// text). Keep false for scripted / log-scraping use so you don't
    /// get flooded; keep true for an interactive "watch it think" feel.</param>
    public StdoutChatEventSink(TextWriter? writer = null, bool showDeltas = false)
    {
        _writer = writer ?? Console.Out;
        _showDeltas = showDeltas;
    }

    public Task TurnStartedAsync(string threadId, string messageId, CancellationToken cancellationToken = default)
    {
        WriteLine($"[turn.start] thread={threadId} msg={messageId}");
        return Task.CompletedTask;
    }

    public Task TurnDeltaAsync(string threadId, string messageId, string text, CancellationToken cancellationToken = default)
    {
        if (_showDeltas)
            WriteLine($"[turn.delta] {text}");
        return Task.CompletedTask;
    }

    public Task TurnCompleteAsync(string threadId, string messageId, string finalText, bool cancelled, CancellationToken cancellationToken = default)
    {
        WriteLine($"[turn.complete] thread={threadId} msg={messageId} cancelled={cancelled} text_len={finalText.Length}");
        return Task.CompletedTask;
    }

    public Task ToolStartedAsync(string activityId, string threadId, string messageId, string tool, string group, string argsPreview, CancellationToken cancellationToken = default)
    {
        WriteLine($"[tool.started] {tool} ({group}) args={argsPreview}");
        return Task.CompletedTask;
    }

    public Task ToolCompletedAsync(string activityId, string threadId, string messageId, string tool, bool ok, long durationMs, string? resultSnippet, string? error, CancellationToken cancellationToken = default)
    {
        var status = ok ? "ok" : "fail";
        var tail = ok
            ? (string.IsNullOrEmpty(resultSnippet) ? "" : $" snippet={resultSnippet}")
            : $" error={error ?? ""}";
        WriteLine($"[tool.{status} {durationMs.ToString(CultureInfo.InvariantCulture)}ms] {tool}{tail}");
        return Task.CompletedTask;
    }

    public Task FootmanDecisionAsync(
        string threadId,
        string messageId,
        string nextState,
        double confidence,
        bool abstain,
        string reasonCode,
        int toolsKept,
        int toolsTotal,
        long elapsedMs,
        CancellationToken cancellationToken = default)
    {
        var abstainTag = abstain ? " abstain" : "";
        WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[footman {0} {1:F2} {2} kept={3}/{4} {5}ms{6}]",
            nextState, confidence, reasonCode, toolsKept, toolsTotal, elapsedMs, abstainTag));
        return Task.CompletedTask;
    }

    private void WriteLine(string line)
    {
        // Lock so tool-start/tool-complete events interleaving with
        // deltas from another turn don't produce spliced lines on stdout.
        lock (_writeGate)
        {
            try
            {
                _writer.WriteLine(line);
                _writer.Flush();
            }
            catch
            {
                // Best-effort — a broken pipe / closed stream on stdout
                // must not derail a turn. Runtime owns the writer.
            }
        }
    }
}
