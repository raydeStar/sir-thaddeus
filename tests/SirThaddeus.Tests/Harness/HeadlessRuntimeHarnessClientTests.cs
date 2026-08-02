using SirThaddeus.Harness.Execution;

namespace SirThaddeus.Tests.Harness;

public sealed class HeadlessRuntimeHarnessClientTests
{
    [Theory]
    [InlineData(null, 120)]
    [InlineData("not-a-number", 120)]
    [InlineData("1", 10)]
    [InlineData("75", 75)]
    [InlineData("900", 600)]
    public void ParseItemTimeout_is_bounded_and_deterministic(string? raw, int expectedSeconds)
    {
        var timeout = HeadlessRuntimeHarnessClient.ParseItemTimeout(raw);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), timeout);
    }
}
