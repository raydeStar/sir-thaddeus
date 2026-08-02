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
    double Temperature,
    string? GatekeeperBaseUrl = null,
    string? GatekeeperModelId = null,
    bool ReusePrimaryForGatekeeperOnSharedEndpoint = false,
    bool GatekeeperEnabled = true,
    string ChatCompletionPath = "/v1/chat/completions",
    string? PreloadModelKey = null,
    bool EnableStartupWarmup = true,
    bool EnableKeepWarm = true,
    int ContextLength = 4096,
    bool FlashAttention = true,
    bool OffloadKvCacheToGpu = true,
    int MaxConcurrentLlmRequests = 1,
    int WarmupTimeoutSeconds = 120,
    int KeepWarmIntervalMinutes = 30,
    int MaxInputTokensSoftCap = 4000,
    int MaxOutputTokensDefault = 700,
    string? CodexCliPath = null,
    string CodexReasoningEffort = "high");

/// <summary>Voice provider configuration.</summary>
public sealed record VoiceSettings(
    string SttProvider,
    string TtsProvider,
    string? PiperVoicePath,
    string? TtsVoiceId = null,
    string? TtsModelId = null,
    string? SttModelId = null,
    string? SttLanguage = null,
    bool VoiceHostEnabled = false,
    string? VoiceHostBaseUrl = null,
    string? YoutubeAsrProvider = null,
    string? YoutubeAsrModelId = null,
    string? YoutubeLanguageHint = null,
    string? YoutubeDraftTone = null,
    bool YoutubeKeepAudio = false,
    int VoiceHostStartupTimeoutMs = 120_000);

/// <summary>Audio capture and playback controls backed by the current runtime.</summary>
public sealed record AudioSettings(
    bool TtsEnabled,
    double InputGain,
    string? InputDeviceName = null,
    string? OutputDeviceName = null);

/// <summary>Keyboard shortcut bindings.</summary>
public sealed record ShortcutSettings(
    string PushToTalk,
    string StopAll);

/// <summary>Privacy-related toggles.</summary>
public sealed record PrivacySettings(
    bool TelemetryEnabled,
    bool AllowScreenCapture,
    bool LocalOnly,
    bool OfflineMode = false);

/// <summary>
/// Per-group tool permission policy. Every MCP tool is classified into one
/// of these groups by <c>ToolGroupClassifier</c>; the group's policy decides
/// whether the call runs silently, prompts the user, or is refused outright.
///
/// Policy values (string-typed so the file stays round-trippable across
/// versions):
/// <list type="bullet">
///   <item><c>off</c> — call is rejected without prompting.</item>
///   <item><c>ask</c> — user sees a modal with Deny / Allow once / Allow for session / Allow always.</item>
///   <item><c>always</c> — call runs without prompting.</item>
/// </list>
///
/// <see cref="DeveloperOverride"/> applies to the dangerous groups
/// (screen/files/system/web) only; valid values are <c>none</c>, <c>off</c>,
/// <c>ask</c>, <c>always</c>. It does NOT affect memory groups — those stay
/// under their explicit per-group value.
///
/// <para><see cref="ToolOverrides"/> is an optional per-tool layer that
/// cascades over the per-group policy. It maps a canonical snake_case tool
/// name to <c>off</c> / <c>ask</c> / <c>always</c>; a tool absent from the
/// map inherits its group's effective policy. Resolution is most-specific
/// wins: a per-tool override beats the group policy (and the developer
/// override) so a single tool can be turned off even when its whole group is
/// <c>always</c>. Dynamic safety modes (offline / panic / safe) still beat
/// every static override. Absent/empty is serialized as omitted.</para>
/// </summary>
public sealed record PermissionsSettings(
    string DeveloperOverride,
    string Screen,
    string Files,
    string System,
    string Web,
    string MemoryRead,
    string MemoryWrite,
    // Concrete Dictionary (not IReadOnlyDictionary) so the STJ source
    // generator that roots SettingsDocument emits converter metadata for it.
    // CamelCase naming policy does not transform dictionary keys, so the
    // canonical snake_case tool names round-trip verbatim.
    Dictionary<string, string>? ToolOverrides = null);

