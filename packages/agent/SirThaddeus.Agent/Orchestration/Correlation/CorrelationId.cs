namespace SirThaddeus.Agent.Orchestration.Correlation;

/// <summary>
/// Lightweight value type wrapping a unique identifier for a single
/// orchestrator turn. Every layer (Route, Validate, Execute, Verify,
/// Repair, Respond) receives this so audit events, logs, and telemetry
/// can be correlated back to a single user request.
/// </summary>
public readonly record struct CorrelationId
{
    /// <summary>The raw string value (12-char hex by default).</summary>
    public string Value { get; }

    public CorrelationId(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Creates a new correlation ID from a truncated GUID.
    /// Format: 12 lowercase hex characters (48 bits of entropy).
    /// </summary>
    public static CorrelationId New() =>
        new(Guid.NewGuid().ToString("N")[..12]);

    public override string ToString() => Value;

    /// <summary>Implicit conversion to string for logging convenience.</summary>
    public static implicit operator string(CorrelationId id) => id.Value;
}
