using System.Net;

namespace SirThaddeus.VoiceHost;

public sealed record VoiceHostRuntimeOptions(
    IPAddress BindIp,
    int Port,
    string BackendMode,
    Uri AsrUpstreamUri,
    Uri TtsUpstreamUri,
    bool AutoStartBackends,
    string BackendExecutablePath,
    int BackendStartupTimeoutMs,
    int BackendShutdownGraceMs)
{
    public static VoiceHostRuntimeOptions Parse(string[] args)
    {
        var values = ParseArgs(args);

        var bindRaw = GetOrDefault(values, "bind", "127.0.0.1");
        var portRaw = GetOrDefault(values, "port", "17845");
        var asrRaw = GetOrDefault(
            values,
            "asr-upstream",
            Environment.GetEnvironmentVariable("ST_VOICEHOST_ASR_UPSTREAM") ?? "http://127.0.0.1:8001/asr");
        // Default TTS upstream shares the ASR backend port (8001) so a single
        // voice-backend process serves both endpoints. Override with --tts-upstream
        // or ST_VOICEHOST_TTS_UPSTREAM when running a dedicated TTS service.
        var ttsRaw = GetOrDefault(
            values,
            "tts-upstream",
            Environment.GetEnvironmentVariable("ST_VOICEHOST_TTS_UPSTREAM") ?? "http://127.0.0.1:8001/tts");
        var modeRaw = GetOrDefault(
            values,
            "mode",
            Environment.GetEnvironmentVariable("ST_VOICEHOST_MODE") ?? "proxy-first");
        var autoStartRaw = GetOrDefault(
            values,
            "autostart-backends",
            Environment.GetEnvironmentVariable("ST_VOICEHOST_BACKEND_AUTOSTART") ?? "true");
        var backendExeRaw = GetOrDefault(
            values,
            "backend-exe",
            Environment.GetEnvironmentVariable("ST_VOICEHOST_BACKEND_EXE") ?? "auto");
        var backendStartupTimeoutRaw = GetOrDefault(
            values,
            "backend-startup-timeout-ms",
            Environment.GetEnvironmentVariable("ST_VOICEHOST_BACKEND_STARTUP_TIMEOUT_MS") ?? "15000");
        var backendShutdownGraceRaw = GetOrDefault(
            values,
            "backend-shutdown-grace-ms",
            Environment.GetEnvironmentVariable("ST_VOICEHOST_BACKEND_SHUTDOWN_GRACE_MS") ?? "2500");

        if (!int.TryParse(portRaw, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("Invalid --port value.");

        var bindIp = ParseLoopbackOnlyAddress(bindRaw);
        var backendMode = ParseBackendMode(modeRaw);
        var asrUri = ParseAbsoluteUri(asrRaw, "--asr-upstream", requireLoopback: true);
        var ttsUri = ParseAbsoluteUri(ttsRaw, "--tts-upstream", requireLoopback: true);
        var autoStartBackends = ParseBool(autoStartRaw, fallback: true);
        var backendStartupTimeoutMs = ParseInt(
            backendStartupTimeoutRaw,
            fallback: 15_000,
            min: 1_000,
            max: 120_000,
            argName: "--backend-startup-timeout-ms");
        var backendShutdownGraceMs = ParseInt(
            backendShutdownGraceRaw,
            fallback: 2_500,
            min: 250,
            max: 30_000,
            argName: "--backend-shutdown-grace-ms");

        return new VoiceHostRuntimeOptions(
            bindIp,
            port,
            backendMode,
            asrUri,
            ttsUri,
            autoStartBackends,
            backendExeRaw,
            backendStartupTimeoutMs,
            backendShutdownGraceMs);
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                continue;

            var key = token[2..];
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                map[key] = args[i + 1];
                i++;
            }
            else
            {
                map[key] = "true";
            }
        }

        return map;
    }

    private static string GetOrDefault(Dictionary<string, string> values, string key, string fallback)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;

    private static IPAddress ParseLoopbackOnlyAddress(string raw)
    {
        if (string.Equals(raw, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;

        if (!IPAddress.TryParse(raw, out var ip))
            throw new InvalidOperationException("Invalid --bind address.");

        if (!IPAddress.IsLoopback(ip))
            throw new InvalidOperationException("VoiceHost may only bind to loopback.");

        return ip;
    }

    private static Uri ParseAbsoluteUri(string raw, string argName, bool requireLoopback)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Invalid {argName} value.");

        if (requireLoopback)
        {
            var isLoopback =
                string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
            if (!isLoopback)
            {
                throw new InvalidOperationException($"{argName} must target loopback.");
            }
        }
        return uri;
    }

    private static string ParseBackendMode(string raw)
    {
        var value = string.IsNullOrWhiteSpace(raw) ? "proxy-first" : raw.Trim();
        if (!string.Equals(value, "proxy-first", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("VoiceHost V1 supports only --mode proxy-first.");
        return "proxy-first";
    }

    private static bool ParseBool(string raw, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };
    }

    private static int ParseInt(string raw, int fallback, int min, int max, string argName)
    {
        if (!int.TryParse(raw, out var parsed))
            return fallback;
        if (parsed < min || parsed > max)
            throw new InvalidOperationException($"Invalid {argName} value.");
        return parsed;
    }
}
