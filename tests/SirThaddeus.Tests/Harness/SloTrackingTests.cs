using SirThaddeus.Harness.Governance;
using Xunit;

namespace SirThaddeus.Tests;

public class SloTrackingTests
{
    [Fact]
    public void Evaluate_AllGreen_NoViolations()
    {
        var snapshot = new SloSnapshot
        {
            TotalTests = 100,
            Passed = 98,
            Failed = 2,
            MeanDurationMs = 5000
        };

        var violations = SloEvaluator.Evaluate(snapshot);
        Assert.Empty(violations);
    }

    [Fact]
    public void Evaluate_LowPassRate_ReportsViolation()
    {
        var snapshot = new SloSnapshot
        {
            TotalTests = 100,
            Passed = 80,
            Failed = 20,
            MeanDurationMs = 5000
        };

        var violations = SloEvaluator.Evaluate(snapshot);
        Assert.Single(violations);
        Assert.Contains("pass rate", violations[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_HighFlakyRate_ReportsViolation()
    {
        var snapshot = new SloSnapshot
        {
            TotalTests = 100,
            Passed = 97,
            Failed = 3,
            MeanDurationMs = 5000,
            FlakyTests = ["test1", "test2", "test3"] // 3% flaky
        };

        var violations = SloEvaluator.Evaluate(snapshot);
        Assert.Contains(violations, v => v.Contains("Flaky", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_SlowTests_ReportsViolation()
    {
        var snapshot = new SloSnapshot
        {
            TotalTests = 50,
            Passed = 50,
            MeanDurationMs = 45_000 // 45 seconds
        };

        var violations = SloEvaluator.Evaluate(snapshot);
        Assert.Contains(violations, v => v.Contains("duration", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tracker_RecordAndSummarize()
    {
        var path = Path.Combine(Path.GetTempPath(), $"slo-test-{Guid.NewGuid():N}.json");
        try
        {
            var tracker = new SloTracker(path);
            tracker.Record(new SloSnapshot
            {
                RunId = "run-1",
                TotalTests = 100,
                Passed = 95,
                Failed = 5,
                MeanDurationMs = 3000,
                StageFailures = new Dictionary<string, int> { ["preprocess"] = 2, ["classify"] = 3 }
            });
            tracker.Record(new SloSnapshot
            {
                RunId = "run-2",
                TotalTests = 100,
                Passed = 98,
                Failed = 2,
                MeanDurationMs = 2500,
                StageFailures = new Dictionary<string, int> { ["classify"] = 1, ["execute"] = 1 }
            });

            Assert.Equal(2, tracker.History.Count);

            var summary = tracker.Summarize();
            Assert.Equal(2, summary.TotalRuns);
            Assert.True(summary.MeanPassRate > 0.9);
            Assert.True(summary.StageFailureDistribution.ContainsKey("classify"));
            Assert.Equal(4, summary.StageFailureDistribution["classify"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Tracker_PersistsAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"slo-persist-{Guid.NewGuid():N}.json");
        try
        {
            var tracker1 = new SloTracker(path);
            tracker1.Record(new SloSnapshot
            {
                RunId = "run-1",
                TotalTests = 50,
                Passed = 50,
                MeanDurationMs = 1000
            });

            var tracker2 = new SloTracker(path);
            Assert.Single(tracker2.History);
            Assert.Equal("run-1", tracker2.History[0].RunId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Tracker_EmptyHistory_SummarizesGracefully()
    {
        var path = Path.Combine(Path.GetTempPath(), $"slo-empty-{Guid.NewGuid():N}.json");
        try
        {
            var tracker = new SloTracker(path);
            var summary = tracker.Summarize();
            Assert.Equal(0, summary.TotalRuns);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
