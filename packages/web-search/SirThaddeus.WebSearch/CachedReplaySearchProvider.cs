using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SirThaddeus.WebSearch;

/// <summary>
/// An IWebSearchProvider that replays cached search results from disk.
/// If no cache entry exists for a query, returns empty results.
///
/// Usage:
///   - Record mode: wrap a real provider with CachedSearchRecorder to populate the cache.
///   - Replay mode: use this provider directly in tests for deterministic, offline search.
/// </summary>
public sealed class CachedReplaySearchProvider : IWebSearchProvider
{
    private readonly string _cacheDirectory;

    public CachedReplaySearchProvider(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory ?? throw new ArgumentNullException(nameof(cacheDirectory));
    }

    public string Name => "CachedReplay";

    public Task<SearchResults> SearchAsync(
        string query,
        WebSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        var cacheFile = GetCacheFilePath(query);
        if (!File.Exists(cacheFile))
        {
            return Task.FromResult(new SearchResults
            {
                Results = [],
                Provider = Name
            });
        }

        var json = File.ReadAllText(cacheFile);
        var cached = JsonSerializer.Deserialize<SearchResults>(json, JsonOptions);
        return Task.FromResult(cached ?? new SearchResults { Results = [], Provider = Name });
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Directory.Exists(_cacheDirectory));
    }

    private string GetCacheFilePath(string query)
    {
        var normalized = query.Trim().ToLowerInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return Path.Combine(_cacheDirectory, $"{hash}.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>
/// Wraps a real search provider and records results to a cache directory.
/// Use this to populate the cache for CachedReplaySearchProvider.
/// </summary>
public sealed class CachedSearchRecorder : IWebSearchProvider
{
    private readonly IWebSearchProvider _inner;
    private readonly string _cacheDirectory;

    private static readonly JsonSerializerOptions JsonWriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public CachedSearchRecorder(IWebSearchProvider inner, string cacheDirectory)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cacheDirectory = cacheDirectory ?? throw new ArgumentNullException(nameof(cacheDirectory));
        Directory.CreateDirectory(_cacheDirectory);
    }

    public string Name => $"CachedRecorder({_inner.Name})";

    public async Task<SearchResults> SearchAsync(
        string query,
        WebSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.SearchAsync(query, options, cancellationToken);

        // Write to cache
        var cacheFile = GetCacheFilePath(query);
        var json = JsonSerializer.Serialize(result, JsonWriteOptions);
        await File.WriteAllTextAsync(cacheFile, json, cancellationToken);

        return result;
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        return _inner.IsAvailableAsync(cancellationToken);
    }

    private string GetCacheFilePath(string query)
    {
        var normalized = query.Trim().ToLowerInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return Path.Combine(_cacheDirectory, $"{hash}.json");
    }
}
