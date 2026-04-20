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
    string? ApiKey);

/// <summary>Voice provider configuration.</summary>
public sealed record VoiceSettings(
    string SttProvider,
    string TtsProvider,
    string? PiperVoicePath);

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
    ShortcutSettings Shortcuts,
    PrivacySettings Privacy,
    AppFlags Flags)
{
    /// <summary>Defaults applied when no settings file exists yet.</summary>
    public static SettingsDocument Defaults() => new(
        Llm: new LlmSettings(
            Provider: "ollama",
            ModelId: "llama3.1:8b",
            BaseUrl: "http://127.0.0.1:11434",
            ApiKey: null),
        Voice: new VoiceSettings(
            SttProvider: "whisper-cpp",
            TtsProvider: "piper",
            PiperVoicePath: null),
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
