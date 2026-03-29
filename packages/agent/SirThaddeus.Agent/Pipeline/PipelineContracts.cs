using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Agent.Pipeline;

// ────────────────────────────────────────────────────────────────
// Enums
// ────────────────────────────────────────────────────────────────

public enum PipelineIntentType
{
    Chat,
    WebSearch,
    FileRead,
    FileWrite,
    CodeExecution,
    McpCall,
    Summarize,
    Unknown
}

public enum PipelineStageName
{
    Preprocess,
    Classify,
    QueryBuild,
    Execute,
    Compose
}

// ────────────────────────────────────────────────────────────────
// Preprocessor (Phase 1B)
// ────────────────────────────────────────────────────────────────

public sealed record PipelineIntent
{
    public required string OriginalFragment { get; init; }
    public required string NormalizedRequest { get; init; }
    public PipelineIntentType Type { get; init; } = PipelineIntentType.Unknown;
    public int Order { get; init; }
    public double Confidence { get; init; } = 0.5;
}

public sealed record PreprocessorResult
{
    public IReadOnlyList<PipelineIntent> Intents { get; init; } = [];
    public bool IsMultiIntent { get; init; }
}

public interface IRequestPreprocessor
{
    PreprocessorResult Decompose(string userMessage);
}

// ────────────────────────────────────────────────────────────────
// Classifier (Phase 1C)
// ────────────────────────────────────────────────────────────────

public sealed record ClassifiedIntent
{
    public required PipelineIntent Source { get; init; }
    public required string ResolvedIntent { get; init; }
    public RouterOutput? RouterOutput { get; init; }
    public PolicyDecision? Policy { get; init; }
    public PipelineIntentType MappedType { get; init; } = PipelineIntentType.Unknown;
    public double Confidence { get; init; } = 0.5;
}

public sealed record ClassifierResult
{
    public IReadOnlyList<ClassifiedIntent> ClassifiedIntents { get; init; } = [];
    public bool AllDeterministic { get; init; }
}

public sealed record ClassifierContext
{
    public bool HasRecentFirstPrinciplesRationale { get; init; }
    public bool HasRecentSearchResults { get; init; }
}

public interface IRequestClassifier
{
    Task<ClassifierResult> ClassifyAsync(
        PreprocessorResult preprocessed,
        ClassifierContext? context = null,
        CancellationToken cancellationToken = default);
}

// ────────────────────────────────────────────────────────────────
// Query Builder (Phase 1D)
// ────────────────────────────────────────────────────────────────

public sealed record QueryBuilderContext
{
    public string UserCity { get; init; } = "";
    public string UserTimezone { get; init; } = "";
    public string FollowUpAnchor { get; init; } = "";
    public IReadOnlyList<(string Role, string Content)> RecentMessages { get; init; } = [];
    public string CurrentFilePath { get; init; } = "";
}

public sealed record BuiltQuery
{
    public required ClassifiedIntent Source { get; init; }
    public string SearchQuery { get; init; } = "";
    public IReadOnlyList<PipelineToolCallRequest> PlannedTools { get; init; } = [];
    public string InlineAnswer { get; init; } = "";
    public bool RequiresExecution { get; init; }
}

public sealed record QueryBuilderResult
{
    public IReadOnlyList<BuiltQuery> Queries { get; init; } = [];
}

public sealed record PipelineToolCallRequest
{
    public required string ToolName { get; init; }
    public IReadOnlyDictionary<string, object> Parameters { get; init; } = new Dictionary<string, object>();
}

public interface IRequestQueryBuilder
{
    Task<QueryBuilderResult> BuildAsync(
        ClassifierResult classified,
        QueryBuilderContext context,
        CancellationToken cancellationToken = default);
}

// ────────────────────────────────────────────────────────────────
// Executor (Phase 1E)
// ────────────────────────────────────────────────────────────────

public sealed record ExecutionSegmentResult
{
    public required BuiltQuery Source { get; init; }
    public string ResponseText { get; init; } = "";
    public bool Success { get; init; }
    public string Error { get; init; } = "";
    public IReadOnlyList<PipelineToolCallRequest> ToolCallsMade { get; init; } = [];
    public long DurationMs { get; init; }
}

public sealed record ExecutorResult
{
    public IReadOnlyList<ExecutionSegmentResult> Segments { get; init; } = [];
    public bool AllSucceeded => Segments.All(s => s.Success);
}

public interface IRequestExecutor
{
    Task<ExecutorResult> ExecuteAsync(
        QueryBuilderResult queries,
        CancellationToken cancellationToken = default);
}

// ────────────────────────────────────────────────────────────────
// Composer (Phase 1E)
// ────────────────────────────────────────────────────────────────

public sealed record ComposerResult
{
    public string FinalResponse { get; init; } = "";
    public bool WasSanitized { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public interface IResponseComposer
{
    ComposerResult Compose(
        string originalMessage,
        PreprocessorResult preprocessed,
        ExecutorResult executed);
}

// ────────────────────────────────────────────────────────────────
// Stage Trace (Phase 1F)
// ────────────────────────────────────────────────────────────────

public sealed record StageTraceEnvelope
{
    public required string CorrelationId { get; init; }
    public PipelineStageName Stage { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public long DurationMs { get; init; }
    public string Decision { get; init; } = "";
    public string InputSummary { get; init; } = "";
    public string OutputSummary { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record PipelineTrace
{
    public required string CorrelationId { get; init; }
    public required string OriginalMessage { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public long TotalDurationMs { get; init; }
    public IReadOnlyList<StageTraceEnvelope> Stages { get; init; } = [];
}
