using System.Globalization;

namespace SirThaddeus.Harness.Cli;

public static class CommandLineParser
{
    public static string HelpText => """
Usage:
    harness run --all [options]
    harness run --suite <name> [options]
    harness run --category <name> [options]
    harness run --test <id> [options]
    harness run --suite <name> --test <id> [options]
    harness inspect latest-run [options]
    harness inspect latest-failure [options]

    harness stage preprocess --input <message>
    harness stage preflight --input <message> [options]
    harness stage classify --input <message>
    harness stage query --input <message>
    harness stage trace --input <message>
    harness stage --all [options]
    harness stage --suite <name> [options]
    harness stage --test <id> [options]
    harness stage --suite <name> --test <id> [options]

Options:
    --all                          Run every headless suite (run) or every stage suite (stage)
    --suite <name>                 Run a suite by directory name under tools/SirThaddeus.Harness/Suites (run) or tools/SirThaddeus.Harness/StageSuites (stage)
    --category <name>              Alias for --suite
    --test <id>                    Run a single test id; requires uniqueness unless paired with --suite
    --input <message>              User message to process through pipeline stages
        --assistant-context <message>  Prior assistant text used to simulate follow-up context for stage preflight
        --followup-anchor <topic>      Explicit prior topic/entity used to simulate a resolved follow-up target
        --user-city <city>             Override user city for query-building diagnostics
        --has-recent-rationale         Simulate a recent first-principles rationale in routing context
        --has-recent-search-results    Simulate an active search session for follow-up routing
  --max-iters <N>                Max iteration count for score-gated runs (default: 1)
  --min-score <S>                Override minimum score threshold for all tests
  --allow-workspace-edits        Allow auto-iteration patch application
  --patch-budget-files <N>       Max files changed per iteration (default: 3)
  --patch-budget-lines <N>       Max total line delta per iteration (default: 200)
  --judge <cursor|none|model>    Judge mode for score enrichment (default: none)
  --judge-timeout-ms <N>         Cursor judge wait timeout in milliseconds (default: 60000)
  --judge-required <true|false>  Hard-fail if judge output missing/invalid (default: true)
  --target <v1|v2>               Runtime to drive: v1 (legacy headless, default) or v2 (hybrid Thaddeus.Runtime). Same agent pipeline either way.
    --suites-root <path>           Override suites root directory for run or stage mode
  --artifacts-root <path>        Override artifacts root directory
    --run-id <id>                  Inspect a specific harness run id instead of the latest run
  --help                         Show this help
""";

    public static HarnessCommandOptions Parse(string[] args)
    {
        if (args.Length == 0)
            return new HarnessCommandOptions { ShowHelp = true };

        var first = args[0].Trim().ToLowerInvariant();
        if (first is "--help" or "-h" or "/?")
            return new HarnessCommandOptions { ShowHelp = true };

        if (!TryParseCommand(first, out var command))
            throw new CommandLineException($"Unknown command '{args[0]}'.");

        // For stage/inspect commands, second arg may be the target (not a --flag)
        var remainingArgs = args.Skip(1).ToArray();
        var stageTarget = HarnessStageTarget.All;
        var inspectTarget = HarnessInspectTarget.LatestFailure;

        if (command == HarnessCommandKind.Stage && remainingArgs.Length > 0
            && !remainingArgs[0].StartsWith("--", StringComparison.Ordinal))
        {
            stageTarget = ParseStageTarget(remainingArgs[0]);
            remainingArgs = remainingArgs.Skip(1).ToArray();
        }
        else if (command == HarnessCommandKind.Inspect && remainingArgs.Length > 0
                 && !remainingArgs[0].StartsWith("--", StringComparison.Ordinal))
        {
            inspectTarget = ParseInspectTarget(remainingArgs[0]);
            remainingArgs = remainingArgs.Skip(1).ToArray();
        }

        var values = ParseValueMap(remainingArgs);
        var options = BuildOptions(command, values, stageTarget, inspectTarget);
        Validate(options);
        return options;
    }

