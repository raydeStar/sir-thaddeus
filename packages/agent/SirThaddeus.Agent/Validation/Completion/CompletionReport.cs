namespace SirThaddeus.Agent.Validation.Completion;

/// <summary>
/// The result of checking tool execution results against a
/// <see cref="CompletionContract"/>. Tells the orchestrator whether
/// the response is complete or what's missing.
/// </summary>
public sealed record CompletionReport
{
    /// <summary>
    /// Whether all required fields and evidence requirements are satisfied.
    /// When false, <see cref="MissingFields"/> and <see cref="Issues"/>
    /// describe what's lacking.
    /// </summary>
    public bool IsComplete { get; init; }

    /// <summary>
    /// Names of required fields that were not found in the tool results.
    /// Empty when <see cref="IsComplete"/> is true.
    /// </summary>
    public IReadOnlyList<string> MissingFields { get; init; } = [];

    /// <summary>
    /// Names of optional fields that were not found.
    /// Informational only — doesn't affect <see cref="IsComplete"/>.
    /// </summary>
    public IReadOnlyList<string> MissingOptionalFields { get; init; } = [];

    /// <summary>
    /// Human-readable issue descriptions (e.g. "no source URLs found",
    /// "only 1 of 3 required items returned").
    /// </summary>
    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>
    /// Number of result items found (for list-type contracts).
    /// </summary>
    public int ItemCount { get; init; }

    /// <summary>
    /// Deterministic confidence score in range [0, 1] derived from
    /// required-field coverage, evidence coverage, and item coverage.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Short reason for finalization state (for diagnostics/audit).
    /// </summary>
    public string StopReason { get; init; } = "unknown";

    /// <summary>
    /// The contract that was evaluated against.
    /// </summary>
    public required CompletionContract Contract { get; init; }

    /// <summary>
    /// Factory for a fully satisfied report.
    /// </summary>
    public static CompletionReport Satisfied(CompletionContract contract, int itemCount = 0) => new()
    {
        IsComplete = true,
        Contract = contract,
        ItemCount = itemCount,
        Confidence = 1.0,
        StopReason = "complete"
    };

    /// <summary>
    /// Factory for the always-satisfied null contract.
    /// </summary>
    public static readonly CompletionReport AlwaysSatisfied = Satisfied(CompletionContract.AlwaysSatisfied);
}
