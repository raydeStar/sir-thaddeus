namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that cleans up the model's draft reply (strip template
/// tokens, collapse refusal loops, etc.) before a terminal
/// <see cref="ResponseComposerStep"/> wraps it into an
/// <see cref="AgentResponse"/>.
///
/// <para>The actual cleanup rules live in the runtime (or in any future
/// agent-package sanitizer module). This step takes the cleanup as a
/// simple <see cref="Func{TurnContext, String, String}"/> so the agent
/// package stays free of runtime-specific string hacks, and so multiple
/// post-processors can be composed by chaining multiple instances of
/// this step with different sanitizers.</para>
///
/// <para>No-op when <see cref="TurnContext.AssistantDraft"/> is null — the
/// step runs after the tool loop, which always populates a draft on the
/// happy path. If a prior step terminated, this step never runs.</para>
/// </summary>
public sealed class PostProcessStep : ITurnStep
{
    private readonly Func<TurnContext, string, string> _sanitize;
    private readonly string _logicalName;

    /// <param name="sanitize">Pure function: given the current context and
    /// the raw draft, returns the cleaned draft. Must not throw. Must
    /// return a non-null string (use <see cref="string.Empty"/> to
    /// signal "nothing left to show").</param>
    /// <param name="logicalName">Optional name shown in pipeline logs.
    /// Defaults to <c>"PostProcess"</c>. Useful when you compose multiple
    /// post-process steps (e.g. <c>"PostProcess:Sanitize"</c> then
    /// <c>"PostProcess:RefusalCollapse"</c>) for easier diagnostics.</param>
    public PostProcessStep(Func<TurnContext, string, string> sanitize, string? logicalName = null)
    {
        _sanitize = sanitize ?? throw new ArgumentNullException(nameof(sanitize));
        _logicalName = string.IsNullOrWhiteSpace(logicalName) ? "PostProcess" : logicalName.Trim();
    }

    public string Name => _logicalName;

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // Nothing to clean yet — forward untouched so the composer can
        // surface the deterministic empty-reply message.
        if (context.AssistantDraft is null)
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var cleaned = _sanitize(context, context.AssistantDraft) ?? string.Empty;
        if (ReferenceEquals(cleaned, context.AssistantDraft))
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        return Task.FromResult<StepResult>(
            new StepResult.Continue(context with { AssistantDraft = cleaned }));
    }
}
