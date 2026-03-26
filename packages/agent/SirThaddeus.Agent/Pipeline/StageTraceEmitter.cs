using System.Diagnostics;
using System.Text.Json;

namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Records stage-level trace data as a pipeline runs. Emits trace
/// envelopes compatible with the harness artifact format (steps.jsonl).
/// Each trace captures the stage name, timing, decision summary, and
/// I/O summaries for diagnostics.
/// </summary>
public sealed class StageTraceEmitter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _correlationId;
    private readonly List<StageTraceEnvelope> _stages = [];
    private readonly DateTimeOffset _pipelineStart;
    private Stopwatch? _currentStageTimer;
    private PipelineStageName _currentStage;
    private string _currentInput = "";

    public StageTraceEmitter(string? correlationId = null)
    {
        _correlationId = correlationId ?? Guid.NewGuid().ToString("N")[..12];
        _pipelineStart = DateTimeOffset.UtcNow;
    }

    public string CorrelationId => _correlationId;

    public void BeginStage(PipelineStageName stage, string inputSummary = "")
    {
        _currentStage = stage;
        _currentInput = inputSummary;
        _currentStageTimer = Stopwatch.StartNew();
    }

    public void EndStage(string decision, string outputSummary = "", IReadOnlyList<string>? warnings = null)
    {
        _currentStageTimer?.Stop();
        var elapsed = _currentStageTimer?.ElapsedMilliseconds ?? 0;

        _stages.Add(new StageTraceEnvelope
        {
            CorrelationId = _correlationId,
            Stage = _currentStage,
            StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-elapsed),
            EndedAt = DateTimeOffset.UtcNow,
            DurationMs = elapsed,
            Decision = decision,
            InputSummary = _currentInput,
            OutputSummary = outputSummary,
            Warnings = warnings ?? []
        });

        _currentStageTimer = null;
    }

    public PipelineTrace BuildTrace(string originalMessage)
    {
        return new PipelineTrace
        {
            CorrelationId = _correlationId,
            OriginalMessage = originalMessage,
            StartedAt = _pipelineStart,
            EndedAt = DateTimeOffset.UtcNow,
            TotalDurationMs = (long)(DateTimeOffset.UtcNow - _pipelineStart).TotalMilliseconds,
            Stages = _stages.ToList()
        };
    }

    // ────────────────────────────────────────────────────────────────
    // Convenience: record entire preprocessor stage
    // ────────────────────────────────────────────────────────────────

    public void RecordPreprocess(PreprocessorResult result, long durationMs)
    {
        var intentList = string.Join(", ", result.Intents.Select(i =>
            $"[{i.Order}] \"{Truncate(i.NormalizedRequest, 60)}\""));

        _stages.Add(new StageTraceEnvelope
        {
            CorrelationId = _correlationId,
            Stage = PipelineStageName.Preprocess,
            StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-durationMs),
            EndedAt = DateTimeOffset.UtcNow,
            DurationMs = durationMs,
            Decision = result.IsMultiIntent ? "multi_intent" : "single_intent",
            InputSummary = $"{result.Intents.Count} intent(s)",
            OutputSummary = intentList
        });
    }

    public void RecordClassify(ClassifierResult result, long durationMs)
    {
        var routeSummary = string.Join(", ", result.ClassifiedIntents.Select(ci =>
            $"{ci.ResolvedIntent} ({ci.MappedType}, conf={ci.Confidence:0.00})"));

        _stages.Add(new StageTraceEnvelope
        {
            CorrelationId = _correlationId,
            Stage = PipelineStageName.Classify,
            StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-durationMs),
            EndedAt = DateTimeOffset.UtcNow,
            DurationMs = durationMs,
            Decision = result.AllDeterministic ? "deterministic" : "llm_assisted",
            InputSummary = $"{result.ClassifiedIntents.Count} intent(s)",
            OutputSummary = routeSummary
        });
    }

    public void RecordQueryBuild(QueryBuilderResult result, long durationMs)
    {
        var querySummary = string.Join(", ", result.Queries.Select(q =>
        {
            if (!string.IsNullOrWhiteSpace(q.SearchQuery))
                return $"search:\"{Truncate(q.SearchQuery, 60)}\"";
            if (q.PlannedTools.Count > 0)
                return $"tools:[{string.Join(",", q.PlannedTools.Select(t => t.ToolName))}]";
            if (!string.IsNullOrWhiteSpace(q.InlineAnswer))
                return "inline";
            return "deferred";
        }));

        _stages.Add(new StageTraceEnvelope
        {
            CorrelationId = _correlationId,
            Stage = PipelineStageName.QueryBuild,
            StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-durationMs),
            EndedAt = DateTimeOffset.UtcNow,
            DurationMs = durationMs,
            Decision = $"{result.Queries.Count} queries built",
            InputSummary = $"{result.Queries.Count} classified intent(s)",
            OutputSummary = querySummary
        });
    }

    public void RecordExecute(ExecutorResult result, long durationMs)
    {
        var succeeded = result.Segments.Count(s => s.Success);
        var failed = result.Segments.Count - succeeded;

        var warnings = new List<string>();
        foreach (var seg in result.Segments.Where(s => !s.Success))
        {
            warnings.Add($"Failed: {Truncate(seg.Error, 100)}");
        }

        _stages.Add(new StageTraceEnvelope
        {
            CorrelationId = _correlationId,
            Stage = PipelineStageName.Execute,
            StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-durationMs),
            EndedAt = DateTimeOffset.UtcNow,
            DurationMs = durationMs,
            Decision = $"{succeeded} ok, {failed} failed",
            InputSummary = $"{result.Segments.Count} queries",
            OutputSummary = $"Total response chars: {result.Segments.Sum(s => s.ResponseText.Length)}",
            Warnings = warnings
        });
    }

    public void RecordCompose(ComposerResult result, long durationMs)
    {
        _stages.Add(new StageTraceEnvelope
        {
            CorrelationId = _correlationId,
            Stage = PipelineStageName.Compose,
            StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-durationMs),
            EndedAt = DateTimeOffset.UtcNow,
            DurationMs = durationMs,
            Decision = result.WasSanitized ? "sanitized" : "clean",
            InputSummary = "executed segments",
            OutputSummary = $"{result.FinalResponse.Length} chars",
            Warnings = result.Warnings.ToList()
        });
    }

    // ────────────────────────────────────────────────────────────────
    // Serialization for artifact output
    // ────────────────────────────────────────────────────────────────

    public string ToJsonLines()
    {
        var lines = _stages.Select(s => JsonSerializer.Serialize(s, JsonOptions));
        return string.Join('\n', lines);
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(BuildTrace(""), JsonOptions);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }
}
