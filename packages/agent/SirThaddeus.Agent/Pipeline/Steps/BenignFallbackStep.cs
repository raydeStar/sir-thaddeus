using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that short-circuits trivial benign prompts (hand-rolled
/// greetings-plus-questions, classic-reasoning follow-ups, etc.) with a
/// deterministic reply — no LLM, no tools. Delegates detection to
/// <see cref="OrchestratorMessageHelpers.TryBuildEarlyDeterministicBenignFallback"/>
/// (the legacy orchestrator's early fallback) so UI + CLI + the harness
/// all emit the same canned text for the same inputs.
///
/// <para>Defensive gates (mirror the legacy orchestrator): the step is
/// only allowed to fire when the user message doesn't look like an
/// explicit tool invocation, a web/screen/file/system/browse request.
/// That keeps benign detection from stealing legitimate tool-use turns
/// that happen to phrase themselves casually.</para>
///
/// <para>Place this step AFTER <see cref="SafetyBoundaryStep"/> and
/// AFTER <see cref="UtilityFastPathStep"/> (both have stricter claims on
/// the turn), and BEFORE <see cref="FeatureExtractorStep"/> is
/// strictly needed — this step reuses feature extraction helpers
/// directly since it needs them <em>before</em> other steps run. When
/// <see cref="TurnContext.Features"/> has already been populated by an
/// upstream step, the pre-cached values are used; otherwise the step
/// calls the helpers itself. Both cases are O(1) on a tiny input.</para>
/// </summary>
public sealed class BenignFallbackStep : ITurnStep
{
    public string Name => "BenignFallback";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var userText = context.UserText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userText))
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var lower = userText.Trim().ToLowerInvariant();

        // Defensive gates: if the message looks like it wants a tool,
        // let the tool loop handle it and don't stamp a benign reply on
        // top. Covers explicit invocation ("call X"), web search shapes,
        // screen / file / system / browser requests.
        if (IntentFeatureExtractor.LooksLikeExplicitToolInvocation(lower) ||
            IntentFeatureExtractor.LooksLikeWebSearchRequest(lower) ||
            IntentFeatureExtractor.LooksLikeScreenRequest(lower) ||
            IntentFeatureExtractor.LooksLikeFileRequest(lower) ||
            IntentFeatureExtractor.LooksLikeSystemCommand(lower) ||
            IntentFeatureExtractor.LooksLikeBrowseRequest(lower))
        {
            return Task.FromResult<StepResult>(new StepResult.Continue(context));
        }

        var reply = OrchestratorMessageHelpers.TryBuildEarlyDeterministicBenignFallback(userText);
        if (string.IsNullOrEmpty(reply))
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var response = new AgentResponse
        {
            Text = reply,
            Success = true,
            ToolCallsMade = [],
            LlmRoundTrips = 0,
        };
        return Task.FromResult<StepResult>(new StepResult.Terminate(response));
    }
}
