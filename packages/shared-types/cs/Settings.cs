// Hand-mirrored from packages/shared-types/ts/src/settings.ts.
// Settings document for the v2 hybrid runtime. Persisted to
// ~/.thaddeus/runtime-settings.json. Distinct from the legacy
// SirThaddeus settings.json so the two surfaces never collide.

namespace Thaddeus.SharedTypes;

/// <summary>LLM provider configuration.</summary>
public sealed record LlmSettings(
    string Provider,
    string ModelId,
    string? BaseUrl,
    string? ApiKey,
    int MaxTokens,
    int ContextWindowTokens,
    double Temperature);

/// <summary>Voice provider configuration.</summary>
public sealed record VoiceSettings(
    string SttProvider,
    string TtsProvider,
    string? PiperVoicePath);

/// <summary>Audio capture and playback controls backed by the current runtime.</summary>
public sealed record AudioSettings(
    bool TtsEnabled,
    double InputGain);

/// <summary>Keyboard shortcut bindings.</summary>
public sealed record ShortcutSettings(
    string PushToTalk,
    string StopAll);

/// <summary>Privacy-related toggles.</summary>
public sealed record PrivacySettings(
    bool TelemetryEnabled,
    bool AllowScreenCapture,
    bool LocalOnly);

/// <summary>Application-level flags (onboarding completion, etc.).</summary>
public sealed record AppFlags(
    bool OnboardingCompleted);

/// <summary>Top-level settings document.</summary>
public sealed record SettingsDocument(
    LlmSettings Llm,
    VoiceSettings Voice,
    AudioSettings Audio,
    ShortcutSettings Shortcuts,
    PrivacySettings Privacy,
    AppFlags Flags)
{
    /// <summary>Defaults applied when no settings file exists yet.</summary>
    public static SettingsDocument Defaults() => new(
        Llm: new LlmSettings(
            Provider: "lmstudio",
            ModelId: "auto",
            BaseUrl: "http://127.0.0.1:1234/v1",
            ApiKey: null,
            MaxTokens: 2048,
            ContextWindowTokens: 8192,
            Temperature: 0.7),
        Voice: new VoiceSettings(
            SttProvider: "whisper-cpp",
            TtsProvider: "piper",
            PiperVoicePath: null),
        Audio: new AudioSettings(
            TtsEnabled: true,
            InputGain: 1.0),
        Shortcuts: new ShortcutSettings(
            PushToTalk: "Ctrl+Shift+Space",
            StopAll: "Ctrl+Shift+Esc"),
        Privacy: new PrivacySettings(
            TelemetryEnabled: false,
            AllowScreenCapture: false,
            LocalOnly: true),
        Flags: new AppFlags(
            OnboardingCompleted: false));
}
