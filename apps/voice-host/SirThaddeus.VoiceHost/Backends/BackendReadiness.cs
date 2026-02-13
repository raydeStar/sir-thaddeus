namespace SirThaddeus.VoiceHost.Backends;

public sealed record BackendReadiness(
    bool Ready,
    string Status,
    string Detail,
    BackendEngineStatus? EngineStatus = null)
{
    public static BackendReadiness Ok(string detail = "", BackendEngineStatus? engineStatus = null)
        => new(true, "ok", detail, engineStatus);

    public static BackendReadiness NotReady(string detail, BackendEngineStatus? engineStatus = null)
        => new(false, "not_ready", detail, engineStatus);
}

public sealed record BackendEngineStatus(
    int SchemaVersion,
    bool Ready,
    string Engine,
    string EngineVersion,
    string ModelId,
    string InstanceId,
    string TimestampUtc,
    BackendEngineStatusDetails Details)
{
    public static BackendEngineStatus Unknown(string engine = "unknown", string detail = "")
        => new(
            SchemaVersion: 1,
            Ready: false,
            Engine: engine,
            EngineVersion: "",
            ModelId: "",
            InstanceId: "",
            TimestampUtc: DateTimeOffset.UtcNow.ToString("O"),
            Details: BackendEngineStatusDetails.Unknown(detail));
}

public sealed record BackendEngineStatusDetails(
    bool Installed,
    IReadOnlyList<string> Missing,
    string LastError)
{
    public static BackendEngineStatusDetails Unknown(string detail = "")
        => new(
            Installed: false,
            Missing: Array.Empty<string>(),
            LastError: detail);
}
