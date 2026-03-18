using SirThaddeus.WebSearch;

namespace SirThaddeus.Tests;

[Trait("Category", "Integration")]
public class PublicApiIntegrationTests
{
    [Fact]
    public async Task Timezone_Live_OpenMeteoFallback_ReturnsTimezone()
    {
        using var provider = new TimezoneProvider();
        var result = await provider.ResolveAsync(35.6762, 139.6503, "JP");
        Assert.False(string.IsNullOrWhiteSpace(result.Timezone));
    }

    [Fact]
    public async Task Holidays_Live_NagerDate_ReturnsUpcoming()
    {
        using var provider = new NagerDateHolidaysProvider();
        var result = await provider.GetNextPublicHolidaysAsync("US", maxItems: 3);
        Assert.NotEmpty(result.Holidays);
    }

    [Fact]
    public async Task Feed_Live_ReturnsItems()
    {
        using var provider = new FeedProvider();
        var result = await provider.FetchAsync("https://xkcd.com/atom.xml", maxItems: 5);
        Assert.NotEmpty(result.Items);
    }

    [Fact]
    public async Task Status_Live_Github_IsReachable()
    {
        using var probe = new StatusProbe();
        var result = await probe.CheckAsync("https://github.com");
        Assert.True(result.Reachable);
    }
}
