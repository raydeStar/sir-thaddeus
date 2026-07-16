using System.Diagnostics;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Opt-in monotonic timing scope for routing investigations. It emits only
/// identifiers, stage names, and durations; prompts and memory contents are
/// deliberately excluded. Enable with ST_ROUTING_LATENCY_TRACE=1.
/// </summary>
internal static class RoutingLatencyTrace
{
    private static readonly AsyncLocal<TraceState?> CurrentState = new();

    internal sealed class TraceState
    {
        public required string CorrelationId { get; init; }
        public required string ThreadId { get; init; }
        public long StartedTimestamp { get; init; }
        public string? UserMessageId { get; set; }
        public string? AssistantMessageId { get; set; }
        public bool LocalWikiEvidencePacketActivated { get; set; }
    }

    public static bool IsEnabled => IsTruthy(Environment.GetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE"));
    public static TraceState? Current => CurrentState.Value;

    public static TraceState? Start(string threadId, ILogger logger)
    {
        if (!IsEnabled)
            return null;

        var state = new TraceState
        {
            CorrelationId = "lat_" + Guid.NewGuid().ToString("N")[..12],
            ThreadId = threadId,
            StartedTimestamp = Stopwatch.GetTimestamp()
        };
        Mark(logger, state, "http_request_receipt");
        return state;
    }

    public static IDisposable Activate(TraceState? state)
    {
        if (state is null)
            return NoopDisposable.Instance;

        var previous = CurrentState.Value;
        CurrentState.Value = state;
        var activity = new System.Diagnostics.Activity("sir-thaddeus.routing-latency");
        activity.SetBaggage("routing_correlation_id", state.CorrelationId);
        activity.SetBaggage("thread_id", state.ThreadId);
        if (!string.IsNullOrWhiteSpace(state.UserMessageId))
            activity.SetBaggage("user_message_id", state.UserMessageId);
        if (!string.IsNullOrWhiteSpace(state.AssistantMessageId))
            activity.SetBaggage("turn_id", state.AssistantMessageId);
        activity.Start();
        return new Activation(activity, previous);
    }

    public static void BindAssistantMessage(TraceState? state, string messageId)
    {
        if (state is null)
            return;

        state.AssistantMessageId = messageId;
        System.Diagnostics.Activity.Current?.SetBaggage("turn_id", messageId);
    }

    public static void Mark(ILogger logger, TraceState? state, string stage, double? durationMs = null)
    {
        if (state is null)
            return;

        var elapsedMs = Stopwatch.GetElapsedTime(state.StartedTimestamp).TotalMilliseconds;
        logger.LogInformation(
            "routing.latency correlationId={CorrelationId} threadId={ThreadId} userMessageId={UserMessageId} turnId={TurnId} stage={Stage} elapsedMs={ElapsedMs} durationMs={DurationMs}",
            state.CorrelationId,
            state.ThreadId,
            state.UserMessageId ?? string.Empty,
            state.AssistantMessageId ?? string.Empty,
            stage,
            elapsedMs,
            durationMs);
    }

    private static bool IsTruthy(string? raw) =>
        string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase);

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class Activation(System.Diagnostics.Activity activity, TraceState? previous) : IDisposable
    {
        public void Dispose()
        {
            activity.Stop();
            CurrentState.Value = previous;
        }
    }
}