/// <summary>
/// Local-filesystem access policy. Every path a file tool touches must be
/// under one of <see cref="AllowedRoots"/>; calls outside fail with a
/// clear error. <see cref="DisableAllFileAccess"/> is a hard kill switch
/// that overrides the roots list — used when the user wants the tools
/// installed but inert (e.g. on a shared machine).
/// </summary>
/// <param name="AllowedRoots">Absolute paths Sir Thaddeus may read from or list.</param>
/// <param name="DisableAllFileAccess">When true, all file tools refuse regardless of roots.</param>
/// <param name="MaxDefaultCharsPerRead">Chars returned per read call before truncation.</param>
public sealed record FilesSettings(
    IReadOnlyList<string> AllowedRoots,
    bool DisableAllFileAccess,
    int MaxDefaultCharsPerRead)
{
    // Default record equality reference-compares AllowedRoots, which breaks
    // round-trip tests: JSON deserialization produces a List<string> while
    // Defaults() creates a string[] — same contents, different reference.
    // Treat two FilesSettings as equal when AllowedRoots has the same ordered
    // elements (ordinal string compare), regardless of concrete list type.
    public bool Equals(FilesSettings? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return DisableAllFileAccess == other.DisableAllFileAccess
            && MaxDefaultCharsPerRead == other.MaxDefaultCharsPerRead
            && AllowedRootsEqual(AllowedRoots, other.AllowedRoots);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(DisableAllFileAccess);
        hash.Add(MaxDefaultCharsPerRead);
        if (AllowedRoots is not null)
        {
            foreach (var root in AllowedRoots) hash.Add(root, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    private static bool AllowedRootsEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }
}

/// <summary>Application-level flags (onboarding completion, etc.).</summary>
public sealed record AppFlags(
    bool OnboardingCompleted);

/// <summary>User location (manual city/ZIP/country text) and display preferences.</summary>
public sealed record LocationSettings(
    string? ManualLocation,
    bool Use24HourTime,
    string PreferredUnits);

/// <summary>Guardrails for tool-heavy sessions. Persisted but not yet enforced.</summary>
public sealed record LimitsSettings(
    int MaxToolCallsPerTurn,
    int MaxToolCallsPerSession,
    int MaxWebPullsPerTurn,
    int MaxFileOpsPerMinute);

/// <summary>Desktop shell behavior toggles.</summary>
public sealed record UiPreferencesSettings(
    bool SendOnEnter,
    bool AutoSwitchToPermissions,
    bool AutoConnectOnStartup,
    bool AutoStartLocalRuntime,
    bool MinimizeToTrayOnClose);

/// <summary>Master policy for durable assistant memory.</summary>
public sealed record RuntimeMemorySettings(bool Enabled);

/// <summary>User policy for model-dependent capabilities.</summary>
public sealed record ModelCapabilitySettings(
    string WikiWriteMode = "on",
    IReadOnlyList<ModelCapabilityCertificate>? WikiWriteCertificates = null,
    IReadOnlyList<ModelCapabilityPreference>? Preferences = null,
    IReadOnlyList<ModelCapabilityCertificate>? Certificates = null)
{
    public bool Equals(ModelCapabilitySettings? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(WikiWriteMode, other.WikiWriteMode, StringComparison.Ordinal)
            && CertificatesEqual(WikiWriteCertificates, other.WikiWriteCertificates)
            && SequenceEqual(Preferences, other.Preferences)
            && CertificatesEqual(Certificates, other.Certificates);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(WikiWriteMode, StringComparer.Ordinal);
        AddCertificates(ref hash, WikiWriteCertificates);
        if (Preferences is not null)
        {
            foreach (var preference in Preferences)
            {
                hash.Add(preference.Capability, StringComparer.Ordinal);
                hash.Add(preference.Mode, StringComparer.Ordinal);
            }
        }
        AddCertificates(ref hash, Certificates);
        return hash.ToHashCode();
    }

    private static void AddCertificates(ref HashCode hash, IReadOnlyList<ModelCapabilityCertificate>? certificates)
    {
        if (certificates is null) return;
        foreach (var certificate in certificates)
        {
            hash.Add(certificate.Capability, StringComparer.Ordinal);
            hash.Add(certificate.Status, StringComparer.Ordinal);
            hash.Add(certificate.ConfigurationFingerprint, StringComparer.Ordinal);
            hash.Add(certificate.TestedAt);
        }
    }

    private static bool SequenceEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (left is null || left.Count == 0) return right is null || right.Count == 0;
        return right is not null && left.SequenceEqual(right);
    }

    private static bool CertificatesEqual(
        IReadOnlyList<ModelCapabilityCertificate>? left,
        IReadOnlyList<ModelCapabilityCertificate>? right)
    {
        if (left is null || left.Count == 0) return right is null || right.Count == 0;
        if (right is null || left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            var a = left[index];
            var b = right[index];
            if (!string.Equals(a.Capability, b.Capability, StringComparison.Ordinal) ||
                !string.Equals(a.Status, b.Status, StringComparison.Ordinal) ||
                !string.Equals(a.ConfigurationFingerprint, b.ConfigurationFingerprint, StringComparison.Ordinal) ||
                !string.Equals(a.ConfiguredModelId, b.ConfiguredModelId, StringComparison.Ordinal) ||
                !string.Equals(a.ReportedModelId, b.ReportedModelId, StringComparison.Ordinal) ||
                !string.Equals(a.ProbeVersion, b.ProbeVersion, StringComparison.Ordinal) ||
                a.ModelCalls != b.ModelCalls ||
                a.ElapsedMilliseconds != b.ElapsedMilliseconds ||
                a.TestedAt != b.TestedAt ||
                !a.Probes.SequenceEqual(b.Probes))
                return false;
        }
        return true;
    }
}

/// <summary>User policy for one registered model-dependent capability.</summary>
public sealed record ModelCapabilityPreference(string Capability, string Mode);

/// <summary>Cached result of an explicit synthetic capability retest.</summary>
public sealed record ModelCapabilityCertificate(
    string Capability,
    string Status,
    string ConfigurationFingerprint,
    string ConfiguredModelId,
    string? ReportedModelId,
    string ProbeVersion,
    int ModelCalls,
    long ElapsedMilliseconds,
    DateTimeOffset TestedAt,
    IReadOnlyList<ModelCapabilityProbeResult> Probes);

/// <summary>One deterministic check within a capability certificate.</summary>
public sealed record ModelCapabilityProbeResult(string Id, bool Passed, string Reason);

/// <summary>Top-level settings document.</summary>
public sealed record SettingsDocument(
    LlmSettings Llm,
    VoiceSettings Voice,
    AudioSettings Audio,
    ShortcutSettings Shortcuts,
    PrivacySettings Privacy,
    AppFlags Flags,
    LocationSettings? Location = null,
    LimitsSettings? Limits = null,
    UiPreferencesSettings? UiPrefs = null,
    PermissionsSettings? Permissions = null,
    FilesSettings? Files = null,
    RuntimeMemorySettings? Memory = null,
    ModelCapabilitySettings? ModelCapabilities = null)
{
    /// <summary>Defaults applied when no settings file exists yet.</summary>
    public static SettingsDocument Defaults() => new(
        Llm: new LlmSettings(
            Provider: "lmstudio",
            ModelId: "auto",
            BaseUrl: "http://127.0.0.1:1234/v1",
            ApiKey: null,
            MaxTokens: 4096,
            ContextWindowTokens: 16384,
            Temperature: 0.7,
            GatekeeperBaseUrl: null,
            GatekeeperModelId: null,
            ReusePrimaryForGatekeeperOnSharedEndpoint: false,
            EnableStartupWarmup: true,
            EnableKeepWarm: true,
            ContextLength: 4096,
            MaxConcurrentLlmRequests: 1,
            WarmupTimeoutSeconds: 120,
            KeepWarmIntervalMinutes: 30,
            MaxInputTokensSoftCap: 4000,
            MaxOutputTokensDefault: 700),
        Voice: new VoiceSettings(
            SttProvider: "whisper-cpp",
            TtsProvider: "kokoro-sharp",
            PiperVoicePath: null,
            TtsVoiceId: "bm_lewis",
            TtsModelId: null,
            SttModelId: "base",
            SttLanguage: "en",
            VoiceHostEnabled: false,
            VoiceHostBaseUrl: "http://127.0.0.1:17845",
            YoutubeAsrProvider: "faster-whisper",
            YoutubeAsrModelId: "base",
            YoutubeLanguageHint: "en-us",
            YoutubeDraftTone: "professional",
            YoutubeKeepAudio: false,
            VoiceHostStartupTimeoutMs: 120_000),
        Audio: new AudioSettings(
            TtsEnabled: true,
            InputGain: 1.0,
            InputDeviceName: null,
            OutputDeviceName: null),
        Shortcuts: new ShortcutSettings(
            PushToTalk: "Ctrl+Alt+M",
            StopAll: "Ctrl+Alt+Esc"),
        Privacy: new PrivacySettings(
            TelemetryEnabled: false,
            AllowScreenCapture: false,
            LocalOnly: true),
        Flags: new AppFlags(
            OnboardingCompleted: false),
        Location: new LocationSettings(
            ManualLocation: null,
            Use24HourTime: false,
            PreferredUnits: "imperial"),
        Limits: new LimitsSettings(
            MaxToolCallsPerTurn: 12,
            MaxToolCallsPerSession: 200,
            MaxWebPullsPerTurn: 12,
            MaxFileOpsPerMinute: 30),
        UiPrefs: new UiPreferencesSettings(
            SendOnEnter: true,
            AutoSwitchToPermissions: true,
            AutoConnectOnStartup: true,
            AutoStartLocalRuntime: true,
            MinimizeToTrayOnClose: true),
        Permissions: new PermissionsSettings(
            DeveloperOverride: "none",
            // Sensitive groups default to "ask" — the user approves each new
            // call once, then can Allow Always to upgrade the group.
            Screen: "ask",
            Files: "ask",
            System: "ask",
            Web: "ask",
            // Memory reads are frequent + harmless (local SQLite lookup).
            // Memory writes touch durable state, so they still prompt.
            MemoryRead: "always",
            MemoryWrite: "ask"),
        Files: new FilesSettings(
            // On fresh installs we seed the user's Documents folder so the
            // common case ("read this doc") works without requiring the user
            // to crack open Settings first. They can still clear the list
            // to opt out, or add more folders.
            AllowedRoots: new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray(),
            DisableAllFileAccess: false,
            MaxDefaultCharsPerRead: 4000),
        Memory: new RuntimeMemorySettings(Enabled: true),
        // Preserve existing behavior until the user explicitly selects Auto.
        ModelCapabilities: new ModelCapabilitySettings(WikiWriteMode: "on"));
}
