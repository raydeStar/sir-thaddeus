using System.Text.Json;
using System.Text.Json.Serialization;
using SirThaddeus.Harness.Cli;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Tracing;

namespace SirThaddeus.Harness.Execution;

internal sealed class HarnessInspector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<int> RunAsync(HarnessCommandOptions options, CancellationToken cancellationToken)
    {
        var artifactsRoot = Path.IsPathRooted(options.ArtifactsRoot)
            ? options.ArtifactsRoot
            : Path.GetFullPath(options.ArtifactsRoot);

        var runDirectory = ResolveRunDirectory(artifactsRoot, options.InspectRunId);
        if (runDirectory is null)
        {
            Console.WriteLine("No harness run artifacts were found.");
            return 1;
        }

        var summaryJsonPath = Path.Combine(runDirectory.FullName, "summary.json");
        var summaryMarkdownPath = Path.Combine(runDirectory.FullName, "summary.md");

        if (!File.Exists(summaryJsonPath))
        {
            Console.WriteLine($"Run found, but summary.json is missing: {summaryJsonPath}");
            return 1;
        }

        var payload = JsonSerializer.Deserialize<SummaryPayload>(
            await File.ReadAllTextAsync(summaryJsonPath, cancellationToken),
            JsonOptions);

        if (payload is null)
        {
            Console.WriteLine($"Unable to parse summary.json: {summaryJsonPath}");
            return 1;
        }

        Console.WriteLine($"Inspect target: {options.InspectTarget}");
        Console.WriteLine($"Run: {payload.RunId}");
        Console.WriteLine($"Artifacts: {runDirectory.FullName}");
        Console.WriteLine($"Summary JSON: {summaryJsonPath}");
        if (File.Exists(summaryMarkdownPath))
            Console.WriteLine($"Summary Markdown: {summaryMarkdownPath}");
        Console.WriteLine();

        if (options.InspectTarget == HarnessInspectTarget.LatestRun)
        {
            PrintRunSummary(payload);
            return 0;
        }

        var failed = payload.Results.Where(r => !r.Passed).ToList();
        if (failed.Count == 0)
        {
            Console.WriteLine("Latest run has no failed tests.");
            PrintRunSummary(payload);
            return 0;
        }

        var target = failed[^1];
        PrintFailureSummary(target, failed.Count);
        await PrintFailureArtifactsAsync(target, cancellationToken);
        return 0;
    }

    private static DirectoryInfo? ResolveRunDirectory(string artifactsRoot, string inspectRunId)
    {
        var root = new DirectoryInfo(artifactsRoot);
        if (!root.Exists)
            return null;

        if (!string.IsNullOrWhiteSpace(inspectRunId))
        {
            var specific = new DirectoryInfo(Path.Combine(root.FullName, inspectRunId));
            return specific.Exists ? specific : null;
        }

        return root.EnumerateDirectories()
            .Where(d => d.Name != "stage")
            .OrderByDescending(d => d.Name)
            .FirstOrDefault();
    }

    private static void PrintRunSummary(SummaryPayload payload)
    {
        Console.WriteLine($"Tests run: {payload.TestsRun}");
        Console.WriteLine($"Passed: {payload.TestsPassed}");
        Console.WriteLine($"Failed: {payload.TestsFailed}");
        Console.WriteLine($"Average score: {payload.AverageScore:0.00}");

        if (payload.Results.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine("Lowest-scoring tests:");
        foreach (var result in payload.Results.OrderBy(r => r.Score).Take(5))
        {
            Console.WriteLine($"  {result.Suite}/{result.TestId} score={result.Score:0.00} passed={result.Passed} artifact={result.ArtifactDirectory}");
        }
    }

    private static void PrintFailureSummary(SummaryResult target, int failureCount)
    {
        Console.WriteLine($"Failures in run: {failureCount}");
        Console.WriteLine($"Selected failure: {target.Suite}/{target.TestId}");
        Console.WriteLine($"Score: {target.Score:0.00} | Passed: {target.Passed} | HardPass: {target.HardPass}");
        Console.WriteLine($"Artifact directory: {target.ArtifactDirectory}");
        if (target.HardFailures.Count > 0)
            Console.WriteLine($"Hard failures: {string.Join("; ", target.HardFailures)}");
        if (target.Breakdown is not null)
        {
            Console.WriteLine(
                $"Penalties: keyword={target.Breakdown.KeywordPenalty:0.0}, deflection={target.Breakdown.DeflectionPenalty:0.0}, tool={target.Breakdown.ToolIncorporationPenalty:0.0}, assertion={target.Breakdown.AssertionDensityPenalty:0.0}, personality={target.Breakdown.PersonalityAdjustment:0.0}");
        }

        if (!string.IsNullOrWhiteSpace(target.FinalResponse))
        {
            Console.WriteLine();
            Console.WriteLine("Final response preview:");
            Console.WriteLine($"  {Truncate(target.FinalResponse, 280)}");
        }
    }

    private static async Task PrintFailureArtifactsAsync(SummaryResult target, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.ArtifactDirectory) || !Directory.Exists(target.ArtifactDirectory))
            return;

        var finalPath = Path.Combine(target.ArtifactDirectory, "final.txt");
        var scorePath = Path.Combine(target.ArtifactDirectory, "score.json");
        var stepsPath = Path.Combine(target.ArtifactDirectory, "steps.jsonl");

        Console.WriteLine();
        Console.WriteLine($"Files: {finalPath} | {scorePath} | {stepsPath}");

        if (File.Exists(stepsPath))
        {
            var lines = await File.ReadAllLinesAsync(stepsPath, cancellationToken);
            var steps = lines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize<TraceStep>(line, JsonOptions))
                .Where(step => step is not null)
                .Cast<TraceStep>()
                .ToList();

            var suspicious = steps
                .Where(step => step.Error is not null || LooksLikeSuspiciousResult(step.Result))
                .Take(5)
                .ToList();

            if (suspicious.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Suspicious tool/output signals:");
                foreach (var step in suspicious)
                {
                    var signal = step.Error?.Message ?? step.Result ?? step.Content ?? "";
                    Console.WriteLine($"  step={step.StepType} tool={step.ToolName ?? "-"} detail={Truncate(signal, 180)}");
                }
            }
        }
    }

    private static bool LooksLikeSuspiciousResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return false;

        var lower = result.ToLowerInvariant();
        return lower.Contains("timeout", StringComparison.Ordinal) ||
               lower.Contains("tool error", StringComparison.Ordinal) ||
               lower.Contains("unavailable", StringComparison.Ordinal) ||
               lower.Contains("system error", StringComparison.Ordinal) ||
               lower.Contains("denied", StringComparison.Ordinal);
    }

    private static string Truncate(string value, int maxLength)
    {
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= maxLength ? oneLine : oneLine[..(maxLength - 3)] + "...";
    }

    private sealed record SummaryPayload
    {
        [JsonPropertyName("run_id")]
        public string RunId { get; init; } = "";

        [JsonPropertyName("tests_run")]
        public int TestsRun { get; init; }

        [JsonPropertyName("tests_passed")]
        public int TestsPassed { get; init; }

        [JsonPropertyName("tests_failed")]
        public int TestsFailed { get; init; }

        [JsonPropertyName("average_score")]
        public double AverageScore { get; init; }

        [JsonPropertyName("results")]
        public List<SummaryResult> Results { get; init; } = [];
    }

    private sealed record SummaryResult
    {
        [JsonPropertyName("test_id")]
        public string TestId { get; init; } = "";

        [JsonPropertyName("suite")]
        public string Suite { get; init; } = "";

        [JsonPropertyName("score")]
        public double Score { get; init; }

        [JsonPropertyName("passed")]
        public bool Passed { get; init; }

        [JsonPropertyName("hard_pass")]
        public bool HardPass { get; init; }

        [JsonPropertyName("final_response")]
        public string? FinalResponse { get; init; }

        [JsonPropertyName("artifact_directory")]
        public string? ArtifactDirectory { get; init; }

        [JsonPropertyName("hard_failures")]
        public List<string> HardFailures { get; init; } = [];

        [JsonPropertyName("breakdown")]
        public SummaryBreakdown? Breakdown { get; init; }
    }

    private sealed record SummaryBreakdown
    {
        [JsonPropertyName("keyword_penalty")]
        public double KeywordPenalty { get; init; }

        [JsonPropertyName("deflection_penalty")]
        public double DeflectionPenalty { get; init; }

        [JsonPropertyName("tool_incorporation_penalty")]
        public double ToolIncorporationPenalty { get; init; }

        [JsonPropertyName("assertion_density_penalty")]
        public double AssertionDensityPenalty { get; init; }

        [JsonPropertyName("personality_adjustment")]
        public double PersonalityAdjustment { get; init; }
    }
}