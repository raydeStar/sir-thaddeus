using SirThaddeus.Config;

namespace SirThaddeus.Tests.Voice;

/// <summary>
/// Validates that VoiceSettings URL derivation methods produce
/// correct, deterministic endpoints for the VoiceHost contract.
/// </summary>
public sealed class VoiceHostConfigTests
{
    [Fact]
    public void GetVoiceHostBaseUrl_DefaultSettings_ReturnsLoopback()
    {
        var settings = new VoiceSettings();
        var baseUrl = settings.GetVoiceHostBaseUrl();

        Assert.Equal("http://127.0.0.1:17845", baseUrl);
    }

    [Fact]
    public void GetVoiceHostBaseUrl_TrimsTrailingSlash()
    {
        var settings = new VoiceSettings { VoiceHostBaseUrl = "http://127.0.0.1:17845/" };
        var baseUrl = settings.GetVoiceHostBaseUrl();

        Assert.Equal("http://127.0.0.1:17845", baseUrl);
    }

    [Fact]
    public void GetVoiceHostBaseUrl_WhitespaceOrEmpty_FallsBackToDefault()
    {
        var empty = new VoiceSettings { VoiceHostBaseUrl = "" };
        var whitespace = new VoiceSettings { VoiceHostBaseUrl = "   " };

        Assert.Equal("http://127.0.0.1:17845", empty.GetVoiceHostBaseUrl());
        Assert.Equal("http://127.0.0.1:17845", whitespace.GetVoiceHostBaseUrl());
    }

    [Fact]
    public void GetAsrUrl_AppendsSlashAsr()
    {
        var settings = new VoiceSettings { VoiceHostBaseUrl = "http://127.0.0.1:9999" };
        Assert.Equal("http://127.0.0.1:9999/asr", settings.GetAsrUrl());
    }

    [Fact]
    public void GetTtsUrl_AppendsSlashTts()
    {
        var settings = new VoiceSettings { VoiceHostBaseUrl = "http://127.0.0.1:9999" };
        Assert.Equal("http://127.0.0.1:9999/tts", settings.GetTtsUrl());
    }

    [Fact]
    public void GetHealthUrl_UsesConfiguredPath()
    {
        var settings = new VoiceSettings
        {
            VoiceHostBaseUrl = "http://127.0.0.1:17845",
            VoiceHostHealthPath = "/health"
        };

        Assert.Equal("http://127.0.0.1:17845/health", settings.GetHealthUrl());
    }

    [Fact]
    public void GetHealthUrl_CustomPath_Preserved()
    {
        var settings = new VoiceSettings
        {
            VoiceHostBaseUrl = "http://127.0.0.1:17845",
            VoiceHostHealthPath = "/v1/health"
        };

        Assert.Equal("http://127.0.0.1:17845/v1/health", settings.GetHealthUrl());
    }

    [Fact]
    public void GetHealthUrl_MissingLeadingSlash_PrependedAutomatically()
    {
        var settings = new VoiceSettings
        {
            VoiceHostBaseUrl = "http://127.0.0.1:17845",
            VoiceHostHealthPath = "health"
        };

        Assert.Equal("http://127.0.0.1:17845/health", settings.GetHealthUrl());
    }

    [Fact]
    public void DefaultSettings_VoiceHostEnabled_IsTrue()
    {
        var settings = new VoiceSettings();
        Assert.True(settings.VoiceHostEnabled);
    }

    [Fact]
    public void DefaultSettings_StartupTimeout_IsSane()
    {
        var settings = new VoiceSettings();
        Assert.True(settings.VoiceHostStartupTimeoutMs >= 5000,
            "Startup timeout should be at least 5 seconds to allow process init.");
    }

    [Fact]
    public void CustomPort_DerivedUrlsReflectPort()
    {
        var settings = new VoiceSettings { VoiceHostBaseUrl = "http://127.0.0.1:18000" };

        Assert.Contains(":18000/asr", settings.GetAsrUrl());
        Assert.Contains(":18000/tts", settings.GetTtsUrl());
        Assert.Contains(":18000/health", settings.GetHealthUrl());
    }

    [Fact]
    public void DeprecatedFields_DefaultEmpty()
    {
        var settings = new VoiceSettings();
        Assert.Equal("", settings.AsrEndpoint);
        Assert.Equal("", settings.TtsEndpoint);
    }

    [Fact]
    public void EngineDefaults_AreDeterministic()
    {
        var settings = new VoiceSettings();

        Assert.Equal("windows", settings.GetNormalizedTtsEngine());
        Assert.Equal("faster-whisper", settings.GetNormalizedSttEngine());
        Assert.Equal("base", settings.GetResolvedSttModelId());
        Assert.Equal("en", settings.GetResolvedSttLanguage());
        Assert.Equal("qwen3asr", settings.GetResolvedYouTubeAsrProvider());
        Assert.Equal("qwen-asr-1.6b", settings.GetResolvedYouTubeAsrModelId());
        Assert.Equal("en-us", settings.GetResolvedYouTubeLanguageHint());
        Assert.False(settings.YouTubeKeepAudio);
    }

    [Fact]
    public void SttModelId_WhenExplicit_Preserved()
    {
        var settings = new VoiceSettings
        {
            SttEngine = "qwen3asr",
            SttModelId = "qwen3-medium"
        };

        Assert.Equal("qwen3asr", settings.GetNormalizedSttEngine());
        Assert.Equal("qwen3-medium", settings.GetResolvedSttModelId());
    }

    [Fact]
    public void TtsVoiceId_WhenExplicit_Preserved()
    {
        var settings = new VoiceSettings
        {
            TtsEngine = "kokoro",
            TtsVoiceId = "af_sky"
        };

        Assert.Equal("kokoro", settings.GetNormalizedTtsEngine());
        Assert.Equal("af_sky", settings.GetResolvedTtsVoiceId());
    }

    [Fact]
    public void SttLanguage_AutoAndExplicitValues_AreNormalized()
    {
        var auto = new VoiceSettings { SttLanguage = "auto" };
        var explicitEnglish = new VoiceSettings { SttLanguage = "EN-US" };

        Assert.Equal("", auto.GetResolvedSttLanguage());
        Assert.Equal("en-us", explicitEnglish.GetResolvedSttLanguage());
    }

    [Fact]
    public void YouTubeAsrSettings_NormalizeProviderModelAndLanguage()
    {
        var settings = new VoiceSettings
        {
            YouTubeAsrProvider = "QWEN-ASR",
            YouTubeAsrModelId = " qwen-asr-1.6b ",
            YouTubeLanguageHint = "AUTO",
            YouTubeKeepAudio = true
        };

        Assert.Equal("qwen3asr", settings.GetResolvedYouTubeAsrProvider());
        Assert.Equal("qwen-asr-1.6b", settings.GetResolvedYouTubeAsrModelId());
        Assert.Equal("", settings.GetResolvedYouTubeLanguageHint());
        Assert.True(settings.YouTubeKeepAudio);
    }
}
