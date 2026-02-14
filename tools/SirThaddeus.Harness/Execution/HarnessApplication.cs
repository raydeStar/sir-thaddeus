using System.Text;
using SirThaddeus.Harness.Artifacts;
using SirThaddeus.Harness.Cli;
using SirThaddeus.Harness.Iteration;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Scoring;
using SirThaddeus.Harness.Suites;

namespace SirThaddeus.Harness.Execution;

public sealed class HarnessApplication
{
    private readonly SuiteLoader _suiteLoader = new();
    private readonly HarnessArtifactWriter _artifactWriter = new();
    private readonly FixtureStore _fixtureStore = new();
    private readonly ScoringEngine _scoringEngine = new();
    private readonly CursorJudgeClient _judgeClient = new();
    private readonly AutoIterationEngine _autoIterationEngine = new(new WorkspacePatchApplier());

    public async Task<int> RunAsync(HarnessCommandOptions options, CancellationToken cancellationToken)
    {
        var suite = _suiteLoader.LoadSuite(options.SuitesRoot, options.SuiteName);
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");

        Console.WriteLine($"Harness command: {options.Command}");
        Console.WriteLine($"Suite: {suite.Name}");
        Console.WriteLine($"Mode: {options.Mode}");
        Console.WriteLine($"RunId: {runId}");
        Console.WriteLine();

        var passCount = 0;
        var failCount = 0;
        var summaries = new List<string>();

        foreach (var test in suite.Tests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"== Test: {test.Id} - {test.Name}");

            var context = new SuiteRunContext
            {
                Options = options,
                SuiteName = suite.Name,
                RunId = runId,
                ShouldRecordFixtures = options.Command == HarnessCommandKind.Record
            };

            var runner = new SingleTestRunner(
                context,
                _artifactWriter,
                _fixtureStore,
                _scoringEngine,
                _judgeClient);

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

            summaries.Add(BuildSummaryLine(test, best, passed, minScore));
        }

        Console.WriteLine("== Run Summary");
        foreach (var line in summaries)
            Console.WriteLine(line);
        Console.WriteLine();
        Console.WriteLine($"Passed: {passCount}");
        Console.WriteLine($"Failed: {failCount}");

        return failCount == 0 ? 0 : 1;
    }

    private static string BuildSummaryLine(
        HarnessTestCase test,
        TestAttemptResult best,
        bool passed,
        double minScore)
    {
        var status = passed ? "[PASS]" : "[FAIL]";
        var builder = new StringBuilder();
        builder.Append(status).Append(' ')
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
}
