namespace SirThaddeus.Agent.ConversationSegmentation;

/// <summary>
/// Async-local context for correlating MCP tool calls to a segment.
/// </summary>
public static class SegmentExecutionContext
{
    private static readonly AsyncLocal<string?> SegmentIdSlot = new();

    public static string? CurrentSegmentId => SegmentIdSlot.Value;

    public static IDisposable Enter(string? segmentId)
        => new Scope(segmentId);

    private sealed class Scope : IDisposable
    {
        private readonly string? _prior;
        private bool _disposed;

        public Scope(string? segmentId)
        {
            _prior = SegmentIdSlot.Value;
            SegmentIdSlot.Value = string.IsNullOrWhiteSpace(segmentId)
                ? _prior
                : segmentId;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            SegmentIdSlot.Value = _prior;
        }
    }
}

