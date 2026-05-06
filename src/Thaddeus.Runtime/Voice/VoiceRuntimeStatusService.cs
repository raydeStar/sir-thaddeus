using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Thaddeus.Runtime.Settings;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Voice;

public sealed class VoiceRuntimeStatusService : IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    private readonly ISettingsStore _settings;
    private readonly VoiceHostProcessSupervisor _voiceHost;
    private readonly ILogger<VoiceRuntimeStatusService> _logger;
    private readonly HttpClient _http;

    public VoiceRuntimeStatusService(
        ISettingsStore settings,
        VoiceHostProcessSupervisor voiceHost,
        ILogger<VoiceRuntimeStatusService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _voiceHost = voiceHost ?? throw new ArgumentNullException(nameof(voiceHost));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = new HttpClient { Timeout = ProbeTimeout };
    }

    public async Task<VoiceRuntimeStatus> GetStatusAsync(bool ensureHost, CancellationToken ct)
    {
        var doc = await _settings.GetAsync(ct).ConfigureAwait(false);
        if (!doc.Voice.VoiceHostEnabled)
        {
            return VoiceRuntimeStatus.Disabled("Local VoiceHost is disabled in Voice settings.");
        }

        if (!TryBuildVoiceHostEndpoint(doc.Voice.VoiceHostBaseUrl, out var healthEndpoint, out var endpointError))
        {
            return VoiceRuntimeStatus.Misconfigured(endpointError);
        }

        var ensureErrorCode = "";
        var ensureMessage = "";
        if (ensureHost)
        {
            var ensure = await _voiceHost.EnsureResponsiveAsync(healthEndpoint, doc.Voice, ct).ConfigureAwait(false);
            if (!ensure.Success)
            {
                ensureErrorCode = ensure.ErrorCode;
                ensureMessage = ensure.Message;
                _logger.LogInformation(
                    "voice.status.ensure_failed code={Code} message={Message}",
                    ensure.ErrorCode,
                    ensure.Message);
            }
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _http
                .GetAsync(healthEndpoint, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            stopwatch.Stop();
            return ParseHealth(doc, response.StatusCode, body, (int)stopwatch.ElapsedMilliseconds, ensureErrorCode, ensureMessage);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return VoiceRuntimeStatus.Unreachable(
                "Timed out reaching VoiceHost.",
                (int)stopwatch.ElapsedMilliseconds,
                ensureErrorCode,
                ensureMessage);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return VoiceRuntimeStatus.Unreachable(
                $"Could not reach VoiceHost: {ex.Message}",
                (int)stopwatch.ElapsedMilliseconds,
                ensureErrorCode,
                ensureMessage);
        }
    }

    public void Dispose() => _http.Dispose();

    private static VoiceRuntimeStatus ParseHealth(
        SettingsDocument doc,
        HttpStatusCode statusCode,
        string body,
        int elapsedMs,
        string ensureErrorCode,
        string ensureMessage)
    {
        if (!IsSuccessStatusCode(statusCode))
        {
            return VoiceRuntimeStatus.Unreachable(
                $"VoiceHost returned HTTP {(int)statusCode}.",
                elapsedMs,
                ensureErrorCode,
                CombineMessages(ensureMessage, Trim(body, 512)));
        }

        var asrReady = false;
        var ttsReady = false;
        var ready = false;
        var status = "loading";
        var errorCode = ensureErrorCode;
        var message = ensureMessage;
        var asrError = "";
        var ttsError = "";

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                ready = ReadBool(root, "ready") ?? false;
                asrReady = ReadBool(root, "asrReady") ?? ready;
                ttsReady = ReadBool(root, "ttsReady") ?? ready;
                status = ReadString(root, "status") ?? (ready ? "ok" : "loading");
                errorCode = FirstNonEmpty(errorCode, ReadString(root, "errorCode"));
                message = CombineMessages(message, ReadString(root, "message"));
                asrError = ReadEngineError(root, "asr");
                ttsError = ReadEngineError(root, "tts");
            }
            catch (JsonException)
            {
                message = CombineMessages(message, Trim(body, 512));
            }
        }

        var ttsConfigured = doc.Audio.TtsEnabled && !UsesDisabledTtsProvider(doc.Voice.TtsProvider);
        var inputAvailable = asrReady;
        var outputAvailable = ttsConfigured && ttsReady;
        var normalizedStatus = ResolveStatus(inputAvailable, outputAvailable, asrReady, ttsReady, ttsConfigured, status);
        var normalizedMessage = ResolveMessage(normalizedStatus, message, asrError, ttsError, ttsConfigured);

        return new VoiceRuntimeStatus(
            VoiceHostEnabled: true,
            HostReachable: true,
            AsrReady: asrReady,
            TtsReady: ttsReady,
            InputAvailable: inputAvailable,
            OutputAvailable: outputAvailable,
            Status: normalizedStatus,
            Message: normalizedMessage,
            ErrorCode: string.IsNullOrWhiteSpace(errorCode) ? null : errorCode,
            Body: Trim(body, 512),
            ElapsedMs: elapsedMs);
    }

    private static string ResolveStatus(bool inputAvailable, bool outputAvailable, bool asrReady, bool ttsReady, bool ttsConfigured, string status)
    {
        if (inputAvailable && outputAvailable) return "ready";
        if (inputAvailable) return ttsConfigured ? "input-ready" : "input-ready-output-disabled";
        if (!asrReady && !ttsReady) return "starting";
        if (!asrReady) return "asr-not-ready";
        return string.IsNullOrWhiteSpace(status) ? "loading" : status.Trim().ToLowerInvariant();
    }

    private static string ResolveMessage(string status, string message, string asrError, string ttsError, bool ttsConfigured)
    {
        if (!string.IsNullOrWhiteSpace(message)) return message;

        return status switch
        {
            "ready" => "Voice input and spoken output are ready.",
            "input-ready" => string.IsNullOrWhiteSpace(ttsError)
                ? "Voice input is ready; spoken output is still warming up."
                : $"Voice input is ready; spoken output is not ready: {ttsError}",
            "input-ready-output-disabled" => "Voice input is ready; spoken output is disabled.",
            "asr-not-ready" => string.IsNullOrWhiteSpace(asrError)
                ? "Voice input is not ready yet."
                : $"Voice input is not ready: {asrError}",
            "starting" => BuildStartingMessage(asrError, ttsError, ttsConfigured),
            _ => "VoiceHost is reachable but still warming up."
        };
    }

    private static string BuildStartingMessage(string asrError, string ttsError, bool ttsConfigured)
    {
        var asr = string.IsNullOrWhiteSpace(asrError) ? "ASR warming" : $"ASR: {asrError}";
        if (!ttsConfigured) return asr;
        var tts = string.IsNullOrWhiteSpace(ttsError) ? "TTS warming" : $"TTS: {ttsError}";
        return $"{asr}; {tts}";
    }

    private static bool TryBuildVoiceHostEndpoint(string? baseUrl, out Uri endpoint, out string error)
    {
        endpoint = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            error = "No VoiceHost URL configured.";
            return false;
        }

        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/health", UriKind.Absolute, out var parsed) || parsed is null)
        {
            error = "VoiceHost URL must be an absolute URL.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "VoiceHost URL must use http or https.";
            return false;
        }

        if (!IsLoopbackHost(parsed.Host))
        {
            error = "VoiceHost URL must point to localhost or a loopback address.";
            return false;
        }

        endpoint = parsed;
        return true;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsSuccessStatusCode(HttpStatusCode statusCode)
        => (int)statusCode is >= 200 and <= 299;

    private static bool? ReadBool(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? ReadString(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ReadEngineError(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var engine) || engine.ValueKind != JsonValueKind.Object)
            return "";
        if (!engine.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Object)
            return "";
        return ReadString(details, "lastError") ?? "";
    }

    private static bool UsesDisabledTtsProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return false;
        var normalized = provider.Trim().ToLowerInvariant();
        return normalized is "stub" or "disabled" or "none";
    }

    private static string CombineMessages(params string?[] messages)
        => string.Join(" ", messages.Where(message => !string.IsNullOrWhiteSpace(message)).Select(message => message!.Trim()));

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }
}

