namespace SirThaddeus.Agent.Guardrails;

/// <summary>
/// Canonical values for the reasoning-guardrails runtime mode.
/// </summary>
public static class ReasoningGuardrailsMode
{
    public const string Off = "off";
    public const string Auto = "auto";
    public const string Always = "always";

    public static string Normalize(string? mode)
    {
        var normalized = (mode ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            Auto => Auto,
            Always => Always,
            _ => Off
        };
    }

    public static bool IsEnabled(string? mode)
        => !string.Equals(Normalize(mode), Off, StringComparison.Ordinal);
}
