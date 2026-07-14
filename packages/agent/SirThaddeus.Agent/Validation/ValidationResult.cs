namespace SirThaddeus.Agent.Validation;

/// <summary>
/// Result of a post-execution validation pass that checks whether
/// the model's output actually answered the user's request.
/// </summary>
public sealed record CompletionValidationResult
{
    /// <summary>True if the response adequately answers the user's request.</summary>
    public required bool Passed { get; init; }

    /// <summary>True if the response should be regenerated via the repair loop.</summary>
    public bool RepairNeeded { get; init; }

    /// <summary>Description of what is missing or wrong (null when passed).</summary>
    public string? MissingElement { get; init; }

    /// <summary>Suggested repair action (null when passed).</summary>
    public string? SuggestedRepair { get; init; }

    /// <summary>Wall-clock milliseconds the validation took.</summary>
    public double ElapsedMs { get; init; }

    /// <summary>True when validation required the helper LLM rather than a deterministic result.</summary>
    public bool UsedLlm { get; init; }
}
