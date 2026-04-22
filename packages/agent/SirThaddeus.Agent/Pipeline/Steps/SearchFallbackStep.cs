using SirThaddeus.Agent.Search;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that replaces a weak or refusal-shaped assistant draft
/// with a fresh search-backed response from
/// <see cref="ISearchFallbackExecutor"/>. Matches the orchestrator's
/// fallback path: when the primary tool loop produced something like "I
/// don't know" or "I can't access that" but a web lookup would actually
/// answer the question, retry via the search orchestrator before the
/// composer emits the response.
///
/// <para>Trigger logic lives in the supplied <c>buildRequest</c> delegate
/// so the step stays lean. The delegate returns a
/// <see cref="SearchFallbackRequest"/> when it wants the fallback to run,
/// or <c>null</c> to pass through. Returning null is the common case;
/// most turns don't need a fallback.</para>
///
/// <para>Place this step <b>after</b> <c>PostProcessStep</c> (operates on
/// the cleaned draft) and <b>before</b> <c>AutoMemoryExtractStep</c> (so
/// any memory capture sees the final text that reached the user).</para>
///
/// <para><b>Fail-open</b>: if the executor throws or returns an empty
/// response, the original draft is preserved.</para>
/// </summary>
public sealed class SearchFallbackStep : ITurnStep
{
    private readonly ISearchFallbackExecutor? _executor;
    private readonly Func<TurnContext, SearchFallbackRequest?> _buildRequest;

    public SearchFallbackStep(
        ISearchFallbackExecutor? executor,
        Func<TurnContext, SearchFallbackRequest?> buildRequest)
    {
        _executor = executor;
        _buildRequest = buildRequest ?? throw new ArgumentNullException(nameof(buildRequest));
    }

    public string Name => "SearchFallback";

    public async Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_executor is null)
            return new StepResult.Continue(context);

        var request = _buildRequest(context);
        if (request is null)
            return new StepResult.Continue(context);

        AgentResponse fallback;
        try
        {
            fallback = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fallback is opportunistic. Transport failures, rate limits,
            // empty result sets — none of them should abort the turn. The
            // user keeps the original draft.
            return new StepResult.Continue(context);
        }

        if (string.IsNullOrWhiteSpace(fallback.Text))
            return new StepResult.Continue(context);

        // Adopt the fallback's text + any additional tool calls it ran.
        // Merge tool call records so the UI activity log shows both the
        // original attempts and the fallback's work.
        var mergedCalls = context.ToolCallsMade.Count == 0 && fallback.ToolCallsMade.Count == 0
            ? context.ToolCallsMade
            : context.ToolCallsMade.Concat(fallback.ToolCallsMade).ToArray();

        return new StepResult.Continue(context with
        {
            AssistantDraft = fallback.Text,
            ToolCallsMade = mergedCalls,
        });
    }
}
