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
    public required HarnessFixture? Fixture { get; init; }
    public required string? ModelName { get; init; }
}
