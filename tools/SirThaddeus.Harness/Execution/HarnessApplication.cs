using System.Text;
using SirThaddeus.Config;
using SirThaddeus.Harness.Artifacts;
using SirThaddeus.Harness.Cli;
using SirThaddeus.Harness.Iteration;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Reporting;
using SirThaddeus.Harness.Scoring;
using SirThaddeus.Harness.Suites;

namespace SirThaddeus.Harness.Execution;

public sealed class HarnessApplication
{
    private readonly SuiteLoader _suiteLoader = new();
    private readonly HarnessArtifactWriter _artifactWriter = new();
    private readonly ScoringEngine _scoringEngine = new();
    private readonly CursorJudgeClient _judgeClient = new();
    private readonly AutoIterationEngine _autoIterationEngine = new(new WorkspacePatchApplier());

    public async Task<int> RunAsync(HarnessCommandOptions options, CancellationToken cancellationToken)
    {
        var selectedSuites = ResolveSelectedSuites(options);
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
        var settings = SettingsManager.Load();
        var runStart = DateTimeOffset.UtcNow;

        Console.WriteLine($"Harness command: {options.Command}");
        Console.WriteLine($"Selection: {DescribeSelection(options, selectedSuites)}");
        Console.WriteLine($"Mode: {options.Mode}");
        Console.WriteLine($"RunId: {runId}");
        Console.WriteLine();

        var passCount = 0;
        var failCount = 0;
        var summaries = new List<string>();
        var reportResults = new List<SuiteReporter.TestResult>();

        await using var host = HarnessHostFactory.Create(options, settings, selectedSuites);

        foreach (var suite in selectedSuites)
        {
            Console.WriteLine($"== Suite: {suite.Name} ({suite.Tests.Count} test(s))");

            var context = new SuiteRunContext
            {
                Options = options,
                SuiteName = suite.Name,
                RunId = runId,
                Host = host
            };

            var runner = new SingleTestRunner(
                context,
                _artifactWriter,
                _scoringEngine,
                _judgeClient);

            var suiteScoreCards = new List<ScoreCard>();

            foreach (var test in suite.Tests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Console.WriteLine($"== Test: {test.Id} - {test.Name}");

                var previousBestScore = (double?)null;
                var previousBestFinal = (string?)null;

                async Task<TestAttemptResult> RunIterationAsync(int iteration)
                {
                    var single = await runner.RunAsync(
                        test,
                        iteration,
                        previousBestScore,
                        previousBestFinal,
                        cancellationToken);

                    if (previousBestScore is null || single.Score.FinalScore > previousBestScore.Value)
                    {
                        previousBestScore = single.Score.FinalScore;
                        previousBestFinal = single.Response.Text;
                    }

                    return new TestAttemptResult
                    {
                        Iteration = iteration,
                        Score = single.Score,
                        FinalResponse = single.Response.Text,
                        ArtifactDirectory = single.ArtifactPaths.RootDirectory,
                        JudgeResult = single.JudgeResult,
                        Timing = single.Timing
                    };
                }

                var attempts = await _autoIterationEngine.ExecuteAsync(
                    options,
                    test,
                    RunIterationAsync,
                    cancellationToken);

                var best = attempts.OrderByDescending(a => a.Score.FinalScore).First();
                var minScore = ScoringEngine.ResolveThreshold(options.MinScoreOverride ?? test.MinScore);
                var passed = best.Score.HardPass && best.Score.FinalScore >= minScore;

                if (passed)
                    passCount++;
                else
                    failCount++;

                var resultLabel = passed ? "PASS" : "FAIL";
                Console.WriteLine($"Result: {resultLabel} | score={best.Score.FinalScore:0.00} | min={minScore:0.00}");
                Console.WriteLine($"Attempts: {attempts.Count}");
                Console.WriteLine($"Artifacts: {best.ArtifactDirectory}");
                PrintTimingSummary(attempts);
                Console.WriteLine();

                summaries.Add(BuildSummaryLine(suite.Name, test, best, passed, minScore));
                suiteScoreCards.Add(best.Score);
                var aggregateTiming = AggregateTiming(attempts);

                reportResults.Add(new SuiteReporter.TestResult
                {
                    SuiteName = suite.Name,
                    TestId = test.Id,
                    TestName = test.Name,
                    Score = best.Score,
                    MinScore = minScore,
                    Passed = passed,
                    Attempts = attempts.Count,
                    ArtifactDirectory = best.ArtifactDirectory,
                    FinalResponse = best.FinalResponse,
                    RuntimeWarmupSeconds = aggregateTiming.RuntimeWarmupSeconds,
                    ResetSeconds = aggregateTiming.ResetSeconds,
                    TestWorkSeconds = aggregateTiming.TestWorkSeconds,
                    HostTotalSeconds = aggregateTiming.TotalSeconds
                });
            }

            ScoringEngine.DetectScoringAnomalies(suiteScoreCards, suite.Name);
        }

        var durationSeconds = (DateTimeOffset.UtcNow - runStart).TotalSeconds;
        var artifactsRunRoot = Path.Combine(options.ArtifactsRoot, runId);

        var reportContext = new SuiteReporter.ReportContext
        {
            RunId = runId,
            ModelName = settings.Llm.Model,
            JudgeMode = options.JudgeMode.ToString().ToLowerInvariant(),
            ArtifactsRoot = artifactsRunRoot,
            DurationSeconds = Math.Round(durationSeconds, 1)
        };

        SuiteReporter.PrintSummary(reportResults, reportContext);

        // Write machine-readable JSON summary
        var jsonSummaryPath = Path.Combine(
            Path.IsPathRooted(artifactsRunRoot) ? artifactsRunRoot : Path.GetFullPath(artifactsRunRoot),
            "summary.json");
        SuiteReporter.WriteJsonSummary(reportResults, reportContext, jsonSummaryPath);
        var markdownSummaryPath = Path.Combine(
            Path.IsPathRooted(artifactsRunRoot) ? artifactsRunRoot : Path.GetFullPath(artifactsRunRoot),
            "summary.md");
        SuiteReporter.WriteMarkdownSummary(reportResults, reportContext, markdownSummaryPath);

        Console.WriteLine("== Run Summary");
        foreach (var line in summaries)
            Console.WriteLine(line);
        Console.WriteLine();
        Console.WriteLine($"Passed: {passCount}");
        Console.WriteLine($"Failed: {failCount}");

        return failCount == 0 ? 0 : 1;
    }

