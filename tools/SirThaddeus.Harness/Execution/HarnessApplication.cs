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

        await using var headlessClient = new HeadlessRuntimeHarnessClient(settings);

        foreach (var suite in selectedSuites)
        {
            Console.WriteLine($"== Suite: {suite.Name} ({suite.Tests.Count} test(s))");

            var context = new SuiteRunContext
            {
                Options = options,
                SuiteName = suite.Name,
                RunId = runId,
                HeadlessClient = headlessClient
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
                        JudgeResult = single.JudgeResult
                    };
                }

                var attempts = await _autoIterationEngine.ExecuteAsync(
                    options,
                    test,
                    RunIterationAsync,
                    cancellationToken);

                var best = attempts.OrderByDescending(a => a.Score.FinalScore).First();
                var minScore = options.MinScoreOverride ?? test.MinScore;
                var passed = best.Score.HardPass && best.Score.FinalScore >= minScore;

                if (passed)
                    passCount++;
                else
                    failCount++;

                var resultLabel = passed ? "PASS" : "FAIL";
                Console.WriteLine($"Result: {resultLabel} | score={best.Score.FinalScore:0.00} | min={minScore:0.00}");
                Console.WriteLine($"Attempts: {attempts.Count}");
                Console.WriteLine($"Artifacts: {best.ArtifactDirectory}");
                Console.WriteLine();

                summaries.Add(BuildSummaryLine(suite.Name, test, best, passed, minScore));
                suiteScoreCards.Add(best.Score);

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
                    FinalResponse = best.FinalResponse
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

        Console.WriteLine("== Run Summary");
        foreach (var line in summaries)
            Console.WriteLine(line);
        Console.WriteLine();
        Console.WriteLine($"Passed: {passCount}");
        Console.WriteLine($"Failed: {failCount}");

        return failCount == 0 ? 0 : 1;
    }

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
