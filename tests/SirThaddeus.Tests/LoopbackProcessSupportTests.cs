using SirThaddeus.Core;

namespace SirThaddeus.Tests;

public sealed class LoopbackProcessSupportTests
{
    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://[::1]:5000")]
    public void IsLoopback_AcceptsSupportedLoopbackHosts(string uriText)
    {
        var uri = new Uri(uriText);

        var result = LoopbackProcessSupport.IsLoopback(uri);

        Assert.True(result);
    }

    [Fact]
    public async Task WaitForProbeAsync_RetriesUntilProbeSucceeds()
    {
        var attempts = 0;

        var result = await LoopbackProcessSupport.WaitForProbeAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(attempts >= 3);
            },
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        Assert.True(result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task WaitForProbeAsync_ReturnsFalseWhenProbeNeverSucceeds()
    {
        var attempts = 0;

        var result = await LoopbackProcessSupport.WaitForProbeAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(false);
            },
            TimeSpan.FromMilliseconds(35),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        Assert.False(result);
        Assert.True(attempts >= 1);
    }
}