using System.Net;
using System.Text;
using System.Text.Json;

namespace SirThaddeus.Tests.Voice;

/// <summary>
/// Minimal loopback HTTP server that mimics the VoiceHost /health, /asr, /tts
/// endpoints for test isolation. Runs on an OS-assigned ephemeral port.
/// </summary>
public sealed class MockVoiceHostServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public int Port { get; }
    public string BaseUrl { get; }

    // ── Configurable readiness state ─────────────────────────────────
    public bool HealthReady { get; set; } = true;
    public bool AsrReady { get; set; } = true;
    public bool TtsReady { get; set; } = true;
    public string AsrTranscript { get; set; } = "mock transcript";
    public byte[] TtsAudioBytes { get; set; } = [0x52, 0x49, 0x46, 0x46]; // "RIFF" stub

    // ── Request counters for assertions ──────────────────────────────
    public int HealthRequestCount { get; private set; }
    public int AsrRequestCount { get; private set; }
    public int TtsRequestCount { get; private set; }

    public MockVoiceHostServer()
    {
        // Find a free port by binding to 0 then releasing.
        var tempListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tempListener.Start();
        Port = ((IPEndPoint)tempListener.LocalEndpoint).Port;
        tempListener.Stop();

        BaseUrl = $"http://127.0.0.1:{Port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }

                try
                {
                    await HandleRequestAsync(context);
                }
                catch
                {
                    try
                    {
                        context.Response.StatusCode = 500;
                        context.Response.Close();
                    }
                    catch { /* best effort */ }
                }
            }
        });
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var method = context.Request.HttpMethod;

        switch (path)
        {
            case "/health" when method == "GET":
                HealthRequestCount++;
                var ready = HealthReady && AsrReady && TtsReady;
                var errorCode = ready
                    ? ""
                    : !AsrReady && !TtsReady
                        ? "asr_tts_not_ready"
                        : !AsrReady
                            ? "asr_not_ready"
                            : "tts_not_ready";
                var message = ready
                    ? ""
                    : !AsrReady && !TtsReady
                        ? "ASR and TTS backends are not ready."
                        : !AsrReady
                            ? "ASR backend is not ready."
                            : "TTS backend is not ready.";
                await RespondJsonAsync(context.Response, new
                {
                    status = "ok",
                    ready,
                    asrReady = AsrReady,
                    ttsReady = TtsReady,
                    version = "0.0.0-test",
                    errorCode,
                    message
                });
                break;

            case "/asr" when method == "POST":
                AsrRequestCount++;
                // Just return the configured transcript regardless of input.
                await RespondJsonAsync(context.Response, new
                {
                    text = AsrTranscript
                });
                break;

            case "/tts" when method == "POST":
                TtsRequestCount++;
                context.Response.StatusCode = 200;
                context.Response.ContentType = "audio/wav";
                context.Response.AddHeader("X-Sample-Rate", "24000");
                context.Response.AddHeader("X-Channels", "1");
                context.Response.AddHeader("X-Format", "pcm_s16le");
                await context.Response.OutputStream.WriteAsync(TtsAudioBytes);
                context.Response.Close();
                break;

            default:
                context.Response.StatusCode = 404;
                context.Response.Close();
                break;
        }
    }

    private static async Task RespondJsonAsync(HttpListenerResponse response, object payload)
    {
        response.StatusCode = 200;
        response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        _cts.Dispose();
    }
}
