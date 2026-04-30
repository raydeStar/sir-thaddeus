using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Settings;
using Thaddeus.Runtime.Voice;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tests;

public sealed class VoiceRuntimeStatusServiceTests
{
    [Fact]
    public async Task GetStatusAsync_reports_input_ready_when_tts_is_warming()
    {
        using var server = new HealthServer(asrReady: true, ttsReady: false, ttsError: "No TTS voices were found.");
        var defaults = SettingsDocument.Defaults();
        var settings = defaults with
        {
            Voice = defaults.Voice with
            {
                VoiceHostEnabled = true,
                VoiceHostBaseUrl = server.BaseUrl,
                TtsProvider = "kokoro-sharp"
            },
            Audio = defaults.Audio with { TtsEnabled = true }
        };
        using var supervisor = new VoiceHostProcessSupervisor(NullLogger<VoiceHostProcessSupervisor>.Instance);
        using var sut = new VoiceRuntimeStatusService(
            new InMemorySettings(settings),
            supervisor,
            NullLogger<VoiceRuntimeStatusService>.Instance);

        var status = await sut.GetStatusAsync(ensureHost: false, CancellationToken.None);

        Assert.True(status.HostReachable);
        Assert.True(status.AsrReady);
        Assert.False(status.TtsReady);
        Assert.True(status.InputAvailable);
        Assert.False(status.OutputAvailable);
        Assert.Equal("input-ready", status.Status);
        Assert.Contains("spoken output", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStatusAsync_reports_disabled_without_probing_host()
    {
        var defaults = SettingsDocument.Defaults();
        var settings = defaults with
        {
            Voice = defaults.Voice with
            {
                VoiceHostEnabled = false,
                VoiceHostBaseUrl = "http://127.0.0.1:1"
            }
        };
        using var supervisor = new VoiceHostProcessSupervisor(NullLogger<VoiceHostProcessSupervisor>.Instance);
        using var sut = new VoiceRuntimeStatusService(
            new InMemorySettings(settings),
            supervisor,
            NullLogger<VoiceRuntimeStatusService>.Instance);

        var status = await sut.GetStatusAsync(ensureHost: true, CancellationToken.None);

        Assert.False(status.VoiceHostEnabled);
        Assert.False(status.HostReachable);
        Assert.False(status.InputAvailable);
        Assert.Equal("disabled", status.Status);
    }

    private sealed class InMemorySettings : ISettingsStore
    {
        private readonly SettingsDocument _doc;

        public InMemorySettings(SettingsDocument doc) => _doc = doc;

        public event Action<SettingsDocument>? Changed { add { } remove { } }

        public Task<SettingsDocument> GetAsync(CancellationToken ct) => Task.FromResult(_doc);

        public Task<SettingsDocument> ReplaceAsync(SettingsDocument document, CancellationToken ct) => Task.FromResult(document);
    }

    private sealed class HealthServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly bool _asrReady;
        private readonly bool _ttsReady;
        private readonly string _ttsError;

        public HealthServer(bool asrReady, bool ttsReady, string ttsError)
        {
            _asrReady = asrReady;
            _ttsReady = ttsReady;
            _ttsError = ttsError;

            var port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(HandleRequestsAsync);
        }

        public string BaseUrl { get; }

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

                var ready = _asrReady && _ttsReady;
                var payload = new
                {
                    status = ready ? "ok" : "loading",
                    ready,
                    asrReady = _asrReady,
                    ttsReady = _ttsReady,
                    errorCode = ready ? "" : "tts_not_ready",
                    message = "",
                    asr = new { details = new { lastError = "" } },
                    tts = new { details = new { lastError = _ttsError } }
                };
                var json = JsonSerializer.Serialize(payload);
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
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