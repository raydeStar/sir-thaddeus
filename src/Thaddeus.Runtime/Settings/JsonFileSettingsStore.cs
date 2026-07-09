using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Settings;

/// <summary>
/// File-backed settings store. The whole document lives in a single
/// JSON file; mutations are serialized through a private semaphore and
/// written via a temp-file-then-replace pattern to prevent torn writes.
/// </summary>
public sealed class JsonFileSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly ILogger<JsonFileSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SettingsDocument? _cached;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public JsonFileSettingsStore(string filePath, ILogger<JsonFileSettingsStore> logger)
    {
        _path = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Test seam — returns the file path settings are persisted to.</summary>
    public string FilePath => _path;

    public event Action<SettingsDocument>? Changed;

    public async Task<SettingsDocument> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null) return _cached;
            _cached = await LoadFromDiskAsync(ct).ConfigureAwait(false);
            return _cached;
        }
        finally { _gate.Release(); }
    }

    public async Task<SettingsDocument> ReplaceAsync(SettingsDocument document, CancellationToken ct)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        var normalized = Normalize(document);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(normalized, ct).ConfigureAwait(false);
            _cached = normalized;
        }
        finally { _gate.Release(); }

        // Raise the event after lock release so handlers do not run under the gate.
        Changed?.Invoke(normalized);
        _logger.LogInformation("settings.replaced provider={Provider} model={Model}",
            normalized.Llm.Provider, normalized.Llm.ModelId);
        return normalized;
    }

    private async Task<SettingsDocument> LoadFromDiskAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            _logger.LogInformation("settings.defaults_used path={Path}", _path);
            return SettingsDocument.Defaults();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var doc = await JsonSerializer
                .DeserializeAsync<SettingsDocument>(stream, s_jsonOptions, ct)
                .ConfigureAwait(false);
            return Normalize(doc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "settings.load_failed path={Path} returning_defaults=true", _path);
            return SettingsDocument.Defaults();
        }
    }

    private async Task WriteAtomicAsync(SettingsDocument document, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer
                .SerializeAsync(stream, document, s_jsonOptions, ct)
                .ConfigureAwait(false);
        }

        if (File.Exists(_path)) File.Replace(tempPath, _path, destinationBackupFileName: null);
        else File.Move(tempPath, _path);
    }

    private static SettingsDocument Normalize(SettingsDocument? document)
    {
        if (document is null) return SettingsDocument.Defaults();

        var defaults = SettingsDocument.Defaults();
        var llm = document.Llm ?? defaults.Llm;
        var voice = document.Voice ?? defaults.Voice;
        var audio = document.Audio ?? defaults.Audio;
        var shortcuts = document.Shortcuts ?? defaults.Shortcuts;
        var privacy = document.Privacy ?? defaults.Privacy;
        var flags = document.Flags ?? defaults.Flags;
        var location = document.Location ?? defaults.Location!;
        var limits = document.Limits ?? defaults.Limits!;
        var uiPrefs = document.UiPrefs ?? defaults.UiPrefs!;
        var permissions = document.Permissions ?? defaults.Permissions!;
        var files = document.Files ?? defaults.Files!;
        var hasLegacyMissingAdvancedLlmFields = llm.MaxTokens <= 0 || llm.ContextWindowTokens <= 0;
        var ttsProvider = NormalizeTtsProvider(voice.TtsProvider, defaults.Voice.TtsProvider, voice.PiperVoicePath);
        return document with
        {
            Llm = llm with
            {
                MaxTokens = llm.MaxTokens > 0 ? llm.MaxTokens : defaults.Llm.MaxTokens,
                ContextWindowTokens = llm.ContextWindowTokens > 0
                    ? llm.ContextWindowTokens
                    : defaults.Llm.ContextWindowTokens,
                Temperature = llm.Temperature is >= 0 and <= 2
                    && !(hasLegacyMissingAdvancedLlmFields && llm.Temperature == 0)
                    ? llm.Temperature
                    : defaults.Llm.Temperature,
            },
            Voice = voice with
            {
                SttProvider = string.IsNullOrWhiteSpace(voice.SttProvider)
                    ? defaults.Voice.SttProvider
                    : voice.SttProvider,
                TtsProvider = ttsProvider,
                TtsVoiceId = NormalizeTtsVoiceId(ttsProvider, voice.TtsVoiceId, defaults.Voice.TtsVoiceId),
                SttModelId = NormalizeSttModelId(voice.SttModelId, defaults.Voice.SttModelId),
                VoiceHostStartupTimeoutMs = voice.VoiceHostStartupTimeoutMs is >= 30_000 and <= 300_000
                    ? voice.VoiceHostStartupTimeoutMs
                    : defaults.Voice.VoiceHostStartupTimeoutMs,
            },
            Audio = audio with
            {
                InputGain = audio.InputGain is >= 0 and <= 2 ? audio.InputGain : defaults.Audio.InputGain,
            },
            Shortcuts = shortcuts with
            {
                PushToTalk = NormalizePushToTalkShortcut(shortcuts.PushToTalk, defaults.Shortcuts.PushToTalk),
                StopAll = NormalizeStopAllShortcut(shortcuts.StopAll, defaults.Shortcuts.StopAll),
            },
            Privacy = privacy,
            Flags = flags,
            Location = location with
            {
                PreferredUnits = string.IsNullOrWhiteSpace(location.PreferredUnits)
                    ? defaults.Location!.PreferredUnits
                    : location.PreferredUnits,
            },
            Limits = limits with
            {
                MaxToolCallsPerTurn = limits.MaxToolCallsPerTurn > 0
                    ? limits.MaxToolCallsPerTurn
                    : defaults.Limits!.MaxToolCallsPerTurn,
                MaxToolCallsPerSession = limits.MaxToolCallsPerSession > 0
                    ? limits.MaxToolCallsPerSession
                    : defaults.Limits!.MaxToolCallsPerSession,
                MaxWebPullsPerTurn = limits.MaxWebPullsPerTurn > 0
                    ? limits.MaxWebPullsPerTurn
                    : defaults.Limits!.MaxWebPullsPerTurn,
                MaxFileOpsPerMinute = limits.MaxFileOpsPerMinute > 0
                    ? limits.MaxFileOpsPerMinute
                    : defaults.Limits!.MaxFileOpsPerMinute,
            },
            UiPrefs = uiPrefs,
            Permissions = permissions with
            {
                DeveloperOverride = NormalizeOverride(permissions.DeveloperOverride),
                Screen = NormalizePolicy(permissions.Screen, defaults.Permissions!.Screen),
                Files = NormalizePolicy(permissions.Files, defaults.Permissions!.Files),
                System = NormalizePolicy(permissions.System, defaults.Permissions!.System),
                Web = NormalizePolicy(permissions.Web, defaults.Permissions!.Web),
                MemoryRead = NormalizePolicy(permissions.MemoryRead, defaults.Permissions!.MemoryRead),
                MemoryWrite = NormalizePolicy(permissions.MemoryWrite, defaults.Permissions!.MemoryWrite),
                ToolOverrides = NormalizeToolOverrides(permissions.ToolOverrides),
            },
            Files = files with
            {
                AllowedRoots = NormalizeAllowedRoots(files.AllowedRoots),
                MaxDefaultCharsPerRead = files.MaxDefaultCharsPerRead > 0
                    ? files.MaxDefaultCharsPerRead
                    : defaults.Files!.MaxDefaultCharsPerRead,
            },
        };
    }

    private static IReadOnlyList<string> NormalizeAllowedRoots(IReadOnlyList<string>? roots)
    {
        if (roots is null || roots.Count == 0) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(roots.Count);
        foreach (var raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var full = Path.GetFullPath(raw.Trim());
                if (seen.Add(full)) result.Add(full);
            }
            catch
            {
                // Skip malformed paths silently — the UI validates on write.
            }
        }
        return result;
    }

    private static string NormalizePolicy(string? value, string fallback)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return v is "off" or "ask" or "always" ? v : fallback;
    }

    /// <summary>
    /// Normalizes the optional per-tool override map: drops null/empty keys,
    /// canonicalizes keys via <c>AuditedMcpToolClient.Canonicalize</c> so only
    /// canonical snake_case names are stored, keeps only valid {off, ask,
    /// always} values (invalid entries are dropped, not defaulted), dedupes
    /// case-insensitively, and returns null for an empty result so the file
    /// (WhenWritingNull) never contains an empty "toolOverrides": {}.
    /// </summary>
    private static Dictionary<string, string>? NormalizeToolOverrides(
        Dictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0) return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in overrides)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key)) continue;
            var value = (kvp.Value ?? "").Trim().ToLowerInvariant();
            if (value is not ("off" or "ask" or "always")) continue;
            var canonical = SirThaddeus.Agent.AuditedMcpToolClient.Canonicalize(kvp.Key);
            result[canonical] = value;
        }

        return result.Count == 0 ? null : result;
    }

    private static string NormalizeTtsProvider(string? value, string fallback, string? piperVoicePath)
    {
        var provider = (value ?? "").Trim().ToLowerInvariant();
        return provider switch
        {
            "" => fallback,
            "kokoro" => "kokoro-sharp",
            "kokoro-sharp" => "kokoro-sharp",
            "kokorosharp" => "kokoro-sharp",
            "piper" => string.IsNullOrWhiteSpace(piperVoicePath) ? fallback : "piper",
            "stub" or "disabled" or "none" => "stub",
            "windows" or "sapi" or "windows-sapi" => "kokoro-sharp",
            _ => fallback,
        };
    }

    private static string? NormalizeTtsVoiceId(string provider, string? value, string? fallback)
    {
        var voiceId = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        if (string.Equals(provider, "kokoro-sharp", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(voiceId) || voiceId.Contains('-') || !voiceId.Contains('_')
                ? fallback
                : voiceId;

        if (string.Equals(provider, "piper", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(voiceId) || !voiceId.Contains('-')
                ? "en_US-john-medium"
                : voiceId;

        return string.IsNullOrWhiteSpace(voiceId) ? fallback : voiceId;
    }

    private static string? NormalizeSttModelId(string? value, string? fallback)
    {
        var modelId = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        if (string.IsNullOrWhiteSpace(modelId)) return fallback;
        if (modelId.Contains("qwen", StringComparison.OrdinalIgnoreCase)) return fallback;
        return modelId;
    }

    private static string NormalizeStopAllShortcut(string? value, string fallback)
    {
        var shortcut = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return shortcut.Equals("Ctrl+Shift+Esc", StringComparison.OrdinalIgnoreCase)
            || shortcut.Equals("Ctrl+Shift+Escape", StringComparison.OrdinalIgnoreCase)
            ? fallback
            : shortcut;
    }

    private static string NormalizePushToTalkShortcut(string? value, string fallback)
    {
        var shortcut = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return shortcut.Equals("Ctrl+Shift+Space", StringComparison.OrdinalIgnoreCase)
            || shortcut.Equals("Ctrl+Alt+Space", StringComparison.OrdinalIgnoreCase)
            ? fallback
            : shortcut;
    }

    private static string NormalizeOverride(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return v is "none" or "off" or "ask" or "always" ? v : "none";
    }
}