    private static HarnessCommandOptions BuildOptions(
        HarnessCommandKind command,
        IReadOnlyDictionary<string, string?> values,
        HarnessStageTarget stageTarget = HarnessStageTarget.All,
        HarnessInspectTarget inspectTarget = HarnessInspectTarget.LatestFailure)
    {
        var suite = GetValue(values, "suite") ?? GetValue(values, "category") ?? "";
        var testId = GetValue(values, "test") ?? "";

        var judgeMode = ParseJudgeMode(GetValue(values, "judge")) ?? HarnessJudgeMode.None;
        var maxIters = ParseInt(GetValue(values, "max-iters"), defaultValue: 1, minValue: 1, key: "max-iters");
        var minScore = ParseDoubleNullable(GetValue(values, "min-score"), "min-score");
        var patchBudgetFiles = ParseInt(GetValue(values, "patch-budget-files"), 3, 1, "patch-budget-files");
        var patchBudgetLines = ParseInt(GetValue(values, "patch-budget-lines"), 200, 1, "patch-budget-lines");
        var judgeTimeoutMs = ParseInt(GetValue(values, "judge-timeout-ms"), 60_000, 1, "judge-timeout-ms");
        var judgeRequired = ParseBool(GetValue(values, "judge-required"), defaultValue: true, key: "judge-required");
        var hostTarget = ParseHostTarget(GetValue(values, "target")) ?? HarnessHostTarget.HeadlessV1;

        var defaultSuitesRoot = command == HarnessCommandKind.Stage
            ? Path.Combine("tools", "SirThaddeus.Harness", "StageSuites")
            : Path.Combine("tools", "SirThaddeus.Harness", "Suites");

        return new HarnessCommandOptions
        {
            Command = command,
            Mode = HarnessExecutionMode.Headless,
            JudgeMode = judgeMode,
            HostTarget = hostTarget,
            RunAllSuites = HasFlag(values, "all"),
            SuiteName = suite,
            TestId = testId,
            ShowHelp = HasFlag(values, "help"),
            MaxIterations = maxIters,
            MinScoreOverride = minScore,
            AllowWorkspaceEdits = HasFlag(values, "allow-workspace-edits"),
            PatchBudgetFiles = patchBudgetFiles,
            PatchBudgetLines = patchBudgetLines,
            JudgeTimeoutMs = judgeTimeoutMs,
            JudgeRequired = judgeRequired,
            SuitesRoot = GetValue(values, "suites-root")
                         ?? defaultSuitesRoot,
            ArtifactsRoot = GetValue(values, "artifacts-root")
                            ?? Path.Combine("artifacts", "harness"),
            StageTarget = stageTarget,
            StageInput = GetValue(values, "input") ?? "",
            StageAssistantContext = GetValue(values, "assistant-context") ?? "",
            StageFollowUpAnchor = GetValue(values, "followup-anchor") ?? "",
            StageUserCity = GetValue(values, "user-city") ?? "",
            StageHasRecentFirstPrinciplesRationale = HasFlag(values, "has-recent-rationale"),
            StageHasRecentSearchResults = HasFlag(values, "has-recent-search-results"),
            InspectTarget = inspectTarget,
            InspectRunId = GetValue(values, "run-id") ?? ""
        };
    }

    private static void Validate(HarnessCommandOptions options)
    {
        if (options.ShowHelp)
            return;

        if (options.Command == HarnessCommandKind.Stage)
        {
            if (options.RunAllSuites && !string.IsNullOrWhiteSpace(options.SuiteName))
                throw new CommandLineException("Use either --all or --suite/--category for stage mode, not both.");

            // Stage command: needs either --input or --all with suite/test selection
            if (string.IsNullOrWhiteSpace(options.StageInput) && !options.RunAllSuites
                && string.IsNullOrWhiteSpace(options.SuiteName)
                && string.IsNullOrWhiteSpace(options.TestId))
            {
                throw new CommandLineException(
                    "Stage command requires --input <message> for ad-hoc analysis, " +
                    "or --all/--suite/--test to run stage checks against test specs.");
            }
            return;
        }

        if (options.Command == HarnessCommandKind.Inspect)
            return;

        if (options.Command != HarnessCommandKind.Run)
            throw new CommandLineException("Unknown command. Use run, stage, or inspect.");

        if (options.RunAllSuites && !string.IsNullOrWhiteSpace(options.SuiteName))
            throw new CommandLineException("Use either --all or --suite/--category, not both.");

        if (!options.RunAllSuites &&
            string.IsNullOrWhiteSpace(options.SuiteName) &&
            string.IsNullOrWhiteSpace(options.TestId))
        {
            throw new CommandLineException("Select what to run with --all, --suite/--category, or --test.");
        }
    }

