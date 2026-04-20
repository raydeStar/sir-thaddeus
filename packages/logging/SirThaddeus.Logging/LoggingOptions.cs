using Serilog.Events;

namespace SirThaddeus.Logging;

/// <summary>
/// Options controlling how Sir Thaddeus components emit logs.
/// </summary>
/// <remarks>
/// The defaults are opinionated for local-first operation: rolling daily file logs
/// under <c>%LocalAppData%/SirThaddeus/logs/{component}/</c>, a structured JSON file
/// format suitable for post-mortem inspection, and a human-readable console sink
/// for active sessions. Every component in the solution — headless runtime, voice
/// host, MCP server, and UI — resolves paths against the same base so operators
/// have one place to look when something breaks.
/// </remarks>
public sealed record LoggingOptions
{
    /// <summary>
    /// Short component name used in log file naming (e.g. "voice-host", "mcp-server").
    /// </summary>
    public required string ComponentName { get; init; }

    /// <summary>
    /// Override the log directory. When null, resolves to
    /// <c>%LocalAppData%/SirThaddeus/logs/{ComponentName}/</c>.
    /// </summary>
    public string? LogDirectory { get; init; }

    /// <summary>
    /// Minimum level written to any sink. Defaults to <see cref="LogEventLevel.Information"/>.
    /// Can be overridden via the <c>SIRTHADDEUS_LOG_LEVEL</c> environment variable.
    /// </summary>
    public LogEventLevel MinimumLevel { get; init; } = LogEventLevel.Information;

    /// <summary>
    /// Emit a human-readable console sink. Defaults to true. Useful to disable
    /// when a component is spawned as a subprocess and stdio is load-bearing.
    /// </summary>
    public bool EnableConsole { get; init; } = true;

    /// <summary>
    /// Route every console log line to stderr instead of stdout. Set this to
    /// true for components whose stdout carries a protocol payload (notably
    /// the MCP server, which uses stdio as transport). Defaults to false.
    /// </summary>
    public bool ConsoleStandardErrorOnly { get; init; }

    /// <summary>
    /// Emit a rolling file sink. Defaults to true.
    /// </summary>
    public bool EnableFile { get; init; } = true;

    /// <summary>
    /// Write file logs in Serilog's compact JSON format. Defaults to true so
    /// logs are post-mortem-grep-friendly and machine-parseable.
    /// </summary>
    public bool JsonFileFormat { get; init; } = true;

    /// <summary>
    /// Cap on file count retained by the rolling sink. Defaults to 14 days.
    /// </summary>
    public int RetainedFileCountLimit { get; init; } = 14;

    /// <summary>
    /// Rolling file size cap before a new file is opened. Defaults to 32 MB.
    /// </summary>
    public long FileSizeLimitBytes { get; init; } = 32L * 1024 * 1024;
}
