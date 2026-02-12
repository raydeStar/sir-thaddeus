namespace SirThaddeus.Agent.Guardrails;

/// <summary>
/// Coordinator-facing shape for first-principles outcomes.
/// </summary>
public sealed record GuardrailsCoordinatorResult
{
    public required string AnswerText { get; init; }
    public IReadOnlyList<string> RationaleLines { get; init; } = [];
    public required string TriggerRisk { get; init; }
    public required string TriggerWhy { get; init; }
    public required string TriggerSource { get; init; }
    public int LlmRoundTrips { get; init; }
}

/// <summary>
/// Coordinates when and how first-principles guardrails run.
/// </summary>
public interface IGuardrailsCoordinator
{
    GuardrailsCoordinatorResult? TryRunDeterministicSpecialCase(string message, string mode);

    Task<GuardrailsCoordinatorResult?> TryRunAsync(
        RouterOutput route,
        string message,
        string mode,
        CancellationToken cancellationToken = default);
}

