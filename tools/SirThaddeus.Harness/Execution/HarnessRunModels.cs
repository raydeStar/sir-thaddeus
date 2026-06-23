using SirThaddeus.Agent;
using SirThaddeus.Harness.Artifacts;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Tracing;

namespace SirThaddeus.Harness.Execution;

public sealed record SingleRunResult
{
    public required AgentResponse Response { get; init; }
    public required ScoreCard Score { get; init; }
    public required CursorJudgeResult? JudgeResult { get; init; }
    public required ArtifactPaths ArtifactPaths { get; init; }
    public required IReadOnlyList<TraceStep> Steps { get; init; }
    public required string? ModelName { get; init; }
    internal HarnessTiming Timing { get; init; } = HarnessTiming.Empty;
}

public sealed record RecordedToolTurn
{
    public int Index { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public string ArgumentsJson { get; init; } = string.Empty;
    public string ResultText { get; init; } = string.Empty;
    public bool Success { get; init; }
}
