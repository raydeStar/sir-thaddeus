namespace SirThaddeus.Agent.Validation;

/// <summary>
/// Tracks one repair attempt made after a completion validation failure.
/// </summary>
public sealed record RepairAttempt
{
    /// <summary>1-based attempt number.</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>The validation failure that triggered this repair.</summary>
    public required string FailureReason { get; init; }

    /// <summary>The repair prompt sent to the LLM.</summary>
    public required string RepairPrompt { get; init; }

    /// <summary>The LLM's repaired response text (null if the repair call failed).</summary>
    public string? RepairedText { get; init; }

    /// <summary>True if the repaired response passed validation.</summary>
    public bool RepairSucceeded { get; init; }

    /// <summary>Wall-clock milliseconds the repair attempt took.</summary>
    public double ElapsedMs { get; init; }
}
