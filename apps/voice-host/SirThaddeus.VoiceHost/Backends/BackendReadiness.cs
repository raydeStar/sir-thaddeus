namespace SirThaddeus.VoiceHost.Backends;

public sealed record BackendReadiness(
    bool Ready,
    string Status,
    string Detail)
{
    public static BackendReadiness Ok(string detail = "")
        => new(true, "ok", detail);

    public static BackendReadiness NotReady(string detail)
        => new(false, "not_ready", detail);
}
