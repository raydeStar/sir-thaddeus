namespace SirThaddeus.Diagnostics;

/// <summary>
/// Outcome of a single startup check. Intentionally advisory — startup checks
/// surface problems to the operator but never block a component from starting,
/// so that a user without LM Studio running still sees a UI with actionable
/// guidance instead of a cold window.
/// </summary>
public sealed record StartupCheck
{
    public required string Name { get; init; }

    public required StartupCheckStatus Status { get; init; }

    /// <summary>
    /// Short human-readable summary. Safe to emit to users and logs.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// How long the check took to run, for triage when startup feels slow.
    /// </summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Underlying exception, if any. Not intended for direct user display.
    /// </summary>
    public Exception? Exception { get; init; }
}

public enum StartupCheckStatus
{
    /// <summary>Check passed.</summary>
    Ok,

    /// <summary>Check was intentionally not run (e.g., feature disabled).</summary>
    Skipped,

    /// <summary>Check returned an unexpected-but-survivable result.</summary>
    Warning,

    /// <summary>Check detected a hard failure a user-facing component cares about.</summary>
    Failed,
}