    private static void PrintTimingSummary(IReadOnlyList<TestAttemptResult> attempts)
    {
        var warmup = attempts.Sum(a => a.Timing.RuntimeWarmupSeconds);
        var reset = attempts.Sum(a => a.Timing.ResetSeconds);
        var work = attempts.Sum(a => a.Timing.TestWorkSeconds);
        var total = attempts.Sum(a => a.Timing.TotalSeconds);
        if (total <= 0)
            return;

        var builder = new StringBuilder("Timing: ");
        if (warmup > 0)
            builder.Append($"runtime_warmup={warmup:0.00}s ");
        builder.Append($"reset={reset:0.00}s test_work={work:0.00}s total={total:0.00}s");
        if (attempts.Count > 1)
            builder.Append($" attempts={attempts.Count}");
        Console.WriteLine(builder.ToString());
    }

    private static HarnessTiming AggregateTiming(IReadOnlyList<TestAttemptResult> attempts) =>
        new(
            RuntimeWarmupSeconds: attempts.Sum(a => a.Timing.RuntimeWarmupSeconds),
            ResetSeconds: attempts.Sum(a => a.Timing.ResetSeconds),
            TestWorkSeconds: attempts.Sum(a => a.Timing.TestWorkSeconds),
            TotalSeconds: attempts.Sum(a => a.Timing.TotalSeconds));

    private static string BuildSummaryLine(
        string suiteName,
        HarnessTestCase test,
        TestAttemptResult best,
        bool passed,
        double minScore)
    {
        var status = passed ? "[PASS]" : "[FAIL]";
        var builder = new StringBuilder();
        builder.Append(status).Append(' ')
            .Append(suiteName)
            .Append('/')
            .Append(test.Id)
            .Append(" score=").Append(best.Score.FinalScore.ToString("0.00"))
            .Append(" min=").Append(minScore.ToString("0.00"));

        if (!best.Score.HardPass && best.Score.HardFailures.Count > 0)
        {
            builder.Append(" hard_failures=\"")
                .Append(string.Join("; ", best.Score.HardFailures))
                .Append('"');
        }

        return builder.ToString();
    }

    private IReadOnlyList<HarnessSuite> ResolveSelectedSuites(HarnessCommandOptions options)
    {
        var suiteNames = options.RunAllSuites ||
                         (!string.IsNullOrWhiteSpace(options.TestId) && string.IsNullOrWhiteSpace(options.SuiteName))
            ? _suiteLoader.ListSuiteNames(options.SuitesRoot)
            : [options.SuiteName];

        var loadedSuites = suiteNames
            .Select(name => _suiteLoader.LoadSuite(options.SuitesRoot, name))
            .ToList();

        if (string.IsNullOrWhiteSpace(options.TestId))
            return loadedSuites;

        var matchedSuites = loadedSuites
            .Select(suite => new HarnessSuite
            {
                Name = suite.Name,
                Tests = suite.Tests
                    .Where(test => string.Equals(test.Id, options.TestId, StringComparison.OrdinalIgnoreCase))
                    .ToList()
            })
            .Where(suite => suite.Tests.Count > 0)
            .ToList();

        if (matchedSuites.Count == 0)
        {
            var scope = string.IsNullOrWhiteSpace(options.SuiteName)
                ? "any suite"
                : $"suite '{options.SuiteName}'";
            throw new InvalidOperationException($"Test '{options.TestId}' was not found in {scope}.");
        }

        if (string.IsNullOrWhiteSpace(options.SuiteName) && matchedSuites.Count > 1)
        {
            var suites = string.Join(", ", matchedSuites.Select(suite => suite.Name));
            throw new InvalidOperationException(
                $"Test id '{options.TestId}' matched multiple suites ({suites}). Re-run with --suite.");
        }

        return matchedSuites;
    }

    private static string DescribeSelection(HarnessCommandOptions options, IReadOnlyList<HarnessSuite> suites)
    {
        if (options.RunAllSuites && string.IsNullOrWhiteSpace(options.TestId))
            return $"all suites ({suites.Count})";

        if (!string.IsNullOrWhiteSpace(options.TestId) && string.IsNullOrWhiteSpace(options.SuiteName))
            return $"test {options.TestId}";

        if (!string.IsNullOrWhiteSpace(options.TestId))
            return $"suite {options.SuiteName}, test {options.TestId}";

        return $"suite {options.SuiteName}";
    }
}
