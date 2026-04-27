namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Terminal pipeline step that converts the accumulated
/// <see cref="TurnContext"/> into an <see cref="AgentResponse"/> for the
/// facade to stream + persist. Always returns
/// <see cref="StepResult.Terminate"/>.
///
/// <para>Reads:</para>
/// <list type="bullet">
///   <item><see cref="TurnContext.AssistantDraft"/> — final text to return.
///         Falls back to a deterministic empty-reply marker if null/blank.</item>
///   <item><see cref="TurnContext.ToolCallsMade"/> — carried through to
///         <see cref="AgentResponse.ToolCallsMade"/>.</item>
/// </list>
///
/// <para>This step exists so post-processing steps can operate on the
/// draft before it becomes a final response, and so the tool-loop step
/// can stay focused on the loop itself rather than response assembly.</para>
/// </summary>
public sealed class ResponseComposerStep : ITurnStep
{
    public string Name => "ResponseComposer";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var text = string.IsNullOrWhiteSpace(context.AssistantDraft)
            ? "(The model returned an empty response.)"
            : context.AssistantDraft!;

        // Extract citation cards from every successful tool result's
        // trailing SOURCES_JSON block (currently emitted by web_search).
        // Merged + de-duped by URL so a follow-up call in the same turn
        // doesn't double-count the same article.
        var sources = SourceCardExtractor.ExtractMerged(
            context.ToolCallsMade
                .Where(call => call.Success)
                .Select(call => call.Result));

        var response = new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = context.ToolCallsMade,
            Sources = sources,
        };

        return Task.FromResult<StepResult>(new StepResult.Terminate(response));
    }
}
