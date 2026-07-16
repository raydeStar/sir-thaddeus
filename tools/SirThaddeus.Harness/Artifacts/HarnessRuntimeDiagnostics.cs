using System.Globalization;
using System.Text.RegularExpressions;
using SirThaddeus.Harness.Execution;

namespace SirThaddeus.Harness.Artifacts;

public sealed record HarnessRuntimeDiagnostics
{
    public int SchemaVersion { get; init; } = 1;
    public required string TurnId { get; init; }
    public bool FullCompositionObserved { get; init; }
    public IReadOnlyList<HarnessDiagnosticEvent> Events { get; init; } = [];
    public required HarnessDiagnosticTimings TimingsMs { get; init; }
    public required HarnessDiagnosticCallCounts CallCounts { get; init; }
}

public sealed record HarnessDiagnosticEvent
{
    public required string Name { get; init; }
    public string? Stage { get; init; }
    public string? Outcome { get; init; }
    public double? DurationMs { get; init; }
    public double? ElapsedMs { get; init; }
    public bool? Passed { get; init; }
    public bool? RepairNeeded { get; init; }
    public bool? Changed { get; init; }
    public int? Messages { get; init; }
    public int? Tools { get; init; }
}

public sealed record HarnessDiagnosticTimings
{
    public double RuntimeWarmup { get; init; }
    public double Reset { get; init; }
    public double TestWork { get; init; }
    public double EndToEnd { get; init; }
    public double? ProductPipeline { get; init; }
    public double ProviderTotal { get; init; }
    public double? ToolLoop { get; init; }
    public double? CompletionValidation { get; init; }
    public double? PromptBuild { get; init; }
    public double? FirstVisibleContent { get; init; }
}

public sealed record HarnessDiagnosticCallCounts
{
    public int ProviderRequests { get; init; }
    public int HelperRequests { get; init; }
}

internal static partial class HarnessRuntimeDiagnosticsReader
{
    private static readonly string[] EventNames =
    [
        "routing.latency",
        "PIPELINE_STEP_TIMING",
        "PIPELINE_TIMING",
        "PROMPT_ASSEMBLY_TIMING",
        "COMPLETION_VALIDATION_DECISION",
        "COMPLETION_REPAIR_TIMING",
        "llm.request_completed",
        "EXPERIMENT_ACTIVATION"
    ];

    public static HarnessRuntimeDiagnostics Read(
        string sandboxRoot,
        string turnId,
        HarnessTiming timing)
    {
        var logDirectory = Path.Combine(sandboxRoot, "logs");
        var events = new List<HarnessDiagnosticEvent>();
        if (Directory.Exists(logDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(logDirectory, "thaddeus-runtime-*.log")
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                foreach (var line in ReadCompleteLines(path))
                {
                    if (!ContainsToken(line, "turnId", turnId) &&
                        !ContainsToken(line, "turn_id", turnId))
                        continue;

                    var name = EventNames.FirstOrDefault(eventName =>
                        line.Contains(eventName, StringComparison.Ordinal));
                    if (name is null)
                        continue;

                    events.Add(ParseEvent(name, line));
                }
            }
        }

        var routingStages = events
            .Where(item => item.Name == "routing.latency")
            .Select(item => item.Stage)
            .ToHashSet(StringComparer.Ordinal);
        var pipelineSteps = events.Where(item => item.Name == "PIPELINE_STEP_TIMING").ToArray();
        var providerEvents = events.Where(item => item.Name == "llm.request_completed").ToArray();

        return new HarnessRuntimeDiagnostics
        {
            TurnId = turnId,
            FullCompositionObserved = routingStages.Contains("pipeline_start") &&
                                      routingStages.Contains("pipeline_complete") &&
                                      pipelineSteps.Length > 0,
            Events = events,
            TimingsMs = new HarnessDiagnosticTimings
            {
                RuntimeWarmup = timing.RuntimeWarmupSeconds * 1000,
                Reset = timing.ResetSeconds * 1000,
                TestWork = timing.TestWorkSeconds * 1000,
                EndToEnd = timing.TotalSeconds * 1000,
                ProductPipeline = LastDuration(events, "PIPELINE_TIMING"),
                ProviderTotal = providerEvents.Sum(item => item.DurationMs ?? 0),
                ToolLoop = LastDuration(pipelineSteps, stage: "ToolLoop"),
                CompletionValidation = LastDuration(pipelineSteps, stage: "CompletionValidation"),
                PromptBuild = LastDuration(events, "PROMPT_ASSEMBLY_TIMING"),
                FirstVisibleContent = events.LastOrDefault(item =>
                    item.Name == "routing.latency" && item.Stage == "first_ui_delta")?.ElapsedMs
            },
            CallCounts = new HarnessDiagnosticCallCounts
            {
                ProviderRequests = providerEvents.Length,
                HelperRequests = providerEvents.Count(item =>
                    string.Equals(item.Stage, "helper", StringComparison.OrdinalIgnoreCase))
            }
        };
    }

    private static IEnumerable<string> ReadCompleteLines(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
                lines.Add(line);
            return lines;
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static HarnessDiagnosticEvent ParseEvent(string name, string line)
    {
        var stageKey = name switch
        {
            "llm.request_completed" => "task",
            "EXPERIMENT_ACTIVATION" => "event",
            "PIPELINE_STEP_TIMING" => "step",
            _ => "stage"
        };
        var stage = Value(line, stageKey);
        var duration = Number(line, "durationMs") ?? Number(line, "elapsedMs") ??
                       Number(line, "elapsed_ms");
        return new HarnessDiagnosticEvent
        {
            Name = name == "EXPERIMENT_ACTIVATION" ? "experiment.activation" : name,
            Stage = stage,
            Outcome = Value(line, name == "EXPERIMENT_ACTIVATION" ? "decision" : "outcome"),
            DurationMs = duration,
            ElapsedMs = Number(line, "elapsedMs") ?? Number(line, "elapsed_ms"),
            Passed = Boolean(line, "passed"),
            RepairNeeded = Boolean(line, "repair_needed"),
            Changed = Boolean(line, "changed"),
            Messages = Integer(line, "messages"),
            Tools = Integer(line, "tools")
        };
    }

    private static bool ContainsToken(string line, string key, string expected) =>
        string.Equals(Value(line, key), expected, StringComparison.Ordinal);

    private static string? Value(string line, string key)
    {
        foreach (Match match in KeyValueRegex().Matches(line))
        {
            if (string.Equals(match.Groups["key"].Value, key, StringComparison.Ordinal))
                return match.Groups["value"].Value.Trim('"');
        }
        return null;
    }

    private static double? Number(string line, string key) =>
        double.TryParse(Value(line, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static int? Integer(string line, string key) =>
        int.TryParse(Value(line, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static bool? Boolean(string line, string key) =>
        bool.TryParse(Value(line, key), out var value) ? value : null;

    private static double? LastDuration(
        IEnumerable<HarnessDiagnosticEvent> events,
        string? name = null,
        string? stage = null) =>
        events.LastOrDefault(item =>
            (name is null || item.Name == name) &&
            (stage is null || string.Equals(item.Stage, stage, StringComparison.OrdinalIgnoreCase)))
        ?.DurationMs;

    [GeneratedRegex("""(?<key>[A-Za-z_][A-Za-z0-9_]*)=(?<value>"[^"]*"|[^\s,]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueRegex();
}
