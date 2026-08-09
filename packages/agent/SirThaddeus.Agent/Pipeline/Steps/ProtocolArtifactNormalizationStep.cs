namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Applies scorer-blind malformed protocol cleanup after ordinary sanitization
/// and before completion validation or final response composition.
/// </summary>
public sealed class ProtocolArtifactNormalizationStep : ITurnStep
{
    private readonly string _name;
    private readonly Action<string, string>? _log;

    public ProtocolArtifactNormalizationStep(
        string name = "PostProcess:ProtocolArtifactNormalize",
        Action<string, string>? log = null)
    {
        _name = string.IsNullOrWhiteSpace(name)
            ? "PostProcess:ProtocolArtifactNormalize"
            : name.Trim();
        _log = log;
    }

    public string Name => _name;

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var result = ProtocolArtifactNormalizer.Normalize(
            context.AssistantDraft,
            context.WikiMutationTarget,
            context.ToolCallsMade);
        LogActivation(context, result);
        if (!result.Applied)
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        return Task.FromResult<StepResult>(
            new StepResult.Continue(context with { AssistantDraft = result.Text }));
    }

    private void LogActivation(
        TurnContext context,
        ProtocolArtifactNormalizationResult result)
    {
        if (!IsLatencyTracingEnabled() || _log is null)
            return;

        _log(
            "EXPERIMENT_ACTIVATION",
            $"thread_id={context.ThreadId} turn_id={context.MessageId} " +
            "event=protocol_artifact_normalization " +
            $"decision={(result.Applied ? "activated" : "inactive")} reason={result.Reason}");
    }

    private static bool IsLatencyTracingEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase);
    }
}
