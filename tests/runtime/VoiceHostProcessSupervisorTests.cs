using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Voice;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tests;

public sealed class VoiceHostProcessSupervisorTests
{
    [Fact]
    public async Task EnsureResponsiveAsync_reuses_recent_successful_health_probe()
    {
        using var server = new CountingHealthServer();
        using var supervisor = new VoiceHostProcessSupervisor(NullLogger<VoiceHostProcessSupervisor>.Instance);
        var settings = SettingsDocument.Defaults().Voice with
        {
            VoiceHostBaseUrl = server.BaseUrl,
            VoiceHostEnabled = true
        };
        var endpoint = new Uri(server.BaseUrl + "/asr");

        var first = await supervisor.EnsureResponsiveAsync(endpoint, settings, CancellationToken.None);
        var second = await supervisor.EnsureResponsiveAsync(endpoint, settings, CancellationToken.None);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(1, server.HealthRequests);

        supervisor.InvalidateResponsiveCache(endpoint);
        var third = await supervisor.EnsureResponsiveAsync(endpoint, settings, CancellationToken.None);

        Assert.True(third.Success);
        Assert.Equal(2, server.HealthRequests);
    }

    private sealed class CountingHealthServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _healthRequests;

        public CountingHealthServer()
        {
            var port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(HandleRequestsAsync);
        }

        public string BaseUrl { get; }

        public int HealthRequests => Volatile.Read(ref _healthRequests);

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(1)); } catch { }
            _cts.Dispose();
        }

        private async Task HandleRequestsAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    break;
                }

                if (context.Request.Url?.AbsolutePath != "/health")
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                Interlocked.Increment(ref _healthRequests);
                var body = Encoding.UTF8.GetBytes("""{"ready":true,"asrReady":true,"ttsReady":true,"status":"ok"}""");
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(body, _cts.Token);
                context.Response.Close();
            }
        }

        private static int GetFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
