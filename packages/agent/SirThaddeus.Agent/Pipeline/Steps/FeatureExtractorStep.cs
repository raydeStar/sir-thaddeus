using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that extracts deterministic routing signals from the
/// user message and attaches them to the <see cref="TurnContext"/>. This is
/// typically the first behavior step in a pipeline — later steps (footman
/// fast-path, utility router, mode injection) read features rather than
/// re-running string matching.
///
/// <para>Stateless + pure — the same input always produces the same
/// features. Safe to run on a hot code path.</para>
///
/// <para>Idempotent: if <see cref="TurnContext.Features"/> has already been
/// set by an earlier step, the call is a no-op. That lets a facade
/// pre-seed features (e.g. for replay / test scenarios) without this step
/// clobbering them.</para>
/// </summary>
public sealed class FeatureExtractorStep : ITurnStep
{
    public string Name => "FeatureExtractor";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // Already populated — don't overwrite what a caller set deliberately.
        if (context.Features is not null)
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var features = RoutingFeatures.Extract(context.UserText ?? string.Empty);
        return Task.FromResult<StepResult>(new StepResult.Continue(context with { Features = features }));
    }
}
