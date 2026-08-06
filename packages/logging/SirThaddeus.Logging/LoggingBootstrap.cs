using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;
using SerilogILogger = Serilog.ILogger;

namespace SirThaddeus.Logging;

/// <summary>
/// Entry points for wiring the Sir Thaddeus logging conventions into a component.
/// </summary>
/// <remarks>
/// Components that run under a generic <see cref="IHostApplicationBuilder"/> (headless
/// runtime, voice host, MCP server) call <see cref="UseSirThaddeusLogging"/> during
/// setup. Components without a host — notably the Avalonia UI — call
/// <see cref="CreateLoggerFactory(LoggingOptions)"/> and register the resulting factory in whatever
/// DI container they manage themselves.
/// </remarks>
public static class LoggingBootstrap
{
    private const string LogLevelEnvVar = "SIRTHADDEUS_LOG_LEVEL";
    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Default directory for a component's log files:
    /// <c>%LocalAppData%/SirThaddeus/logs/{componentName}/</c>.
    /// </summary>
    public static string DefaultLogDirectory(string componentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "SirThaddeus", "logs", componentName);
    }

    /// <summary>
    /// Configure a host builder to route all Microsoft.Extensions.Logging output
    /// through Serilog, using the conventions in <paramref name="options"/>.
    /// </summary>
    /// <remarks>
    /// This replaces any existing logging providers on the builder. Call it
    /// before adding custom providers you want to preserve.
    /// </remarks>
    public static TBuilder UseSirThaddeusLogging<TBuilder>(this TBuilder builder, LoggingOptions options)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        var serilog = BuildSerilogLogger(options);

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new SerilogLoggerProvider(serilog, dispose: true));
        builder.Logging.SetMinimumLevel(ToMelLevel(ResolveMinimumLevel(options)));

        builder.Services.AddSingleton(serilog);

        return builder;
    }

    /// <summary>
    /// Build a standalone <see cref="ILoggerFactory"/> for components that do
    /// not run under a generic host (e.g. the Avalonia desktop UI).
    /// </summary>
    /// <remarks>
    /// Callers are responsible for disposing the returned factory on shutdown
    /// so rolling-file buffers are flushed cleanly.
    /// </remarks>
    public static ILoggerFactory CreateLoggerFactory(LoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var serilog = BuildSerilogLogger(options);
        return new SerilogLoggerFactory(serilog, dispose: true);
    }

    /// <summary>
    /// Adapt an existing Serilog logger to the Microsoft logging abstraction.
    /// This keeps legacy hosts that already own <see cref="Log.Logger"/> from
    /// silently constructing product services with null loggers.
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory(SerilogILogger serilog, bool dispose = false)
    {
        ArgumentNullException.ThrowIfNull(serilog);
        return new SerilogLoggerFactory(serilog, dispose);
    }

    /// <summary>
    /// Build a <see cref="SerilogILogger"/> directly — only for scenarios that
    /// need the raw Serilog API. Prefer the MEL-based entry points above.
    /// </summary>
    public static SerilogILogger BuildSerilogLogger(LoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var logDirectory = options.LogDirectory ?? DefaultLogDirectory(options.ComponentName);
        Directory.CreateDirectory(logDirectory);

        var minimumLevel = ResolveMinimumLevel(options);
        var logPath = Path.Combine(logDirectory, $"{options.ComponentName}-.log");

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Component", options.ComponentName);

        if (options.EnableConsole)
        {
            var standardErrorFromLevel = options.ConsoleStandardErrorOnly
                ? LogEventLevel.Verbose
                : (LogEventLevel?)null;

            config = standardErrorFromLevel is null
                ? config.WriteTo.Console(
                    restrictedToMinimumLevel: minimumLevel,
                    outputTemplate: ConsoleTemplate)
                : config.WriteTo.Console(
                    restrictedToMinimumLevel: minimumLevel,
                    outputTemplate: ConsoleTemplate,
                    standardErrorFromLevel: standardErrorFromLevel.Value);
        }

        if (options.EnableFile)
        {
            if (options.JsonFileFormat)
            {
                config = config.WriteTo.File(
                    formatter: new CompactJsonFormatter(),
                    path: logPath,
                    restrictedToMinimumLevel: minimumLevel,
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: options.FileSizeLimitBytes,
                    retainedFileCountLimit: options.RetainedFileCountLimit,
                    shared: true);
            }
            else
            {
                config = config.WriteTo.File(
                    path: logPath,
                    restrictedToMinimumLevel: minimumLevel,
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: options.FileSizeLimitBytes,
                    retainedFileCountLimit: options.RetainedFileCountLimit,
                    shared: true);
            }
        }

        return config.CreateLogger();
    }

    private static LogEventLevel ResolveMinimumLevel(LoggingOptions options)
    {
        var env = Environment.GetEnvironmentVariable(LogLevelEnvVar);
        if (!string.IsNullOrWhiteSpace(env) &&
            Enum.TryParse<LogEventLevel>(env.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return options.MinimumLevel;
    }

    private static LogLevel ToMelLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevel.Trace,
        LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Information,
        LogEventLevel.Warning => LogLevel.Warning,
        LogEventLevel.Error => LogLevel.Error,
        LogEventLevel.Fatal => LogLevel.Critical,
        _ => LogLevel.Information,
    };
}
