using SirThaddeus.Agent;
using SirThaddeus.Harness.Execution;
using SirThaddeus.Harness.Tracing;

namespace SirThaddeus.Tests.Harness;

public sealed class ToolEvidenceTraceEnricherTests
{
    [Fact]
    public void Enrich_ReplacesEachRedactedOccurrenceWithoutChangingTraceShape()
    {
        var trace = (
            ToolCalls: (IReadOnlyList<ToolCallRecord>)
            [
                Call("web_search", "first-redacted"),
                Call("web_search", "second-redacted")
            ],
            ToolTurns: (IReadOnlyList<RecordedToolTurn>)
            [
                Turn(0, "web_search", "first-redacted"),
                Turn(1, "web_search", "second-redacted")
            ],
            Steps: (IReadOnlyList<TraceStep>)
            [
                Step(1, "web_search", "first-redacted"),
                Step(2, "web_search", "second-redacted"),
                new TraceStep { StepIndex = 3, StepType = "final_response", Content = "answer" }
            ]);

        var evidence = new[]
        {
            Call("web_search", "first-full-result", "{\"query\":\"first\"}"),
            Call("web_search", "second-full-result", "{\"query\":\"second\"}")
        };

        var enriched = ToolEvidenceTraceEnricher.Enrich(trace, evidence);

        Assert.Equal(3, enriched.Steps.Count);
        Assert.Equal("first-full-result", enriched.ToolCalls[0].Result);
        Assert.Equal("second-full-result", enriched.ToolTurns[1].ResultText);
        Assert.Contains("first-full-result", enriched.Steps[0].Result, StringComparison.Ordinal);
        Assert.Contains("second-full-result", enriched.Steps[1].Result, StringComparison.Ordinal);
        Assert.Equal("answer", enriched.Steps[2].Content);
    }

    [Fact]
    public void Enrich_LeavesUnmatchedAuditEntriesRedacted()
    {
        var trace = (
            ToolCalls: (IReadOnlyList<ToolCallRecord>)[Call("web_search", "redacted")],
            ToolTurns: (IReadOnlyList<RecordedToolTurn>)[Turn(0, "web_search", "redacted")],
            Steps: (IReadOnlyList<TraceStep>)[Step(1, "web_search", "redacted")]);

        var enriched = ToolEvidenceTraceEnricher.Enrich(
            trace,
            [Call("calculator", "42")]);

        Assert.Equal("redacted", enriched.ToolCalls[0].Result);
        Assert.Equal("redacted", enriched.ToolTurns[0].ResultText);
        Assert.Contains("redacted", enriched.Steps[0].Result, StringComparison.Ordinal);
    }

    private static ToolCallRecord Call(string name, string result, string arguments = "{}") => new()
    {
        ToolName = name,
        Arguments = arguments,
        Result = result,
        Success = true
    };

    private static RecordedToolTurn Turn(int index, string name, string result) => new()
    {
        Index = index,
        ToolName = name,
        ArgumentsJson = "{}",
        ResultText = result,
        Success = true
    };

    private static TraceStep Step(int index, string name, string result) => new()
    {
        StepIndex = index,
        StepType = "tool_result",
        ToolName = name,
        Arguments = "{}",
        Result = ToolResultPayloads.BuildSuccess(result)
    };
}
