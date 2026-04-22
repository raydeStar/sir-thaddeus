using System.Net;
using SirThaddeus.Config;
using SirThaddeus.Diagnostics;

namespace SirThaddeus.Tests;

public sealed class StartupDiagnosticsTests
{
    [Fact]
    public async Task EmptyLlmBaseUrl_MarksCheckSkipped()
    {
        var settings = new AppSettings
        {
            Llm = new LlmSettings { BaseUrl = "" },
        };

        var report = await StartupDiagnostics.RunAsync(settings, perCheckTimeout: TimeSpan.FromSeconds(1));

        var llmCheck = report.Checks.Single(c => c.Name == "llm.reachable");
        Assert.Equal(StartupCheckStatus.Skipped, llmCheck.Status);
    }

    [Fact]
    public async Task UnreachableLlmBaseUrl_MarksCheckFailed()
    {
        // Use a loopback port with nothing listening — the probe should fail
        // with either connection-refused or a short timeout.
        var settings = new AppSettings
        {
            Llm = new LlmSettings { BaseUrl = $"http://127.0.0.1:{GetLikelyUnusedPort()}" },
        };

        var report = await StartupDiagnostics.RunAsync(settings, perCheckTimeout: TimeSpan.FromSeconds(1));

        var llmCheck = report.Checks.Single(c => c.Name == "llm.reachable");
        Assert.Equal(StartupCheckStatus.Failed, llmCheck.Status);
        Assert.False(report.AllOk);
        Assert.Equal(StartupCheckStatus.Failed, report.Worst);
    }

    [Fact]
    public async Task RespondingLlmEndpoint_MarksCheckOk()
    {
        using var listener = new HttpListener();
        var port = GetFreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var serverLoop = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch (HttpListenerException) { /* stopped */ }
            catch (ObjectDisposedException) { /* stopped */ }
        });

        try
        {
            var settings = new AppSettings
            {
                Llm = new LlmSettings { BaseUrl = $"http://127.0.0.1:{port}" },
            };

            var report = await StartupDiagnostics.RunAsync(settings, perCheckTimeout: TimeSpan.FromSeconds(2));

            var llmCheck = report.Checks.Single(c => c.Name == "llm.reachable");
            Assert.Equal(StartupCheckStatus.Ok, llmCheck.Status);
        }
        finally
        {
            listener.Stop();
            await serverLoop;
        }
    }

    [Fact]
    public async Task VoiceHostDisabled_MarksCheckSkipped()
    {
        var settings = new AppSettings
        {
            Llm = new LlmSettings { BaseUrl = "" },
            Voice = new VoiceSettings { VoiceHostEnabled = false },
        };

        var report = await StartupDiagnostics.RunAsync(settings, perCheckTimeout: TimeSpan.FromSeconds(1));

        var voiceCheck = report.Checks.Single(c => c.Name == "voicehost.reachable");
        Assert.Equal(StartupCheckStatus.Skipped, voiceCheck.Status);
    }

    [Fact]
    public async Task VoiceHostEnabledButUnreachable_MarksCheckWarning()
    {
        // VoiceHost is launched on demand in practice, so an unreachable
        // probe should surface as a Warning, not a hard Failed.
        var settings = new AppSettings
        {
            Llm = new LlmSettings { BaseUrl = "" },
            Voice = new VoiceSettings
            {
                VoiceHostEnabled = true,
                VoiceHostBaseUrl = $"http://127.0.0.1:{GetLikelyUnusedPort()}",
            },
        };

        var report = await StartupDiagnostics.RunAsync(settings, perCheckTimeout: TimeSpan.FromSeconds(1));

        var voiceCheck = report.Checks.Single(c => c.Name == "voicehost.reachable");
        Assert.Equal(StartupCheckStatus.Warning, voiceCheck.Status);
        // Warnings must not promote the whole report to Failed.
        Assert.NotEqual(StartupCheckStatus.Failed, report.Worst);
    }

    [Fact]
    public async Task LogsWritableCheck_PassesOnDefaultEnvironment()
    {
        var settings = new AppSettings
        {
            Llm = new LlmSettings { BaseUrl = "" },
        };

        var report = await StartupDiagnostics.RunAsync(settings, perCheckTimeout: TimeSpan.FromSeconds(1));

        var logsCheck = report.Checks.Single(c => c.Name == "logs.writable");
        Assert.Equal(StartupCheckStatus.Ok, logsCheck.Status);
    }

    private static int GetFreePort()
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static int GetLikelyUnusedPort()
    {
        // Pick a free port, then close the listener so nothing is bound.
        // A new bind/connect there is very unlikely to happen in the test window.
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
