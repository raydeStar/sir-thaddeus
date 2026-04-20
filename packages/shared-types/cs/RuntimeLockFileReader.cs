using System.Text.Json;

namespace Thaddeus.SharedTypes;

/// <summary>
/// Read-only helpers for locating and parsing the runtime lock file. Lives in the
/// shared-types package so both the shell and any auxiliary tools can consume it
/// without depending on the runtime project.
/// </summary>
public static class RuntimeLockFileReader
{
    /// <summary>JSON options matching the runtime's writer.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Returns the canonical lock-file path. Does not create directories.</summary>
    public static string GetDefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".thaddeus", "runtime.lock");
    }

    /// <summary>Reads the lock file. Returns null if the file does not exist or cannot be parsed.</summary>
    public static RuntimeLockFile? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<RuntimeLockFile>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Best-effort delete; never throws.</summary>
    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
