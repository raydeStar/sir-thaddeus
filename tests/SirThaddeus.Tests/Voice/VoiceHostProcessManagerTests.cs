using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.DesktopRuntime.Services;

namespace SirThaddeus.Tests.Voice;

public sealed class VoiceHostProcessManagerTests
{
    [Fact]
    public async Task EnsureRunningAsync_UsesFirstReadyPortInBoundedRange()
    {
        var tempHostExe = CreateTempHostExe();
        try
        {
            var starterCalls = 0;
            using var handler = new RoutingHealthHandler(uri =>
            {
                if (uri.AbsolutePath.Equals("/health", StringComparison.OrdinalIgnoreCase) &&
                    uri.Port == 17847)
                {
                    return HealthResponse(ready: true, asrReady: true, ttsReady: true);
                }

                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":\"not_ready\"}", Encoding.UTF8, "application/json")
                };
            });

            await using var manager = new VoiceHostProcessManager(
                new TestAuditLogger(),
                new VoiceSettings
                {
                    VoiceHostEnabled = true,
                    VoiceHostBaseUrl = "http://127.0.0.1:17845",
                    VoiceHostHealthPath = "/health",
                    VoiceHostStartupTimeoutMs = 10_000
                },
                httpMessageHandler: handler,
                processStarter: _ =>
                {
                    starterCalls++;
                    return null;
                },
                voiceHostPathResolver: () => tempHostExe,
                ephemeralPortProvider: () => null);

            var result = await manager.EnsureRunningAsync(CancellationToken.None);

