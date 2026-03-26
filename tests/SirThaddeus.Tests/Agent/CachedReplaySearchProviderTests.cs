using System.Text.Json;
using SirThaddeus.WebSearch;
using Xunit;

namespace SirThaddeus.Tests;

public class CachedReplaySearchProviderTests
{
    [Fact]
    public async Task Replay_ReturnsCachedResults()
    {
        using var tempDir = CreateTempCacheDir();
        var expected = new SearchResults
        {
            Results = [new SearchResult { Title = "Result 1", Url = "https://example.com" }],
            Provider = "TestProvider"
        };

        // Seed the cache (using the same hash logic)
        var hash = ComputeHash("test query");
        var cachePath = Path.Combine(tempDir.Path, $"{hash}.json");
        await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(expected, JsonOpts));

        var provider = new CachedReplaySearchProvider(tempDir.Path);
        var result = await provider.SearchAsync("test query", new WebSearchOptions());

        Assert.Single(result.Results);
        Assert.Equal("Result 1", result.Results[0].Title);
    }

    [Fact]
    public async Task Replay_MissingQuery_ReturnsEmpty()
    {
        using var tempDir = CreateTempCacheDir();
        var provider = new CachedReplaySearchProvider(tempDir.Path);

        var result = await provider.SearchAsync("never cached this", new WebSearchOptions());

        Assert.Empty(result.Results);
        Assert.Equal("CachedReplay", result.Provider);
    }

    [Fact]
    public async Task Replay_CaseInsensitiveHash()
    {
        using var tempDir = CreateTempCacheDir();
        var data = new SearchResults
        {
            Results = [new SearchResult { Title = "CaseTest", Url = "https://a.com" }],
            Provider = "Test"
        };

        var hash = ComputeHash("weather new york");
        await File.WriteAllTextAsync(
            Path.Combine(tempDir.Path, $"{hash}.json"),
            JsonSerializer.Serialize(data, JsonOpts));

        var provider = new CachedReplaySearchProvider(tempDir.Path);
        var result = await provider.SearchAsync("Weather New York", new WebSearchOptions());

        Assert.Single(result.Results);
        Assert.Equal("CaseTest", result.Results[0].Title);
    }

    [Fact]
    public async Task IsAvailable_TrueWhenDirectoryExists()
    {
        using var tempDir = CreateTempCacheDir();
        var provider = new CachedReplaySearchProvider(tempDir.Path);
        Assert.True(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailable_FalseWhenDirectoryMissing()
    {
        var provider = new CachedReplaySearchProvider(@"C:\nonexistent\path\never\exists");
        Assert.False(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task Recorder_RecordsAndReplays()
    {
        using var tempDir = CreateTempCacheDir();
        var fakeProvider = new FakeSearchProvider([
            new SearchResult { Title = "Live Result", Url = "https://live.com", Snippet = "Fresh" }
        ]);

        var recorder = new CachedSearchRecorder(fakeProvider, tempDir.Path);
        var recorded = await recorder.SearchAsync("my search", new WebSearchOptions());
        Assert.Single(recorded.Results);
        Assert.Equal("Live Result", recorded.Results[0].Title);

        // Now replay
        var replay = new CachedReplaySearchProvider(tempDir.Path);
        var replayed = await replay.SearchAsync("my search", new WebSearchOptions());
        Assert.Single(replayed.Results);
        Assert.Equal("Live Result", replayed.Results[0].Title);
    }

    [Fact]
    public void Name_IsCachedReplay()
    {
        using var tempDir = CreateTempCacheDir();
        var provider = new CachedReplaySearchProvider(tempDir.Path);
        Assert.Equal("CachedReplay", provider.Name);
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static string ComputeHash(string query)
    {
        var normalized = query.Trim().ToLowerInvariant();
        var hash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(normalized)))[..16];
        return hash;
    }

    private static TempDir CreateTempCacheDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sir-thaddeus-test-cache-{Guid.NewGuid():N}"[..40]);
        Directory.CreateDirectory(path);
        return new TempDir(path);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record TempDir(string Path) : IDisposable
    {
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
            catch { /* best effort */ }
        }
    }

    private sealed class FakeSearchProvider(IReadOnlyList<SearchResult> results) : IWebSearchProvider
    {
        public string Name => "Fake";

        public Task<SearchResults> SearchAsync(string query, WebSearchOptions options, CancellationToken ct = default)
        {
            return Task.FromResult(new SearchResults { Results = results, Provider = Name });
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    }
}
