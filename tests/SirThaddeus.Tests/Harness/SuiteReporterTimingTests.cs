using System.Text.Json;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Reporting;

namespace SirThaddeus.Tests;

public sealed class SuiteReporterTimingTests
{
    [Fact]
    public void Json_summary_preserves_latency_breakdown()
    {
        var path = Path.Combine(Path.GetTempPath(), $"st-harness-summary-{Guid.NewGuid():N}.json");
        try
        {
            var result = new SuiteReporter.TestResult
            {
                SuiteName = "latency",
                TestId = "one",
                TestName = "one",
                Score = new ScoreCard { OverallScore = 1, Status = "pass", LatencyMs = 5_000 },
                MinScore = 0.5,
                Passed = true,
                Attempts = 1,
                RuntimeWarmupSeconds = 1,
                ResetSeconds = 1,
                TestWorkSeconds = 3,
                HostTotalSeconds = 5
            };
            var context = new SuiteReporter.ReportContext
            {
                RunId = "timing",
                DurationSeconds = 5.5
            };

            SuiteReporter.WriteJsonSummary([result], context, path);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var timing = doc.RootElement.GetProperty("timing");
            Assert.Equal(1, timing.GetProperty("runtime_warmup_seconds").GetDouble());
            Assert.Equal(1, timing.GetProperty("reset_seconds").GetDouble());
            Assert.Equal(3, timing.GetProperty("test_work_seconds").GetDouble());
            Assert.Equal(5, timing.GetProperty("host_total_seconds").GetDouble());
            Assert.Equal(0.5, timing.GetProperty("harness_overhead_seconds").GetDouble());

            var perTestTiming = doc.RootElement.GetProperty("results")[0].GetProperty("timing");
            Assert.Equal(1_000, perTestTiming.GetProperty("runtime_warmup_ms").GetInt64());
            Assert.Equal(3_000, perTestTiming.GetProperty("test_work_ms").GetInt64());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
