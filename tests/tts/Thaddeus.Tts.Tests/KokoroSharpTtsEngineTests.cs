using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Tts.Kokoro;

namespace Thaddeus.Tts.Tests;

public sealed class KokoroSharpTtsEngineTests
{
    [Fact]
    public async Task ListVoicesAsync_ReturnsBundledKokoroVoices()
    {
        await using var engine = CreateEngine();

        var voices = await engine.ListVoicesAsync(CancellationToken.None);

        Assert.Contains(voices, voice => voice.Id == "bm_lewis");
        Assert.Contains(voices, voice => voice.Id == "am_michael");
        Assert.Contains(voices, voice => voice.Id == "bf_emma");
        Assert.All(voices, voice => Assert.Equal(KokoroSharpTtsEngine.Name, voice.EngineName));
    }

    [Fact]
    public async Task SupportsVoiceAsync_ReturnsFalseForUnknownVoice()
    {
        await using var engine = CreateEngine();

        Assert.True(await engine.SupportsVoiceAsync("bm_lewis", CancellationToken.None));
        Assert.False(await engine.SupportsVoiceAsync("not_a_voice", CancellationToken.None));
        Assert.False(await engine.SupportsVoiceAsync("", CancellationToken.None));
    }

    private static KokoroSharpTtsEngine CreateEngine()
        => new(
            new KokoroOptions { AutoDownloadModel = false },
            NullLogger<KokoroSharpTtsEngine>.Instance);
}
