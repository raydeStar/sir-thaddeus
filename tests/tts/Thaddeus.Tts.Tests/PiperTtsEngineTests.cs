using Thaddeus.Tts.Piper.Legacy;

namespace Thaddeus.Tts.Tests;

public sealed class PiperTtsEngineTests
{
    [Fact]
    public async Task ListVoicesAsync_ReturnsEmptyWhenFallbackModelIsMissing()
    {
        await using var engine = new PiperTtsEngine(new PiperOptions
        {
            VoiceModelPath = "en_US-john-medium"
        });

        var voices = await engine.ListVoicesAsync(CancellationToken.None);

        Assert.Empty(voices);
    }
}
