using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Settings;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tests;

public sealed class JsonFileSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;

    public JsonFileSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "thaddeus-settings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task GetAsync_returns_defaults_when_file_missing()
    {
        var store = NewStore();
        var doc = await store.GetAsync(CancellationToken.None);

        var defaults = SettingsDocument.Defaults();
        Assert.Equal(defaults, doc);
        Assert.Null(doc.Llm.GatekeeperModelId);
    }

    [Fact]
    public async Task ReplaceAsync_persists_and_round_trips()
    {
        var store = NewStore();
        var updated = SettingsDocument.Defaults() with
        {
            Llm = new LlmSettings("openai", "gpt-4o-mini", "https://api.openai.com", "sk-test", 4096, 16384, 0.2),
            Audio = new AudioSettings(false, 1.35),
            Privacy = new PrivacySettings(true, true, false),
        };

        await store.ReplaceAsync(updated, CancellationToken.None);

        // New store reads the same path and sees the changes.
        var fresh = NewStore();
        var roundTripped = await fresh.GetAsync(CancellationToken.None);
        Assert.Equal(updated, roundTripped);
    }

    [Fact]
    public async Task ReplaceAsync_raises_changed_event()
    {
        var store = NewStore();
        SettingsDocument? observed = null;
        store.Changed += d => observed = d;

        var updated = SettingsDocument.Defaults() with
        {
            Shortcuts = new ShortcutSettings("Ctrl+Alt+M", "Ctrl+Alt+Esc"),
        };
        await store.ReplaceAsync(updated, CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal(updated.Shortcuts, observed!.Shortcuts);
    }

    [Fact]
    public async Task ReplaceAsync_round_trips_model_capability_certificates_by_value()
    {
        var defaults = SettingsDocument.Defaults();
        var certificate = new ModelCapabilityCertificate(
            ModelCapabilityPolicy.WikiWriteCapability,
            "certified",
            ModelCapabilityPolicy.CreateConfigurationFingerprint(defaults.Llm, defaults.Llm.ModelId),
            defaults.Llm.ModelId,
            defaults.Llm.ModelId,
            ModelCapabilityPolicy.ProbeVersion,
            4,
            1234,
            DateTimeOffset.UtcNow,
            [new ModelCapabilityProbeResult("exact_page_update", true, "pass")]);
        var updated = defaults with
        {
            ModelCapabilities = new ModelCapabilitySettings("auto", [certificate]),
        };

        await NewStore().ReplaceAsync(updated, CancellationToken.None);
        var roundTripped = await NewStore().GetAsync(CancellationToken.None);

        Assert.Equal(updated, roundTripped);
    }

    [Fact]
    public async Task ReplaceAsync_normalizes_generic_capability_preferences_and_certificates()
    {
        var defaults = SettingsDocument.Defaults();
        var certificate = new ModelCapabilityCertificate(
            "Structured-Output",
            "certified",
            "fingerprint",
            defaults.Llm.ModelId,
            defaults.Llm.ModelId,
            "structured-v1",
            1,
            10,
            DateTimeOffset.UtcNow,
            [new ModelCapabilityProbeResult("json", true, "pass")],
            "auto");
        var updated = defaults with
        {
            ModelCapabilities = new ModelCapabilitySettings(
                Preferences:
                [
                    new ModelCapabilityPreference("structured-output", "off"),
                    new ModelCapabilityPreference("Structured-Output", "AUTO"),
                ],
                Certificates: [certificate]),
        };

        await NewStore().ReplaceAsync(updated, CancellationToken.None);
        var roundTripped = await NewStore().GetAsync(CancellationToken.None);

        var capabilities = roundTripped.ModelCapabilities!;
        var preference = Assert.Single(capabilities.Preferences!);
        Assert.Equal("structured_output", preference.Capability);
        Assert.Equal("auto", preference.Mode);
        var savedCertificate = Assert.Single(capabilities.Certificates!);
        Assert.Equal("structured_output", savedCertificate.Capability);
        Assert.Equal("auto", savedCertificate.SelectedMode);
    }

    [Fact]
    public async Task GetAsync_returns_defaults_on_corrupt_file()
    {
        var path = Path.Combine(_tempDir, "runtime-settings.json");
        await File.WriteAllTextAsync(path, "{ this isn't valid json");
        var store = new JsonFileSettingsStore(path, NullLogger<JsonFileSettingsStore>.Instance);

        var doc = await store.GetAsync(CancellationToken.None);

        Assert.Equal(SettingsDocument.Defaults(), doc);
    }

        [Fact]
        public async Task GetAsync_backfills_new_llm_fields_for_older_files()
        {
                var path = Path.Combine(_tempDir, "runtime-settings.json");
                var legacyLike = """
                {
                    "llm": {
                        "provider": "lmstudio",
                        "modelId": "auto",
                        "baseUrl": "http://127.0.0.1:1234/v1",
                        "apiKey": null
                    },
                    "voice": {
                        "sttProvider": "whisper-cpp",
                        "ttsProvider": "piper",
                        "piperVoicePath": null
                    },
                    "shortcuts": {
                        "pushToTalk": "Ctrl+Shift+Space",
                        "stopAll": "Ctrl+Shift+Esc"
                    },
                    "privacy": {
                        "telemetryEnabled": false,
                        "allowScreenCapture": false,
                        "localOnly": true
                    },
                    "flags": {
                        "onboardingCompleted": false
                    }
                }
                """;
                await File.WriteAllTextAsync(path, legacyLike);
                var store = new JsonFileSettingsStore(path, NullLogger<JsonFileSettingsStore>.Instance);

                var doc = await store.GetAsync(CancellationToken.None);

                Assert.Equal(SettingsDocument.Defaults().Llm.MaxTokens, doc.Llm.MaxTokens);
                Assert.Equal(SettingsDocument.Defaults().Llm.ContextWindowTokens, doc.Llm.ContextWindowTokens);
                Assert.Equal(SettingsDocument.Defaults().Llm.Temperature, doc.Llm.Temperature);
                Assert.Equal("kokoro-sharp", doc.Voice.TtsProvider);
                Assert.Equal("bm_lewis", doc.Voice.TtsVoiceId);
                Assert.Equal("base", doc.Voice.SttModelId);
                Assert.Equal(120_000, doc.Voice.VoiceHostStartupTimeoutMs);
                Assert.Equal(SettingsDocument.Defaults().Audio.TtsEnabled, doc.Audio.TtsEnabled);
                Assert.Equal(SettingsDocument.Defaults().Audio.InputGain, doc.Audio.InputGain);
        }

    [Fact]
    public async Task GetAsync_clamps_invalid_audio_gain()
    {
        var path = Path.Combine(_tempDir, "runtime-settings.json");
        var invalidAudio = """
        {
            "llm": {
                "provider": "lmstudio",
                "modelId": "auto",
                "baseUrl": "http://127.0.0.1:1234/v1",
                "apiKey": null,
                "maxTokens": 2048,
                "contextWindowTokens": 8192,
                "temperature": 0.7
            },
            "voice": {
                "sttProvider": "whisper-cpp",
                "ttsProvider": "piper",
                "piperVoicePath": null
            },
            "audio": {
                "ttsEnabled": true,
                "inputGain": 99
            },
            "shortcuts": {
                "pushToTalk": "Ctrl+Shift+Space",
                "stopAll": "Ctrl+Shift+Esc"
            },
            "privacy": {
                "telemetryEnabled": false,
                "allowScreenCapture": false,
                "localOnly": true
            },
            "flags": {
                "onboardingCompleted": false
            }
        }
        """;
        await File.WriteAllTextAsync(path, invalidAudio);
        var store = new JsonFileSettingsStore(path, NullLogger<JsonFileSettingsStore>.Instance);

        var doc = await store.GetAsync(CancellationToken.None);

        Assert.Equal(SettingsDocument.Defaults().Audio.InputGain, doc.Audio.InputGain);
    }

    [Fact]
    public async Task GetAsync_normalizes_windows_tts_to_kokoro_sharp()
    {
        var path = Path.Combine(_tempDir, "runtime-settings.json");
        var windowsTts = """
        {
            "llm": {
                "provider": "lmstudio",
                "modelId": "auto",
                "baseUrl": "http://127.0.0.1:1234/v1",
                "apiKey": null,
                "maxTokens": 2048,
                "contextWindowTokens": 8192,
                "temperature": 0.7
            },
            "voice": {
                "sttProvider": "whisper-cpp",
                "ttsProvider": "windows",
                "ttsVoiceId": "Microsoft David",
                "piperVoicePath": null
            },
            "audio": {
                "ttsEnabled": true,
                "inputGain": 1
            },
            "shortcuts": {
                "pushToTalk": "Ctrl+Shift+Space",
                "stopAll": "Ctrl+Shift+Esc"
            },
            "privacy": {
                "telemetryEnabled": false,
                "allowScreenCapture": false,
                "localOnly": true
            },
            "flags": {
                "onboardingCompleted": false
            }
        }
        """;
        await File.WriteAllTextAsync(path, windowsTts);
        var store = new JsonFileSettingsStore(path, NullLogger<JsonFileSettingsStore>.Instance);

        var doc = await store.GetAsync(CancellationToken.None);

        Assert.Equal("kokoro-sharp", doc.Voice.TtsProvider);
        Assert.Equal("bm_lewis", doc.Voice.TtsVoiceId);
    }

    [Fact]
    public async Task GetAsync_normalizes_reserved_shortcuts_to_defaults()
    {
        var path = Path.Combine(_tempDir, "runtime-settings.json");
        var reservedShortcut = """
        {
            "llm": {
                "provider": "lmstudio",
                "modelId": "auto",
                "baseUrl": "http://127.0.0.1:1234/v1",
                "apiKey": null,
                "maxTokens": 2048,
                "contextWindowTokens": 8192,
                "temperature": 0.7
            },
            "voice": {
                "sttProvider": "whisper-cpp",
                "ttsProvider": "kokoro-sharp",
                "ttsVoiceId": "bm_lewis",
                "piperVoicePath": null
            },
            "shortcuts": {
                "pushToTalk": "Ctrl+Shift+Space",
                "stopAll": "Ctrl+Shift+Esc"
            },
            "privacy": {
                "telemetryEnabled": false,
                "allowScreenCapture": false,
                "localOnly": true
            },
            "flags": {
                "onboardingCompleted": false
            }
        }
        """;
        await File.WriteAllTextAsync(path, reservedShortcut);
        var store = new JsonFileSettingsStore(path, NullLogger<JsonFileSettingsStore>.Instance);

        var doc = await store.GetAsync(CancellationToken.None);

        Assert.Equal("Ctrl+Alt+M", doc.Shortcuts.PushToTalk);
        Assert.Equal("Ctrl+Alt+Esc", doc.Shortcuts.StopAll);
    }

    [Fact]
    public async Task ReplaceAsync_roundtrips_and_normalizes_toolOverrides()
    {
        var store = NewStore();
        var defaults = SettingsDocument.Defaults();
        var updated = defaults with
        {
            Permissions = defaults.Permissions! with
            {
                ToolOverrides = new Dictionary<string, string>
                {
                    ["web_search"] = "off",       // kept as-is
                    ["WeatherGeocode"] = "always", // key canonicalized to snake_case
                    ["file_read"] = "GARBAGE",     // invalid value → dropped
                    [""] = "off",                  // empty key → dropped
                },
            },
        };

        await store.ReplaceAsync(updated, CancellationToken.None);

        var fresh = NewStore();
        var roundTripped = await fresh.GetAsync(CancellationToken.None);
        var overrides = roundTripped.Permissions!.ToolOverrides;

        Assert.NotNull(overrides);
        Assert.Equal("off", overrides!["web_search"]);
        Assert.Equal("always", overrides["weather_geocode"]); // canonicalized
        Assert.False(overrides.ContainsKey("WeatherGeocode"));
        Assert.False(overrides.ContainsKey("file_read")); // invalid value dropped
        Assert.Equal(2, overrides.Count);
    }

    [Fact]
    public async Task ReplaceAsync_collapses_empty_toolOverrides_to_null()
    {
        var store = NewStore();
        var defaults = SettingsDocument.Defaults();
        var updated = defaults with
        {
            Permissions = defaults.Permissions! with
            {
                // All entries invalid → normalizes to an empty map → null.
                ToolOverrides = new Dictionary<string, string> { ["file_read"] = "nope" },
            },
        };

        await store.ReplaceAsync(updated, CancellationToken.None);

        // The persisted file must not contain an empty "toolOverrides": {}.
        var raw = await File.ReadAllTextAsync(store.FilePath);
        Assert.DoesNotContain("toolOverrides", raw);

        var fresh = NewStore();
        var roundTripped = await fresh.GetAsync(CancellationToken.None);
        Assert.Null(roundTripped.Permissions!.ToolOverrides);
    }

    private JsonFileSettingsStore NewStore() =>
        new(Path.Combine(_tempDir, "runtime-settings.json"), NullLogger<JsonFileSettingsStore>.Instance);
}
