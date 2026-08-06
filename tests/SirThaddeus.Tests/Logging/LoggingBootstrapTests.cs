using Microsoft.Extensions.Logging;
using Serilog.Events;
using SirThaddeus.Logging;

namespace SirThaddeus.Tests.Logging;

public sealed class LoggingBootstrapTests
{
    [Fact]
    public void ExistingSerilogAdapter_RoutesMicrosoftLogsToOwnedSink()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "sir-thaddeus-logging-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var serilog = LoggingBootstrap.BuildSerilogLogger(new LoggingOptions
            {
                ComponentName = "headless-wiring",
                LogDirectory = directory,
                MinimumLevel = LogEventLevel.Information,
                EnableConsole = false,
            });

            using (var factory = LoggingBootstrap.CreateLoggerFactory(serilog))
            {
                factory.CreateLogger("headless-wiring")
                    .LogInformation("HEADLESS_LOGGER_WIRING proof=present");
            }

            (serilog as IDisposable)?.Dispose();

            var log = Directory.GetFiles(directory, "headless-wiring-*.log").Single();
            var contents = File.ReadAllText(log);
            Assert.Contains("HEADLESS_LOGGER_WIRING", contents, StringComparison.Ordinal);
            Assert.Contains("proof=present", contents, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
