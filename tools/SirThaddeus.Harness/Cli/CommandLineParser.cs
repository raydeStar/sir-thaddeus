using System.Globalization;

namespace SirThaddeus.Harness.Cli;

public static class CommandLineParser
{
    public static string HelpText => """
Usage:
  harness run --suite <name> --mode live|replay|stub --max-iters N --min-score S [options]
  harness record --suite <name> [options]
  harness replay --suite <name> [options]
  harness smoke --mode live|replay|stub [options]

Options:
  --suite <name>                 Suite name (directory under tools/SirThaddeus.Harness/Suites)
  --mode <live|replay|stub>      Execution mode (command defaults may override)
  --max-iters <N>                Max iteration count for score-gated runs (default: 1)
  --min-score <S>                Override minimum score threshold for all tests
  --allow-workspace-edits        Allow auto-iteration patch application
  --patch-budget-files <N>       Max files changed per iteration (default: 3)
  --patch-budget-lines <N>       Max total line delta per iteration (default: 200)
  --judge <cursor|none|model>    Judge mode for score enrichment (default: none)
  --judge-timeout-ms <N>         Cursor judge wait timeout in milliseconds (default: 60000)
  --judge-required <true|false>  Hard-fail if judge output missing/invalid (default: true)
  --suites-root <path>           Override suites root directory
  --fixtures-root <path>         Override fixtures root directory
  --artifacts-root <path>        Override artifacts root directory
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

        var values = ParseValueMap(args.Skip(1).ToArray());
        var options = BuildOptions(command, values);
        Validate(options);
        return options;
    }

    private static HarnessCommandOptions BuildOptions(
        HarnessCommandKind command,
        IReadOnlyDictionary<string, string?> values)
    {
        var modeRaw = GetValue(values, "mode");
        var modeExplicitlySet = !string.IsNullOrWhiteSpace(modeRaw);
        var requestedMode = ParseMode(modeRaw) ?? HarnessExecutionMode.Live;
        var mode = command switch
        {
            HarnessCommandKind.Record => HarnessExecutionMode.Live,
            HarnessCommandKind.Replay => HarnessExecutionMode.Replay,
            _ => requestedMode
        };

        var suite = command == HarnessCommandKind.Smoke
            ? "smoke"
            : (GetValue(values, "suite") ?? "");

        var judgeMode = ParseJudgeMode(GetValue(values, "judge")) ?? HarnessJudgeMode.None;
        var maxIters = ParseInt(GetValue(values, "max-iters"), defaultValue: 1, minValue: 1, key: "max-iters");
        var minScore = ParseDoubleNullable(GetValue(values, "min-score"), "min-score");
        var patchBudgetFiles = ParseInt(GetValue(values, "patch-budget-files"), 3, 1, "patch-budget-files");
        var patchBudgetLines = ParseInt(GetValue(values, "patch-budget-lines"), 200, 1, "patch-budget-lines");
        var judgeTimeoutMs = ParseInt(GetValue(values, "judge-timeout-ms"), 60_000, 1, "judge-timeout-ms");
        var judgeRequired = ParseBool(GetValue(values, "judge-required"), defaultValue: true, key: "judge-required");

        return new HarnessCommandOptions
        {
            Command = command,
            Mode = mode,
            ModeExplicitlySet = modeExplicitlySet,
            JudgeMode = judgeMode,
            SuiteName = suite,
            ShowHelp = HasFlag(values, "help"),
            MaxIterations = maxIters,
            MinScoreOverride = minScore,
            AllowWorkspaceEdits = HasFlag(values, "allow-workspace-edits"),
            PatchBudgetFiles = patchBudgetFiles,
            PatchBudgetLines = patchBudgetLines,
            JudgeTimeoutMs = judgeTimeoutMs,
            JudgeRequired = judgeRequired,
            SuitesRoot = GetValue(values, "suites-root")
                         ?? Path.Combine("tools", "SirThaddeus.Harness", "Suites"),
            FixturesRoot = GetValue(values, "fixtures-root")
                           ?? Path.Combine("tools", "SirThaddeus.Harness", "fixtures"),
            ArtifactsRoot = GetValue(values, "artifacts-root")
                            ?? Path.Combine("artifacts", "harness")
        };
    }

    private static void Validate(HarnessCommandOptions options)
    {
        if (options.ShowHelp)
            return;

        if (string.IsNullOrWhiteSpace(options.SuiteName))
            throw new CommandLineException("Missing required option --suite <name>.");

        if (options.Mode == HarnessExecutionMode.Replay &&
            options.Command == HarnessCommandKind.Record)
        {
            throw new CommandLineException("Record command cannot run in replay mode.");
        }
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
            "record" => HarnessCommandKind.Record,
            "replay" => HarnessCommandKind.Replay,
            "smoke" => HarnessCommandKind.Smoke,
            _ => default
        };

        return raw is "run" or "record" or "replay" or "smoke";
    }

    private static HarnessExecutionMode? ParseMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.Trim().ToLowerInvariant() switch
        {
            "live" => HarnessExecutionMode.Live,
            "replay" => HarnessExecutionMode.Replay,
            "stub" => HarnessExecutionMode.Stub,
            _ => throw new CommandLineException($"Invalid --mode value '{raw}'.")
        };
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
