namespace Thaddeus.Runtime.Hosting;

/// <summary>
/// Runtime configuration discovered or computed at startup. This is intentionally
/// distinct from <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>: these
/// values are derived (token, port, paths) rather than read from settings files.
/// </summary>
public sealed record RuntimeOptions
{
    /// <summary>Loopback port chosen by Kestrel. Set after Kestrel binds.</summary>
    public int Port { get; init; }

    /// <summary>Base64url-encoded 256-bit bearer token, rotated every start.</summary>
    public required string BearerToken { get; init; }

    /// <summary>OS PID of the runtime process.</summary>
    public required int Pid { get; init; }

    /// <summary>Runtime semantic version reported during the hello handshake.</summary>
    public required string Version { get; init; }

    /// <summary>Absolute path to the lock file (e.g. <c>~/.thaddeus/runtime.lock</c>).</summary>
    public required string LockFilePath { get; init; }

    /// <summary>OS-specific IPC endpoint (named pipe name on Windows, UDS path elsewhere).</summary>
    public required string IpcEndpoint { get; init; }

    /// <summary>UTC time the runtime started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When true, no external services are touched; deterministic IDs/clocks are used.</summary>
    public bool TestMode { get; init; }

    /// <summary>Optional PID of the parent shell. Used to auto-shutdown if the shell exits.</summary>
    public int? ParentPid { get; init; }
}
