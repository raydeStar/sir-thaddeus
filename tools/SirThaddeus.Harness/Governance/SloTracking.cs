using System.Text.Json;
using System.Text.Json.Serialization;

namespace SirThaddeus.Harness.Governance;

/// <summary>
/// SLO (Service Level Objective) definitions for the test harness.
/// Tracks pass rate, flaky rate, mean diagnosis time, and stage-specific failure distribution.
/// </summary>
public static class SloDefinitions
{
    /// <summary>Target: ≥95% of non-Integration tests should pass on every commit.</summary>
    public const double UnitPassRateTarget = 0.95;

    /// <summary>Target: ≤2% flaky rate (tests that flip between pass/fail across runs).</summary>
    public const double FlakyRateMax = 0.02;

    /// <summary>Target: harness suite pass rate ≥80% for smoke, ≥60% for live search.</summary>
    public const double SmokeSuitePassTarget = 0.80;
    public const double LiveSearchPassTarget = 0.60;

    /// <summary>Target: stage test pass rate ≥90%.</summary>
    public const double StageTestPassTarget = 0.90;

    /// <summary>Target: mean single-test execution time ≤30s for non-search tests.</summary>
    public const double MeanTestDurationSecondsTarget = 30.0;
}

/// <summary>
/// A single run's SLO metrics snapshot.
/// Can be serialized and accumulated across runs.
/// </summary>
public sealed record SloSnapshot
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("run_id")]
    public string RunId { get; init; } = "";

    [JsonPropertyName("total_tests")]
    public int TotalTests { get; init; }

    [JsonPropertyName("passed")]
    public int Passed { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; init; }

    [JsonPropertyName("pass_rate")]
    public double PassRate => TotalTests > 0 ? (double)Passed / TotalTests : 0;

    [JsonPropertyName("mean_duration_ms")]
    public double MeanDurationMs { get; init; }

    [JsonPropertyName("stage_failures")]
    public IReadOnlyDictionary<string, int> StageFailures { get; init; } =
        new Dictionary<string, int>();

    [JsonPropertyName("flaky_tests")]
    public IReadOnlyList<string> FlakyTests { get; init; } = [];

    [JsonPropertyName("slo_violations")]
    public IReadOnlyList<string> SloViolations { get; init; } = [];
}

/// <summary>
/// Evaluates an SLO snapshot against defined targets.
/// </summary>
public static class SloEvaluator
{
    public static IReadOnlyList<string> Evaluate(SloSnapshot snapshot, string context = "commit")
    {
        var violations = new List<string>();

        if (context == "commit" && snapshot.PassRate < SloDefinitions.UnitPassRateTarget)
            violations.Add($"Unit pass rate {snapshot.PassRate:P1} < {SloDefinitions.UnitPassRateTarget:P0} target");

        if (snapshot.TotalTests > 0)
        {
            var flakyRate = (double)snapshot.FlakyTests.Count / snapshot.TotalTests;
            if (flakyRate > SloDefinitions.FlakyRateMax)
                violations.Add($"Flaky rate {flakyRate:P1} > {SloDefinitions.FlakyRateMax:P0} max");
        }

        if (snapshot.MeanDurationMs > SloDefinitions.MeanTestDurationSecondsTarget * 1000)
            violations.Add($"Mean test duration {snapshot.MeanDurationMs / 1000:F1}s > {SloDefinitions.MeanTestDurationSecondsTarget}s target");

        return violations;
    }
}

/// <summary>
/// Accumulates SLO snapshots over time and reports trends.
/// </summary>
public sealed class SloTracker
{
    private readonly string _historyFilePath;
    private readonly List<SloSnapshot> _history;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SloTracker(string historyFilePath)
    {
        _historyFilePath = historyFilePath;
        _history = LoadHistory();
    }

    public IReadOnlyList<SloSnapshot> History => _history;

    /// <summary>
    /// Records a new snapshot and persists to disk.
    /// </summary>
    public void Record(SloSnapshot snapshot)
    {
        var evaluated = snapshot with
        {
            SloViolations = SloEvaluator.Evaluate(snapshot)
        };
        _history.Add(evaluated);

        // Keep last 90 days
        var cutoff = DateTimeOffset.UtcNow.AddDays(-90);
        _history.RemoveAll(s => s.Timestamp < cutoff);

        SaveHistory();
    }

    /// <summary>
    /// Generates a summary report of the last N snapshots.
    /// </summary>
    public SloSummary Summarize(int lastN = 30)
    {
        var recent = _history.TakeLast(lastN).ToList();
        if (recent.Count == 0)
            return new SloSummary();

        return new SloSummary
        {
            TotalRuns = recent.Count,
            MeanPassRate = recent.Average(s => s.PassRate),
            MinPassRate = recent.Min(s => s.PassRate),
            MaxPassRate = recent.Max(s => s.PassRate),
            TotalSloViolations = recent.Sum(s => s.SloViolations.Count),
            MostCommonViolation = recent
                .SelectMany(s => s.SloViolations)
                .GroupBy(v => v)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault() ?? "none",
            StageFailureDistribution = AggregateStageFailures(recent),
            MostFlakyTests = recent
                .SelectMany(s => s.FlakyTests)
                .GroupBy(t => t)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => new FlakyTestEntry(g.Key, g.Count()))
                .ToList()
        };
    }

    private static IReadOnlyDictionary<string, int> AggregateStageFailures(
        IEnumerable<SloSnapshot> snapshots)
    {
        var agg = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            foreach (var (stage, count) in snapshot.StageFailures)
            {
                if (agg.TryGetValue(stage, out var existing))
                    agg[stage] = existing + count;
                else
                    agg[stage] = count;
            }
        }
        return agg;
    }

    private List<SloSnapshot> LoadHistory()
    {
        if (!File.Exists(_historyFilePath))
            return [];

        try
        {
            var json = File.ReadAllText(_historyFilePath);
            return JsonSerializer.Deserialize<List<SloSnapshot>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveHistory()
    {
        var dir = Path.GetDirectoryName(_historyFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_history, JsonOptions);
        File.WriteAllText(_historyFilePath, json);
    }
}

public sealed record SloSummary
{
    public int TotalRuns { get; init; }
    public double MeanPassRate { get; init; }
    public double MinPassRate { get; init; }
    public double MaxPassRate { get; init; }
    public int TotalSloViolations { get; init; }
    public string MostCommonViolation { get; init; } = "none";
    public IReadOnlyDictionary<string, int> StageFailureDistribution { get; init; } =
        new Dictionary<string, int>();
    public IReadOnlyList<FlakyTestEntry> MostFlakyTests { get; init; } = [];
}

public sealed record FlakyTestEntry(string TestId, int FlakyCount);
