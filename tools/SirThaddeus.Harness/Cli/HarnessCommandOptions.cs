namespace SirThaddeus.Harness.Cli;

public enum HarnessCommandKind
{
    Run,
    Record,
    Replay,
    Smoke
}

public enum HarnessExecutionMode
{
    Live,
    Replay,
    Stub
}

public enum HarnessJudgeMode
{
    None,
    Cursor,
    Model
}

public sealed record HarnessCommandOptions
{
    public HarnessCommandKind Command { get; init; } = HarnessCommandKind.Run;
    public HarnessExecutionMode Mode { get; init; } = HarnessExecutionMode.Live;
    public bool ModeExplicitlySet { get; init; }
    public HarnessJudgeMode JudgeMode { get; init; } = HarnessJudgeMode.None;

    public string SuiteName { get; init; } = "";
    public bool ShowHelp { get; init; }

    public int MaxIterations { get; init; } = 1;
    public double? MinScoreOverride { get; init; }

    public bool AllowWorkspaceEdits { get; init; }
    public int PatchBudgetFiles { get; init; } = 3;
    public int PatchBudgetLines { get; init; } = 200;

    public int JudgeTimeoutMs { get; init; } = 60_000;
    public bool JudgeRequired { get; init; } = true;

    public string SuitesRoot { get; init; } =
        Path.Combine("tools", "SirThaddeus.Harness", "Suites");

    public string FixturesRoot { get; init; } =
        Path.Combine("tools", "SirThaddeus.Harness", "fixtures");

    public string ArtifactsRoot { get; init; } =
        Path.Combine("artifacts", "harness");
}

public sealed class CommandLineException : Exception
{
    public CommandLineException(string message) : base(message) { }
}
