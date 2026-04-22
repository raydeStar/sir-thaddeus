using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Thaddeus.Runtime.Ipc;

/// <summary>
/// Resolves the OS-specific IPC endpoint string used in the lock file and by the IPC
/// transport layer. Windows uses a named pipe; macOS and Linux use a Unix domain socket.
/// </summary>
public static class IpcEndpoint
{
    /// <summary>True when the runtime is hosted on Windows.</summary>
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Returns a stable, per-user endpoint string. On Windows: <c>thaddeus-&lt;hash&gt;</c>
    /// (used as the named-pipe name). On POSIX: an absolute path under
    /// <c>$XDG_RUNTIME_DIR</c> or <c>/tmp</c>.
    /// </summary>
    public static string GetDefault()
    {
        var userHash = HashCurrentUser();

        if (IsWindows)
        {
            return $"thaddeus-{userHash}";
        }

        var xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(xdgRuntime) && Directory.Exists(xdgRuntime))
        {
            return Path.Combine(xdgRuntime, $"thaddeus-{userHash}.sock");
        }

        var uid = GetUid();
        return $"/tmp/thaddeus-{uid}.sock";
    }

    private static string HashCurrentUser()
    {
        var raw = Environment.UserName + "|" + Environment.UserDomainName;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant();
    }

    private static string GetUid()
    {
        // On POSIX `Environment.UserName` is the login name, which is sufficiently
        // unique for our per-user path. We avoid P/Invoke for a one-line fallback.
        return Environment.UserName.ToLowerInvariant();
    }
}