public sealed record VoiceRuntimeStatus(
    bool VoiceHostEnabled,
    bool HostReachable,
    bool AsrReady,
    bool TtsReady,
    bool InputAvailable,
    bool OutputAvailable,
    string Status,
    string Message,
    string? ErrorCode,
    string? Body,
    int ElapsedMs)
{
    public static VoiceRuntimeStatus Disabled(string message) => new(
        VoiceHostEnabled: false,
        HostReachable: false,
        AsrReady: false,
        TtsReady: false,
        InputAvailable: false,
        OutputAvailable: false,
        Status: "disabled",
        Message: message,
        ErrorCode: "voice_host_disabled",
        Body: null,
        ElapsedMs: 0);

    public static VoiceRuntimeStatus Misconfigured(string message) => new(
        VoiceHostEnabled: true,
        HostReachable: false,
        AsrReady: false,
        TtsReady: false,
        InputAvailable: false,
        OutputAvailable: false,
        Status: "misconfigured",
        Message: message,
        ErrorCode: "voice_host_url_invalid",
        Body: null,
        ElapsedMs: 0);

    public static VoiceRuntimeStatus Unreachable(
        string message,
        int elapsedMs,
        string? errorCode = null,
        string? detail = null) => new(
        VoiceHostEnabled: true,
        HostReachable: false,
        AsrReady: false,
        TtsReady: false,
        InputAvailable: false,
        OutputAvailable: false,
        Status: "unreachable",
        Message: string.IsNullOrWhiteSpace(detail) ? message : $"{message} {detail.Trim()}",
        ErrorCode: string.IsNullOrWhiteSpace(errorCode) ? "voice_host_unreachable" : errorCode,
        Body: null,
        ElapsedMs: elapsedMs);
}