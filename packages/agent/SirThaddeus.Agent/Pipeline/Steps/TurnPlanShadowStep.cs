using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Compiles a deterministic TurnPlan and records it for comparison while the
/// existing pipeline remains authoritative. The step is present only when
/// ST_TURN_PLAN_SHADOW is enabled and never changes execution behavior.
/// </summary>
public sealed class TurnPlanShadowStep : ITurnStep
{
    private readonly Action<string, string>? _logEvent;

    public TurnPlanShadowStep(Action<string, string>? logEvent = null)
    {
        _logEvent = logEvent;
    }

    public string Name => "TurnPlanShadow";

    public static bool IsEnabled => IsTruthy(Environment.GetEnvironmentVariable("ST_TURN_PLAN_SHADOW"));

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsEnabled)
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var plan = TurnPlanCompiler.Compile(new TurnPlanningInput
        {
            UserText = context.UserText,
            Features = context.Features
        });

        var capabilities = string.Join(',', new[]
        {
            plan.DynamicMemoryRequired ? "dynamic_memory" : null,
            plan.FreshnessRequired ? "freshness" : null,
            plan.ToolsRequired ? "tools" : null,
            plan.FilesOrUrlsRequired ? "files_or_urls" : null,
            plan.DeepReasoningRequired ? "deep_reasoning" : null,
            plan.HighStakesHandlingRequired ? "high_stakes" : null,
            plan.StructuredResponseRequired ? "structured_response" : null,
            plan.BackgroundPersistenceRequired ? "background_persistence" : null
        }.Where(value => value is not null));
        var reasons = string.Join(',', plan.Reasons.Select(reason => $"{reason.Capability}:{reason.Code}"));

        _logEvent?.Invoke(
            "TURN_PLAN_SHADOW",
            $"thread_id={context.ThreadId} turn_id={context.MessageId} " +
            $"kind={plan.PrimaryKind} confidence={plan.Confidence:0.00} " +
            $"full_path={plan.RequiresExistingFullPath} capabilities={capabilities} reasons={reasons}");

        return Task.FromResult<StepResult>(new StepResult.Continue(context));
    }

    private static bool IsTruthy(string? raw) =>
        string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase);
}
