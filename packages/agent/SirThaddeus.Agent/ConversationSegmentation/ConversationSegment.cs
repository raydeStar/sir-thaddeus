namespace SirThaddeus.Agent.ConversationSegmentation;

/// <summary>
/// A contiguous span from the original user message.
/// </summary>
public sealed record ConversationSegment
{
    public required string SegmentId { get; init; }
    public required string Text { get; init; }
    public required int Order { get; init; }
    public required int StartIndex { get; init; }
    public required int EndIndex { get; init; }
    public required bool IsActionable { get; init; }
    public required double Confidence { get; init; }
}

/// <summary>
/// Deterministic segmentation output for one user message.
/// </summary>
public sealed record ConversationSegmentationResult
{
    public required string OriginalMessage { get; init; }
    public IReadOnlyList<ConversationSegment> Segments { get; init; } = [];
    public bool HasActionable { get; init; }
    public bool HighConfidence { get; init; }
    public string ConfidenceReason { get; init; } = "unknown";
}

