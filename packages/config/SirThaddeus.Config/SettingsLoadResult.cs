namespace SirThaddeus.Config;

/// <summary>
/// Rich settings load result used by startup safety logic.
/// </summary>
public sealed record SettingsLoadResult
{
    public required AppSettings Settings { get; init; }
    public bool CreatedDefaults { get; init; }
    public bool RecoveredFromCorruption { get; init; }
    public bool MigratedSchema { get; init; }
    public string? SafeModeReason { get; init; }
}
