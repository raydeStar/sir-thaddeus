using SirThaddeus.Agent;
using SirThaddeus.Agent.Search.DeepDive;

namespace SirThaddeus.Agent.ConversationSegmentation;

public sealed record SegmentExecutionResult
{
    public required string SegmentId { get; init; }
    public required string SegmentText { get; init; }
    public required string Intent { get; init; }
    public required bool Success { get; init; }
    public required string ResponseText { get; init; }
    public required bool UsedTools { get; init; }
    public required int ToolCallCount { get; init; }
    public IReadOnlyList<ToolCallRecord> ToolCallsMade { get; init; } = [];
    public int LlmRoundTrips { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// Structured briefing payload produced by this segment, if any.
    /// Propagated upward so the multi-intent response can carry it to the UI.
    /// </summary>
    public DeepDiveBriefing? DeepDiveBriefing { get; init; }
}

public sealed record SegmentExecutionPlan
{
    public IReadOnlyList<SegmentExecutionResult> Executed { get; init; } = [];
    public IReadOnlyList<ConversationSegment> Deferred { get; init; } = [];
}