            Assert.True(result.Success, result.UserMessage);
            Assert.Equal("http://127.0.0.1:17847", result.BaseUrl);
            Assert.True(starterCalls >= 2); // 17845 + 17846 should have attempted start before 17847 was found ready
        }
        finally
        {
            TryDelete(tempHostExe);
        }
    }

    [Fact]
    public async Task EnsureRunningAsync_UsesEphemeralPortAfterBoundedRangeExhausted()
    {
        var tempHostExe = CreateTempHostExe();
        try
        {
            const int ephemeralPort = 19001;
            using var handler = new RoutingHealthHandler(uri =>
            {
                if (uri.AbsolutePath.Equals("/health", StringComparison.OrdinalIgnoreCase) &&
                    uri.Port == ephemeralPort)
                {
                    return HealthResponse(ready: true, asrReady: true, ttsReady: true);
                }

                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":\"not_ready\"}", Encoding.UTF8, "application/json")
                };
            });

            await using var manager = new VoiceHostProcessManager(
                new TestAuditLogger(),
                new VoiceSettings
                {
                    VoiceHostEnabled = true,
                    VoiceHostBaseUrl = "http://127.0.0.1:17845",
                    VoiceHostHealthPath = "/health",
                    VoiceHostStartupTimeoutMs = 10_000
                },
                httpMessageHandler: handler,
                processStarter: _ => null,
                voiceHostPathResolver: () => tempHostExe,
                ephemeralPortProvider: () => ephemeralPort);

            var result = await manager.EnsureRunningAsync(CancellationToken.None);

            Assert.True(result.Success, result.UserMessage);
            Assert.Equal($"http://127.0.0.1:{ephemeralPort}", result.BaseUrl);
        }
        finally
        {
            TryDelete(tempHostExe);
        }
    }

    [Fact]
    public async Task EnsureRunningAsync_WaitsForReadyTrue_ThenFailsWhenNeverReady()
    {
        var tempHostExe = CreateTempHostExe();
        try
        {
            using var handler = new RoutingHealthHandler(_ => HealthResponse(
                ready: false,
                asrReady: false,
                ttsReady: false,
                errorCode: "asr_not_ready",
                message: "ASR backend still loading."));

            await using var manager = new VoiceHostProcessManager(
                new TestAuditLogger(),
                new VoiceSettings
                {
                    VoiceHostEnabled = true,
                    VoiceHostBaseUrl = "http://127.0.0.1:17845",
                    VoiceHostHealthPath = "/health",
                    VoiceHostStartupTimeoutMs = 5_000
                },
                httpMessageHandler: handler,
                processStarter: _ => StartSleepProcess(seconds: 20),
                voiceHostPathResolver: () => tempHostExe,
                ephemeralPortProvider: () => null);

            var sw = Stopwatch.StartNew();
            var result = await manager.EnsureRunningAsync(CancellationToken.None);
            sw.Stop();

            Assert.False(result.Success);
            Assert.NotNull(result.ErrorCode);
            Assert.Contains(result.ErrorCode!, ["voicehost_unhealthy", "voicehost_startup_timeout", "voicehost_not_ready", "voicehost_warming_up"]);
            Assert.False(string.IsNullOrWhiteSpace(result.UserMessage));
            Assert.True(sw.ElapsedMilliseconds >= 1_500, $"Expected readiness polling before failure, actual={sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            TryDelete(tempHostExe);
        }
    }

    [Fact]
    public async Task EnsureRunningAsync_DoesNotRestartManagedProcessWhileStillWarming()
    {
        var tempHostExe = CreateTempHostExe();
        try
        {
            var starterCalls = 0;
            var elapsed = Stopwatch.StartNew();

            using var handler = new RoutingHealthHandler(_ =>
            {
                var ready = elapsed.ElapsedMilliseconds >= 7_000;
                return HealthResponse(
                    ready: ready,
                    asrReady: ready,
                    ttsReady: ready,
                    errorCode: ready ? "" : "voicehost_warming_up",
                    message: ready ? "" : "ASR backend still loading.");
            });

            await using var manager = new VoiceHostProcessManager(
                new TestAuditLogger(),
                new VoiceSettings
                {
                    VoiceHostEnabled = true,
                    VoiceHostBaseUrl = "http://127.0.0.1:17845",
                    VoiceHostHealthPath = "/health",
                    VoiceHostStartupTimeoutMs = 5_000
                },
                httpMessageHandler: handler,
                processStarter: _ =>
                {
                    starterCalls++;
                    return StartSleepProcess(seconds: 30);
                },
                voiceHostPathResolver: () => tempHostExe,
                ephemeralPortProvider: () => null);

            var first = await manager.EnsureRunningAsync(CancellationToken.None);
            Assert.False(first.Success);
            Assert.Equal("voicehost_warming_up", first.ErrorCode);
            Assert.Contains("warming up", first.UserMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, starterCalls);

            await Task.Delay(2_500);

            var second = await manager.EnsureRunningAsync(CancellationToken.None);
            Assert.True(second.Success, second.UserMessage);
            Assert.Equal("http://127.0.0.1:17845", second.BaseUrl);
            Assert.Equal(1, starterCalls);
        }
        finally
        {
            TryDelete(tempHostExe);
        }
    }

    [Fact]
    public async Task EnsureRunningAsync_PassesEngineSelectionArgsToManagedVoiceHost()
    {
        var tempHostExe = CreateTempHostExe();
        try
        {
            var started = false;
            string startArgs = "";

            using var handler = new RoutingHealthHandler(uri =>
            {
                if (started &&
                    uri.AbsolutePath.Equals("/health", StringComparison.OrdinalIgnoreCase) &&
                    uri.Port == 17845)
                {
                    return HealthResponse(ready: true, asrReady: true, ttsReady: true);
                }

                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":\"warming\"}", Encoding.UTF8, "application/json")
                };
            });

            await using var manager = new VoiceHostProcessManager(
                new TestAuditLogger(),
                new VoiceSettings
                {
                    VoiceHostEnabled = true,
                    VoiceHostBaseUrl = "http://127.0.0.1:17845",
                    VoiceHostHealthPath = "/health",
                    VoiceHostStartupTimeoutMs = 8_000,
                    TtsEngine = "kokoro",
                    TtsVoiceId = "af_sky",
                    SttEngine = "faster-whisper",
                    SttModelId = ""
                },
                httpMessageHandler: handler,
                processStarter: info =>
                {
                    startArgs = info.Arguments;
                    started = true;
                    return StartSleepProcess(seconds: 20);
                },
                voiceHostPathResolver: () => tempHostExe,
                ephemeralPortProvider: () => null);

            var result = await manager.EnsureRunningAsync(CancellationToken.None);

            Assert.True(result.Success, result.UserMessage);
            Assert.Contains("--tts-engine", startArgs);
            Assert.Contains("kokoro", startArgs, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--tts-voice-id", startArgs);
            Assert.Contains("af_sky", startArgs, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--stt-engine", startArgs);
            Assert.Contains("faster-whisper", startArgs, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--stt-model-id", startArgs);
            Assert.Contains("base", startArgs, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(tempHostExe);
        }
    }

    private static HttpResponseMessage HealthResponse(
        bool ready,
        bool asrReady,
        bool ttsReady,
        string errorCode = "",
        string message = "")
    {
        var json = $$"""
                     {
                       "status":"ok",
                       "ready":{{ready.ToString().ToLowerInvariant()}},
                       "asrReady":{{asrReady.ToString().ToLowerInvariant()}},
                       "ttsReady":{{ttsReady.ToString().ToLowerInvariant()}},
                       "version":"test",
                       "errorCode":"{{errorCode}}",
                       "message":"{{message}}"
                     }
                     """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static Process StartSleepProcess(int seconds)
    {
        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds {seconds}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        return Process.Start(info)!;
    }

    private static string CreateTempHostExe()
    {
        var path = Path.Combine(Path.GetTempPath(), $"voicehost-test-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A]); // MZ header marker only; existence is what matters in tests.
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    private sealed class RoutingHealthHandler(Func<Uri, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request.RequestUri!);
            return Task.FromResult(response);
        }
    }

    private sealed class TestAuditLogger : IAuditLogger
    {
        private readonly List<AuditEvent> _events = [];

        public void Append(AuditEvent auditEvent) => _events.Add(auditEvent);

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            _events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public IReadOnlyList<AuditEvent> ReadTail(int maxEvents)
            => _events.TakeLast(Math.Max(0, maxEvents)).ToList();

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int maxEvents, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AuditEvent>>(ReadTail(maxEvents));
    }
}
