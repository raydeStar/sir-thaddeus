namespace SirThaddeus.Harness.Cli;

public enum HarnessCommandKind
{
    Run,
    Stage,
    Inspect
}

public enum HarnessInspectTarget
{
    LatestRun,
    LatestFailure
}

public enum HarnessExecutionMode
{
    Headless
}

public enum HarnessStageTarget
{
    All,
    Preprocess,
    Preflight,
    Classify,
    Query,
    Trace
}

public enum HarnessJudgeMode
{
    None,
    Cursor,
    Model
}

/// <summary>
/// Which runtime host the harness drives. Both share the same agent
/// pipeline; the difference is the chat surface (v1 has /api/chat + SSE,
/// v2 has /api/threads + /ws). Defaults to v1 today; v2 adapter is a
/// work in progress (see HybridRuntimeHostAdapter).
/// </summary>
public enum HarnessHostTarget
{
    HeadlessV1,
    HybridV2
}

public sealed record HarnessCommandOptions
{
    public HarnessCommandKind Command { get; init; } = HarnessCommandKind.Run;
    public HarnessExecutionMode Mode { get; init; } = HarnessExecutionMode.Headless;
    public HarnessJudgeMode JudgeMode { get; init; } = HarnessJudgeMode.None;
    public HarnessHostTarget HostTarget { get; init; } = HarnessHostTarget.HeadlessV1;

    public bool RunAllSuites { get; init; }
    public string SuiteName { get; init; } = "";
    public string TestId { get; init; } = "";
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

    public string ArtifactsRoot { get; init; } =
        Path.Combine("artifacts", "harness");

    // ── Stage command options ─────────────────────────────────────
    public HarnessStageTarget StageTarget { get; init; } = HarnessStageTarget.All;
    public string StageInput { get; init; } = "";
    public string StageAssistantContext { get; init; } = "";
    public string StageFollowUpAnchor { get; init; } = "";
    public string StageUserCity { get; init; } = "";
    public bool StageHasRecentFirstPrinciplesRationale { get; init; }
    public bool StageHasRecentSearchResults { get; init; }

    // ── Inspect command options ───────────────────────────────────
    public HarnessInspectTarget InspectTarget { get; init; } = HarnessInspectTarget.LatestFailure;
    public string InspectRunId { get; init; } = "";
}

public sealed class CommandLineException : Exception
{
    public CommandLineException(string message) : base(message) { }
}
