using SirThaddeus.Contracts;

namespace SirThaddeus.RuntimeHost.Harness;

/// <summary>
/// Host-agnostic helpers for the harness reset surface. Both the v1
/// headless runtime and the v2 hybrid runtime call into these from their
/// own ASP.NET endpoint registrations, so the harness can drive either
/// host with the same <see cref="HarnessResetRequest"/> shape.
///
/// The endpoint itself is wired by each host (route style differs) but
/// the actual mutation logic lives here. Host-specific concerns — memory
/// store shape, permission gate type — are passed in by the caller.
/// </summary>
public static class HarnessControlPlane
{
    /// <summary>
    /// True only when the runtime was started by the harness (ST_HARNESS_RUN_ACTIVE=true).
    /// Hosts must gate the harness endpoint behind this so production never exposes it.
    /// </summary>
    public static bool IsHarnessReuseEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable("ST_HARNESS_RUN_ACTIVE"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Mutates ST_HARNESS_ALLOWED_TOOLS in-process. Pipeline steps read it
    /// fresh on every call, so the next chat run picks up the new value.
    /// </summary>
    /// <param name="allowedTools">
    /// null  → leave the existing value alone.
    /// ""    → clear the override (no per-test allow-list).
    /// "..." → set as comma-separated allow-list (or "__none__" sentinel).
    /// </param>
    public static string? ApplyAllowedToolsOverride(string? allowedTools)
    {
        if (allowedTools is null)
            return Environment.GetEnvironmentVariable("ST_HARNESS_ALLOWED_TOOLS");

        if (allowedTools.Length == 0)
        {
            Environment.SetEnvironmentVariable("ST_HARNESS_ALLOWED_TOOLS", null);
            return null;
        }

        Environment.SetEnvironmentVariable("ST_HARNESS_ALLOWED_TOOLS", allowedTools);
        return allowedTools;
    }

    /// <summary>
    /// Clears every ST_STUB_* env var (so a prior test's stubs never bleed
    /// into the next test) and applies the new set, if any.
    /// </summary>
    public static (int Cleared, int Set) ApplyStubOverrides(
        IReadOnlyDictionary<string, string?>? overrides)
    {
        var cleared = 0;
        foreach (var key in EnumerateStubKeys())
        {
            Environment.SetEnvironmentVariable(key, null);
            cleared++;
        }

        if (overrides is null || overrides.Count == 0)
            return (cleared, 0);

        var set = 0;
        foreach (var (toolName, value) in overrides)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                continue;

            var key = $"ST_STUB_{toolName.Trim().ToUpperInvariant().Replace("-", "_")}";
            if (string.IsNullOrWhiteSpace(value))
            {
                Environment.SetEnvironmentVariable(key, null);
                continue;
            }

            Environment.SetEnvironmentVariable(key, value);
            set++;
        }

        return (cleared, set);
    }

    /// <summary>
    /// Resets per-test JSON history files referenced by ST_CHAT_HISTORY_PATH
    /// and ST_BRIEFING_HISTORY_PATH. Best-effort: silently ignores missing
    /// paths or held file handles.
    /// </summary>
    public static void ResetHistoryFiles()
    {
        TryWriteEmptyJsonArray(Environment.GetEnvironmentVariable("ST_CHAT_HISTORY_PATH"));
        TryWriteEmptyJsonArray(Environment.GetEnvironmentVariable("ST_BRIEFING_HISTORY_PATH"));
    }

    private static IEnumerable<string> EnumerateStubKeys()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.StartsWith("ST_STUB_", StringComparison.OrdinalIgnoreCase))
                yield return key;
        }
    }

    private static void TryWriteEmptyJsonArray(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, "[]");
        }
        catch
        {
            // Best-effort: history file may be held open elsewhere.
        }
    }
}
