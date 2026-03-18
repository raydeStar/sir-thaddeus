namespace SirThaddeus.Core.Caching;

public interface IResultCache
{
    Task<T?> GetAsync<T>(string key);

    Task SetAsync<T>(string key, T value, TimeSpan ttl);
}