    private static HarnessStageTarget ParseStageTarget(string raw)
    {
        return raw.Trim().ToLowerInvariant() switch
        {
            "preprocess" => HarnessStageTarget.Preprocess,
            "preflight" => HarnessStageTarget.Preflight,
            "classify" => HarnessStageTarget.Classify,
            "query" => HarnessStageTarget.Query,
            "trace" => HarnessStageTarget.Trace,
            "all" => HarnessStageTarget.All,
            _ => throw new CommandLineException(
                $"Unknown stage target '{raw}'. Use preprocess, preflight, classify, query, trace, or all.")
        };
    }

    private static HarnessInspectTarget ParseInspectTarget(string raw)
    {
        return raw.Trim().ToLowerInvariant() switch
        {
            "latest-run" => HarnessInspectTarget.LatestRun,
            "latest-failure" => HarnessInspectTarget.LatestFailure,
            _ => throw new CommandLineException(
                $"Unknown inspect target '{raw}'. Use latest-run or latest-failure.")
        };
    }

    private static Dictionary<string, string?> ParseValueMap(string[] args)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new CommandLineException($"Unexpected token '{token}'. Options must start with --.");

            var key = token[2..].Trim();
            if (string.IsNullOrWhiteSpace(key))
                throw new CommandLineException("Empty option key.");

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                map[key] = args[i + 1];
                i++;
            }
            else
            {
                // flag option
                map[key] = null;
            }
        }

        return map;
    }

    private static bool TryParseCommand(string raw, out HarnessCommandKind command)
    {
        command = raw switch
        {
            "run" => HarnessCommandKind.Run,
            "stage" => HarnessCommandKind.Stage,
            "inspect" => HarnessCommandKind.Inspect,
            _ => default
        };

        return raw is "run" or "stage" or "inspect";
    }

    private static HarnessJudgeMode? ParseJudgeMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.Trim().ToLowerInvariant() switch
        {
            "none" => HarnessJudgeMode.None,
            "cursor" => HarnessJudgeMode.Cursor,
            "model" => HarnessJudgeMode.Model,
            _ => throw new CommandLineException($"Invalid --judge value '{raw}'.")
        };
    }

    private static HarnessHostTarget? ParseHostTarget(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.Trim().ToLowerInvariant() switch
        {
            "v1" or "headless" or "headless-v1" => HarnessHostTarget.HeadlessV1,
            "v2" or "hybrid" or "hybrid-v2" => HarnessHostTarget.HybridV2,
            _ => throw new CommandLineException(
                $"Invalid --target value '{raw}'. Use v1 (default, legacy headless) or v2 (hybrid runtime).")
        };
    }

    private static int ParseInt(string? raw, int defaultValue, int minValue, string key)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new CommandLineException($"Invalid --{key} value '{raw}'.");

        if (value < minValue)
            throw new CommandLineException($"--{key} must be >= {minValue}.");

        return value;
    }

    private static double? ParseDoubleNullable(string? raw, string key)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value))
            throw new CommandLineException($"Invalid --{key} value '{raw}'.");

        if (value < 0 || value > 10)
            throw new CommandLineException($"--{key} must be between 0 and 10.");

        return value;
    }

    private static bool ParseBool(string? raw, bool defaultValue, string key)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (!bool.TryParse(raw, out var value))
            throw new CommandLineException($"Invalid --{key} value '{raw}'. Use true|false.");

        return value;
    }

    private static bool HasFlag(IReadOnlyDictionary<string, string?> values, string key)
        => values.ContainsKey(key);

    private static string? GetValue(IReadOnlyDictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var value) ? value : null;
}
