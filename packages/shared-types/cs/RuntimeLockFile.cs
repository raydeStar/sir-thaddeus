namespace Thaddeus.SharedTypes;

/// <summary>
/// Contents of <c>~/.thaddeus/runtime.lock</c>. Written by the runtime on startup with
/// 0600 file mode. Mirrors <c>packages/shared-schemas/lock-file.schema.json</c>.
/// </summary>
public sealed record RuntimeLockFile
{
    /// <summary>OS PID of the runtime process.</summary>
    public required int Pid { get; init; }

    /// <summary>Loopback HTTP/WebSocket port.</summary>
    public required int Port { get; init; }

    /// <summary>Base64url-encoded 256-bit bearer token, rotated every start.</summary>
    public required string Token { get; init; }

    /// <summary>Runtime semantic version. Used for hello-handshake compatibility checks.</summary>
    public required string Version { get; init; }

    /// <summary>OS-specific IPC endpoint (named pipe name on Windows, UDS path elsewhere).</summary>
    public string? IpcEndpoint { get; init; }

    /// <summary>UTC timestamp when the runtime started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>PIDs of long-lived sidecars (whisper.cpp, piper). Used to clean up stale processes on next start.</summary>
    public IReadOnlyList<int>? SidecarPids { get; init; }
}
