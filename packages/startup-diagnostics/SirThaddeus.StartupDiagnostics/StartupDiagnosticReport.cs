using System.Collections.Immutable;

namespace SirThaddeus.Diagnostics;

/// <summary>
/// Aggregated result of running all startup checks. Callers inspect
/// <see cref="Checks"/> to log or surface the report, and can use
/// <see cref="Worst"/> to decide whether to show a warning banner.
/// </summary>
public sealed record StartupDiagnosticReport
{
    public required ImmutableArray<StartupCheck> Checks { get; init; }

    public StartupCheckStatus Worst => Checks.IsDefaultOrEmpty
        ? StartupCheckStatus.Ok
        : Checks.Max(c => c.Status);

    public bool AllOk => Worst == StartupCheckStatus.Ok;
}
