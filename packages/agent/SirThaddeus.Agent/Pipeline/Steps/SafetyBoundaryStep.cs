namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that short-circuits turns requesting high-risk illicit
/// instructions (bypass security, cause harm, etc.) with a deterministic
/// safety response + redirect. Matches the legacy orchestrator's safety
/// boundary check: same detector (<see cref="OrchestratorMessageHelpers.LooksLikeHighRiskIllicitInstructionRequest"/>),
/// same canned reply (<see cref="OrchestratorMessageHelpers.BuildSafetyBoundaryWithAlternativeReply"/>).
///
/// <para>Place this step <b>very early</b> in the pipeline — before the
/// LLM is touched, before memory is fetched, before any tool call. The
/// detector is a cheap regex over the user message, so the check cost is
/// negligible. When it fires, the pipeline terminates with the canned
/// reply and skips every downstream step.</para>
///
/// <para>No-op on messages that don't trigger the detector — the vast
/// majority of turns pass straight through.</para>
/// </summary>
public sealed class SafetyBoundaryStep : ITurnStep
{
    public string Name => "SafetyBoundary";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OrchestratorMessageHelpers.LooksLikeHighRiskIllicitInstructionRequest(context.UserText))
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var response = new AgentResponse
        {
            Text = OrchestratorMessageHelpers.BuildSafetyBoundaryWithAlternativeReply(),
            Success = true,
            ToolCallsMade = [],
            LlmRoundTrips = 0,
        };
        return Task.FromResult<StepResult>(new StepResult.Terminate(response));
    }
}
