using SirThaddeus.AuditLog;
using Thaddeus.Runtime.Api;

namespace Thaddeus.Runtime.Tests;

public sealed class AuditInsightsApiTests
{
    [Fact]
    public void Compute_DerivesOutcomeAndInterventionRatesFromAuditEvidence()
    {
        var events = new AuditEvent[]
        {
            Event("CHAT_RUN_PAUSE", "run-1"),
            Event("CHAT_RUN_COMPLETED", "run-1"),
            Event("CHAT_RUN_FAILED", "run-2", "error"),
            Event("TOOL_PERMISSION_DECISION", "wiki.update", details: new()
            {
                ["latencyMs"] = 450L,
                ["decision"] = "once",
            }),
        };

        var result = AuditInsightsApi.Compute(events);

        var completion = Assert.Single(result.Metrics, metric => metric.Key == "task-completion");
        Assert.Equal("measured", completion.Status);
        Assert.Equal(0.5, completion.Value);
        var intervention = Assert.Single(result.Metrics, metric => metric.Key == "human-intervention");
        Assert.Equal(0.5, intervention.Value);
        var fatigue = Assert.Single(result.Metrics, metric => metric.Key == "approval-fatigue");
        Assert.Equal(1d, fatigue.Value);
    }

    [Fact]
    public void Compute_LeavesTrustCalibrationWithoutFeedbackExplicitlyUnavailable()
    {
        var result = AuditInsightsApi.Compute([]);

        var trust = Assert.Single(result.Metrics, metric => metric.Key == "trust-calibration");
        Assert.Equal("insufficient-data", trust.Status);
        Assert.Null(trust.Value);
        Assert.Contains("no proxy", trust.Definition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compute_CalibratesDisplayedConfidenceAgainstUserOutcome()
    {
        var result = AuditInsightsApi.Compute([
            Event("ASSISTANT_OUTCOME_FEEDBACK", "message-1", details: new()
            {
                ["confidence"] = 0.8d,
                ["actualSuccess"] = true,
            }),
            Event("ASSISTANT_OUTCOME_FEEDBACK", "message-2", "correction", new()
            {
                ["confidence"] = 0.8d,
                ["actualSuccess"] = false,
            }),
        ]);

        var trust = Assert.Single(result.Metrics, metric => metric.Key == "trust-calibration");
        Assert.Equal("measured", trust.Status);
        Assert.Equal(0.5d, trust.Value!.Value, precision: 6);
    }

    private static AuditEvent Event(
        string action,
        string target,
        string result = "ok",
        Dictionary<string, object>? details = null) =>
        new()
        {
            Actor = "test",
            Action = action,
            Target = target,
            Result = result,
            Details = details,
        };
}
