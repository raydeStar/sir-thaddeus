using SirThaddeus.Agent.Memory;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that fires background memory writes for the current turn:
/// a structured-fact extraction pass over the user message, and a raw
/// conversation-chunk capture of the assistant draft (when the tool loop
/// produced one). Both are fire-and-forget — the step returns
/// <see cref="StepResult.Continue"/> immediately while the writes run on
/// the thread pool.
///
/// <para>Place this step <b>after</b> <c>PostProcessStep</c> (so the
/// assistant chunk captures the cleaned text) and <b>before</b>
/// <c>ResponseComposerStep</c> (so it runs at all — the composer
/// terminates the pipeline).</para>
///
/// <para>No-op when the extractor is null, when eligibility rules reject
/// the message (short greetings, etc. — enforced inside the extractor),
/// or when the assistant draft is blank.</para>
/// </summary>
public sealed class AutoMemoryExtractStep : ITurnStep
{
    private readonly IAutoMemoryExtractor? _extractor;
    private readonly Func<TurnContext, string?> _activeProfileIdGetter;

    /// <param name="extractor">Background memory writer. Null = no-op.</param>
    /// <param name="activeProfileIdGetter">Optional resolver for the
    /// active personality profile id — extraction scopes facts to the
    /// profile so one user can keep separate "work" vs "personal"
    /// memories. Defaults to null.</param>
    public AutoMemoryExtractStep(
        IAutoMemoryExtractor? extractor,
        Func<TurnContext, string?>? activeProfileIdGetter = null)
    {
        _extractor = extractor;
        _activeProfileIdGetter = activeProfileIdGetter ?? (_ => null);
    }

    public string Name => "AutoMemoryExtract";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_extractor is null)
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var profileId = _activeProfileIdGetter(context);

        // Fire the user-message extractor (eligibility gating lives inside
        // the extractor implementation — short greetings are rejected there).
        if (!string.IsNullOrWhiteSpace(context.UserText))
        {
            _extractor.FireAndForgetExtraction(context.UserText, profileId, context.MessageId);
            _extractor.FireAndForgetConversationChunk(
                context.UserText, context.ThreadId, context.MessageId, role: "user");
        }

        // Capture the assistant reply as a chunk once the tool loop has
        // produced one. Cleanup (sanitizer) has already run in the
        // PostProcess step so the stored chunk matches what the user sees.
        if (!string.IsNullOrWhiteSpace(context.AssistantDraft))
        {
            _extractor.FireAndForgetConversationChunk(
                context.AssistantDraft!, context.ThreadId, context.MessageId, role: "assistant");
        }

        return Task.FromResult<StepResult>(new StepResult.Continue(context));
    }
}
