using SirThaddeus.Agent.Dialogue;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that reads the per-conversation dialogue state (topic,
/// location anchor, time scope) from <see cref="IDialogueStateAccessor"/>
/// and appends a compact continuity block to the system prompt so the
/// model can resolve follow-up questions ("what about tomorrow?", "how
/// about there?") against the prior turn without the user having to
/// re-state context.
///
/// <para>Read-only — this step does not write back to the accessor.
/// State updates happen after the tool loop via a future companion
/// <c>DialogueStatePatchStep</c>, which inspects tool results (weather
/// calls that resolved a city, for example) and patches the accessor
/// accordingly.</para>
///
/// <para>No-op when:</para>
/// <list type="bullet">
///   <item>The accessor is null (runtimes that don't persist dialogue state).</item>
///   <item>The stored state has no usable signals (fresh conversation).</item>
/// </list>
///
/// <para>Place this step <b>after</b> <see cref="MemoryContextStep"/> so
/// the continuity block sits next to remembered facts, and <b>before</b>
/// <c>FootmanRouterStep</c> so the gatekeeper sees the context when
/// deciding which tools to expose.</para>
/// </summary>
public sealed class DialogueStateStep : ITurnStep
{
    private readonly IDialogueStateAccessor? _accessor;

    public DialogueStateStep(IDialogueStateAccessor? accessor)
    {
        _accessor = accessor;
    }

    public string Name => "DialogueState";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_accessor is null)
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var state = _accessor.Get(context.ThreadId);
        var block = BuildContinuityBlock(state);
        if (block is null)
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var updated = PromptSuffixAppender.Append(context.LlmMessages, block);
        return Task.FromResult<StepResult>(new StepResult.Continue(context with { LlmMessages = updated }));
    }

    private static string? BuildContinuityBlock(DialogueState state)
    {
        // Only emit the block when at least one usable signal exists —
        // otherwise we'd pollute every turn with an empty marker.
        var hasTopic = !string.IsNullOrWhiteSpace(state.Topic);
        var hasLocation = !string.IsNullOrWhiteSpace(state.LocationName);
        var hasTimeScope = !string.IsNullOrWhiteSpace(state.TimeScope);

        if (!hasTopic && !hasLocation && !hasTimeScope)
            return null;

        var lines = new List<string>(3);
        if (hasTopic) lines.Add($"Topic: {state.Topic.Trim()}");
        if (hasLocation) lines.Add($"Location: {state.LocationName!.Trim()}");
        if (hasTimeScope) lines.Add($"Time scope: {state.TimeScope!.Trim()}");

        // Lead with a clear header so small models recognise the block
        // as prior-turn context, not a new instruction. Matches the
        // shape of MemoryContextStep's [REMEMBERED CONTEXT] block.
        return "\n\n[CONVERSATION CONTEXT]\n" + string.Join("\n", lines) + "\n[/CONVERSATION CONTEXT]";
    }
}
