namespace SirThaddeus.Core.Caching;

/// <summary>
/// Provides short-lived, in-memory caching for tool results
/// with per-key TTL expiry.
/// </summary>
public interface IResultCache
{
    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, or <c>default</c> if the
    /// key is absent or expired.
    /// </summary>
    Task<T?> GetAsync<T>(string key);

    /// <summary>
    /// Stores a value under <paramref name="key"/> that expires after <paramref name="ttl"/>.
    /// If the cache is full, the least-recently-used entry is evicted.
    /// </summary>
    Task SetAsync<T>(string key, T value, TimeSpan ttl);
}
