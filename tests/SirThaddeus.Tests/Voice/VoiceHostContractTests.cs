using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SirThaddeus.AuditLog;
using SirThaddeus.Voice;

namespace SirThaddeus.Tests.Voice;

// ─────────────────────────────────────────────────────────────────────────
// VoiceHost HTTP Contract Tests
//
// Validates the frozen V1 HTTP contracts (/health, /asr, /tts) by running
// a real loopback MockVoiceHostServer and asserting response shapes,
// content types, and metadata headers.
//
// These are the source-of-truth for what the runtime clients must send
// and what the VoiceHost executable must return.
// ─────────────────────────────────────────────────────────────────────────

public sealed class VoiceHostContractTests : IDisposable
{
    private readonly MockVoiceHostServer _server;
    private readonly HttpClient _httpClient;

    public VoiceHostContractTests()
    {
        _server = new MockVoiceHostServer();
        _server.Start();
        _httpClient = new HttpClient();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _server.Dispose();
    }

    // ── /health ──────────────────────────────────────────────────────

    [Fact]
    public async Task Health_AllReady_ReturnsExpectedShape()
    {
        _server.HealthReady = true;
        _server.AsrReady = true;
        _server.TtsReady = true;

        var response = await _httpClient.GetAsync($"{_server.BaseUrl}/health");
        Assert.True(response.IsSuccessStatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("ready").GetBoolean());
        Assert.True(root.GetProperty("asrReady").GetBoolean());
        Assert.True(root.GetProperty("ttsReady").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task Health_AsrNotReady_ReadyIsFalse()
    {
        _server.AsrReady = false;
        _server.TtsReady = true;

        var response = await _httpClient.GetAsync($"{_server.BaseUrl}/health");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.False(doc.RootElement.GetProperty("ready").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("asrReady").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("ttsReady").GetBoolean());
        Assert.Equal("asr_not_ready", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Health_TtsNotReady_ReadyIsFalse()
    {
        _server.AsrReady = true;
        _server.TtsReady = false;

        var response = await _httpClient.GetAsync($"{_server.BaseUrl}/health");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.False(doc.RootElement.GetProperty("ready").GetBoolean());
        Assert.Equal("tts_not_ready", doc.RootElement.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Health_RequestCounter_IncrementedPerCall()
    {
        await _httpClient.GetAsync($"{_server.BaseUrl}/health");
        await _httpClient.GetAsync($"{_server.BaseUrl}/health");

        Assert.Equal(2, _server.HealthRequestCount);
    }

    // ── /asr ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Asr_MultipartWithAudioField_ReturnsTextInJson()
    {
        _server.AsrTranscript = "the quick brown fox";

        using var content = new MultipartFormDataContent();
        var audioBytes = new byte[] { 0x52, 0x49, 0x46, 0x46 };
        var audioContent = new ByteArrayContent(audioBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "audio", "test.wav");

        var response = await _httpClient.PostAsync($"{_server.BaseUrl}/asr", content);
        Assert.True(response.IsSuccessStatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("the quick brown fox", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal(1, _server.AsrRequestCount);
    }

    // ── /tts ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Tts_JsonPayload_ReturnsAudioBytes()
    {
        _server.TtsAudioBytes = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00 };

        var payload = JsonSerializer.Serialize(new
        {
            text = "hello",
            voice = "default",
            format = "pcm_s16le",
            sampleRate = 24000
        });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync($"{_server.BaseUrl}/tts", content);
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);

        var audioBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(_server.TtsAudioBytes.Length, audioBytes.Length);
        Assert.Equal(1, _server.TtsRequestCount);
    }

    [Fact]
    public async Task Tts_ResponseHeaders_ContainAudioMetadata()
    {
        var payload = JsonSerializer.Serialize(new { text = "test" });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync($"{_server.BaseUrl}/tts", content);

        Assert.True(response.Headers.TryGetValues("X-Sample-Rate", out var sr));
        Assert.Contains("24000", sr);

        Assert.True(response.Headers.TryGetValues("X-Channels", out var ch));
        Assert.Contains("1", ch);

        Assert.True(response.Headers.TryGetValues("X-Format", out var fmt));
        Assert.Contains("pcm_s16le", fmt);
    }

    // ── 404 for unknown routes ───────────────────────────────────────

    [Fact]
    public async Task UnknownRoute_Returns404()
    {
        var response = await _httpClient.GetAsync($"{_server.BaseUrl}/nope");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}

// ─────────────────────────────────────────────────────────────────────────
// Orchestrator Integration with Mock VoiceHost HTTP
//
// Validates that the voice orchestrator can complete a full cycle when
// ASR transcription is backed by a real HTTP round-trip to the mock server.
// ─────────────────────────────────────────────────────────────────────────

public sealed class VoiceHostOrchestratorIntegrationTests : IDisposable
{
    private readonly MockVoiceHostServer _server;

    public VoiceHostOrchestratorIntegrationTests()
    {
        _server = new MockVoiceHostServer();
        _server.Start();
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task FullCycle_WithHttpAsr_CompletesSuccessfully()
    {
        _server.AsrTranscript = "integration test transcript";

        var capture = new StubCaptureService();
        var playback = new StubPlaybackService();
        var asr = new HttpBackedAsrService(_server.BaseUrl);
        var agent = new StubAgentService("agent response");
        var audit = new TestAuditLogger();

        await using var orchestrator = new VoiceSessionOrchestrator(
            capture, playback, asr, agent, audit);
        await orchestrator.StartAsync();

        orchestrator.EnqueueMicDown();
        await WaitForStateAsync(orchestrator, VoiceState.Listening, TimeSpan.FromSeconds(2));

        orchestrator.EnqueueMicUp();
        await WaitForStateAsync(orchestrator, VoiceState.Idle, TimeSpan.FromSeconds(5));

        Assert.True(_server.AsrRequestCount >= 1, "ASR endpoint should have been hit.");
        Assert.True(agent.CallCount >= 1, "Agent should have been invoked.");
    }

    [Fact]
    public async Task ShutupDuringHttp_CancelsAndReturnsIdle()
    {
        _server.AsrTranscript = "will be interrupted";

        var capture = new StubCaptureService();
        var playback = new StubPlaybackService(blockPlayback: true);
        var asr = new HttpBackedAsrService(_server.BaseUrl);
        var agent = new StubAgentService("response");
        var audit = new TestAuditLogger();

        await using var orchestrator = new VoiceSessionOrchestrator(
            capture, playback, asr, agent, audit);
        await orchestrator.StartAsync();

        orchestrator.EnqueueMicDown();
        orchestrator.EnqueueMicUp();
        await WaitForStateAsync(orchestrator, VoiceState.Speaking, TimeSpan.FromSeconds(4));

        orchestrator.EnqueueShutup();
        await WaitForStateAsync(orchestrator, VoiceState.Idle, TimeSpan.FromSeconds(2));

        Assert.Equal(VoiceState.Idle, orchestrator.CurrentState);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async Task WaitForStateAsync(
        VoiceSessionOrchestrator orchestrator,
        VoiceState expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (orchestrator.CurrentState == expected)
                return;
            await Task.Delay(15);
        }

        Assert.Fail($"Timed out waiting for state '{expected}'. " +
                     $"Current='{orchestrator.CurrentState}'.");
    }

    /// <summary>
    /// IAsrService that makes a real HTTP POST to the mock VoiceHost /asr endpoint.
    /// This validates the full transport contract in integration tests.
    /// </summary>
    private sealed class HttpBackedAsrService : IAsrService, IDisposable
    {
        private readonly HttpClient _httpClient = new();
        private readonly string _baseUrl;

        public HttpBackedAsrService(string baseUrl) => _baseUrl = baseUrl;

        public async Task<string> TranscribeAsync(
            VoiceAudioClip clip,
            string sessionId,
            CancellationToken cancellationToken)
        {
            using var payload = new MultipartFormDataContent();
            var audioContent = new ByteArrayContent(clip.AudioBytes);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue(clip.ContentType);
            payload.Add(audioContent, "audio", "audio.wav");

            var response = await _httpClient.PostAsync(
                $"{_baseUrl}/asr", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("text").GetString() ?? "";
        }

        public void Dispose() => _httpClient.Dispose();
    }

    private sealed class StubCaptureService : IAudioCaptureService
    {
        public bool IsCapturing { get; private set; }

        public Task StartCaptureAsync(string sessionId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            IsCapturing = true;
            return Task.CompletedTask;
        }

        public Task<VoiceAudioClip?> StopCaptureAsync(string sessionId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            IsCapturing = false;
            return Task.FromResult<VoiceAudioClip?>(new VoiceAudioClip
            {
                AudioBytes = [1, 2, 3],
                ContentType = "audio/wav",
                SampleRateHz = 16000,
                Channels = 1,
                BitsPerSample = 16
            });
        }

        public Task AbortCaptureAsync(string sessionId, CancellationToken ct)
        {
            IsCapturing = false;
            return Task.CompletedTask;
        }
    }

    private sealed class StubPlaybackService : IAudioPlaybackService
    {
        private readonly bool _blockPlayback;
        private TaskCompletionSource<bool>? _gate;

        public StubPlaybackService(bool blockPlayback = false)
        {
            _blockPlayback = blockPlayback;
        }

        public bool IsPlaying { get; private set; }

        public async Task PlayTextAsync(string text, string sessionId, CancellationToken ct)
        {
            IsPlaying = true;
            try
            {
                if (_blockPlayback)
                {
                    _gate = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    using var reg = ct.Register(() => _gate.TrySetResult(true));
                    await _gate.Task.WaitAsync(ct);
                }
            }
            finally
            {
                IsPlaying = false;
                _gate = null;
            }
        }

        public Task StopAsync(CancellationToken ct)
        {
            IsPlaying = false;
            _gate?.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    private sealed class StubAgentService : IVoiceAgentService
    {
        private readonly string _response;
        public int CallCount { get; private set; }

        public StubAgentService(string response) => _response = response;

        public Task<VoiceAgentResponse> ProcessAsync(
            string transcript, string sessionId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new VoiceAgentResponse
            {
                Text = _response,
                Success = true
            });
        }
    }
}
