using Microsoft.Extensions.Time.Testing;
using SirThaddeus.Core.Caching;

namespace SirThaddeus.Tests;

public sealed class InMemoryResultCacheTests
{
    [Fact]
    public async Task GetAsync_WhenKeyMissing_ReturnsNull()
    {
        var cache = new InMemoryResultCache();

        var value = await cache.GetAsync<string>("missing");

        Assert.Null(value);
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_ReturnsValueBeforeExpiry()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new InMemoryResultCache(timeProvider: time);

        await cache.SetAsync("k", "v", TimeSpan.FromMinutes(5));

        var value = await cache.GetAsync<string>("k");

        Assert.Equal("v", value);
    }

    [Fact]
    public async Task GetAsync_AfterExpiry_ReturnsNull()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new InMemoryResultCache(timeProvider: time);

        await cache.SetAsync("k", "v", TimeSpan.FromMinutes(1));
        time.Advance(TimeSpan.FromMinutes(2));

        var value = await cache.GetAsync<string>("k");

        Assert.Null(value);
    }

    [Fact]
    public async Task SetAsync_WhenAtCapacity_EvictsLeastRecentlyUsed()
    {
        var cache = new InMemoryResultCache(maxEntries: 2);

        await cache.SetAsync("a", "A", TimeSpan.FromHours(1));
        await cache.SetAsync("b", "B", TimeSpan.FromHours(1));
        _ = await cache.GetAsync<string>("a"); // make 'a' most-recently-used
        await cache.SetAsync("c", "C", TimeSpan.FromHours(1));

        var a = await cache.GetAsync<string>("a");
        var b = await cache.GetAsync<string>("b");
        var c = await cache.GetAsync<string>("c");

        Assert.Equal("A", a);
        Assert.Null(b);
        Assert.Equal("C", c);
    }

    [Fact]
    public async Task ConcurrentAccess_IsThreadSafe()
    {
        var cache = new InMemoryResultCache(maxEntries: 1000);

        var tasks = Enumerable.Range(0, 200)
            .Select(async i =>
            {
                var key = $"k-{i % 20}";
                await cache.SetAsync(key, i, TimeSpan.FromMinutes(5));
                _ = await cache.GetAsync<int>(key);
            });

        await Task.WhenAll(tasks);

        var sample = await cache.GetAsync<int>("k-1");
        Assert.True(sample >= 0);
    }
}
