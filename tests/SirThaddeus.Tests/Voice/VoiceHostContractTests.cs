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
