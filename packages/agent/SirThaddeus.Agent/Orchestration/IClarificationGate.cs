namespace SirThaddeus.Agent.Orchestration;

/// <summary>
/// A single succinct question to ask the user, returned by the ClarificationGate.
/// </summary>
public sealed record ClarificationResponse(string Question);

/// <summary>
/// Gate that intercepts low-confidence or missing-slot routing decisions and requests
/// user clarification instead of guessing incorrectly.
/// </summary>
public interface IClarificationGate
{
    /// <summary>
    /// Evaluates the decision. Returns a <see cref="ClarificationResponse"/> if the decision
    /// does not meet the necessary threshold or is missing required slots.
    /// Returns null if the decision is clear and safe to proceed.
    /// </summary>
    ClarificationResponse? TryClarify(IntentDecisionV2 decision);
}
