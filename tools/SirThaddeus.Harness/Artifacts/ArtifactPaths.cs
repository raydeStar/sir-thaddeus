namespace SirThaddeus.Harness.Artifacts;

public sealed record ArtifactPaths
{
    public required string RootDirectory { get; init; }
    public required string InputJsonPath { get; init; }
    public required string StepsJsonlPath { get; init; }
    public string ObservationsJsonPath { get; init; } = "";
    public required string FinalTextPath { get; init; }
    public required string ScoreJsonPath { get; init; }
    public required string DiffMarkdownPath { get; init; }
    public required string JudgePacketPath { get; init; }
    public required string JudgeResultPath { get; init; }
}
