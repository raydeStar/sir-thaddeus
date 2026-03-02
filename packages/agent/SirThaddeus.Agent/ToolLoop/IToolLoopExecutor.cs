using SirThaddeus.Agent.Orchestration.Correlation;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.ToolLoop;

/// <summary>
/// Input for tool-loop execution.
/// </summary>
public sealed record ToolLoopExecutionRequest
{
    public required List<ChatMessage> History { get; init; }
    public required IReadOnlyList<ToolDefinition> Tools { get; init; }
    public required List<ToolCallRecord> ToolCallsMade { get; init; }
    public required int InitialRoundTrips { get; init; }
    public required Orchestration.IntentDecisionV2 Decision { get; init; }
    public int MaxRoundTrips { get; init; } = 10;
    public required Func<string, string> SanitizeAssistantText { get; init; }
    public Action<string, string>? LogEvent { get; init; }
    public IReadOnlyList<PersonalityEngine.Profiles.PersonalityFewShotExample>? FewShotExamples { get; init; }

    /// <summary>
    /// Optional run context for completion-aware execution.
    /// When set, <see cref="CompletionAwareToolLoopExecutor"/> will check
    /// tool results against completion contracts and attempt bounded repair.
    /// When null, completion checking is skipped (backward-compatible).
    /// </summary>
    public RunContext? RunContext { get; init; }
}

/// <summary>
/// Executes policy-filtered tool loops. Must never widen tool availability.
/// </summary>
public interface IToolLoopExecutor
{
    Task<AgentResponse> ExecuteAsync(
        ToolLoopExecutionRequest request,
        CancellationToken cancellationToken = default);
}

