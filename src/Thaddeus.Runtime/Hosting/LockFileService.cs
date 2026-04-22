using System.Runtime.InteropServices;
using System.Text.Json;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Hosting;

/// <summary>
/// Reads and writes <c>~/.thaddeus/runtime.lock</c>. The file mode on POSIX systems is
/// forced to 0600 (per spec §6.3); 0644 is explicitly not acceptable.
/// </summary>
public static class LockFileService
{
    /// <summary>Standard JSON options used for the lock file payload.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Returns the canonical path to the lock file (creates the parent directory if missing).
    /// On POSIX, ensures the parent directory has 0700 permissions.
    /// </summary>
    public static string GetDefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, ".thaddeus");
        Directory.CreateDirectory(dir);
        TrySetUnixDirectoryPermissions(dir, "700");
        return Path.Combine(dir, "runtime.lock");
    }

    /// <summary>Atomically writes the lock file with 0600 permissions.</summary>
    public static void Write(string path, RuntimeLockFile contents)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(contents);

        var json = JsonSerializer.Serialize(contents, JsonOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        TrySetUnixFilePermissions(tmp, "600");
        File.Move(tmp, path, overwrite: true);
        TrySetUnixFilePermissions(path, "600");
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

    private static void TrySetUnixFilePermissions(string path, string octal)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            File.SetUnixFileMode(path, ParseOctalMode(octal));
        }
        catch
        {
            // Permissions are best-effort; if File.SetUnixFileMode is unavailable
            // (older runtimes, exotic FS) the loopback token is still our primary defence.
        }
    }

    private static void TrySetUnixDirectoryPermissions(string path, string octal)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            File.SetUnixFileMode(path, ParseOctalMode(octal));
        }
        catch
        {
            // best effort
        }
    }

    private static UnixFileMode ParseOctalMode(string octal)
    {
        // Convert "600" to UnixFileMode flags. Each digit represents owner/group/other.
        if (octal.Length is < 3 or > 4) throw new ArgumentException("Octal mode must be 3 or 4 digits.", nameof(octal));
        var idx = octal.Length - 3;
        var owner = octal[idx] - '0';
        var group = octal[idx + 1] - '0';
        var other = octal[idx + 2] - '0';

        var mode = UnixFileMode.None;
        if ((owner & 4) != 0) mode |= UnixFileMode.UserRead;
        if ((owner & 2) != 0) mode |= UnixFileMode.UserWrite;
        if ((owner & 1) != 0) mode |= UnixFileMode.UserExecute;
        if ((group & 4) != 0) mode |= UnixFileMode.GroupRead;
        if ((group & 2) != 0) mode |= UnixFileMode.GroupWrite;
        if ((group & 1) != 0) mode |= UnixFileMode.GroupExecute;
        if ((other & 4) != 0) mode |= UnixFileMode.OtherRead;
        if ((other & 2) != 0) mode |= UnixFileMode.OtherWrite;
        if ((other & 1) != 0) mode |= UnixFileMode.OtherExecute;
        return mode;
    }
}
