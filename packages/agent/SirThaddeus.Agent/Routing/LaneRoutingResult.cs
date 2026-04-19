namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Result of lane classification. Produced before any tool is loaded.
/// </summary>
public sealed record LaneRoutingResult
{
    /// <summary>The classified task lane.</summary>
    public required TaskLane Lane { get; init; }

    /// <summary>Classifier confidence (0.0–1.0).</summary>
    public required double Confidence { get; init; }

    /// <summary>Human-readable rationale for the classification.</summary>
    public required string Rationale { get; init; }

    /// <summary>
    /// Wall-clock milliseconds spent on classification.
    /// Logged for latency tracking (target: &lt; 500 ms).
    /// </summary>
    public double ElapsedMs { get; init; }
}
