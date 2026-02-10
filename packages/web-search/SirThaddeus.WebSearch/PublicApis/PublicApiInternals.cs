using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SirThaddeus.WebSearch;

internal static class PublicApiCacheHelper
{
    public static bool TryGetFresh<T>(
        ConcurrentDictionary<string, CacheEntry<T>> cache,
        string key,
        TimeSpan ttl,
        DateTimeOffset now,
        out T value,
        out int ageSeconds)
    {
        if (cache.TryGetValue(key, out var entry))
        {
            var age = now - entry.StoredAt;
            if (age <= ttl)
            {
                value = entry.Value;
                ageSeconds = (int)Math.Max(0, age.TotalSeconds);
                return true;
            }
        }

        value = default!;
        ageSeconds = 0;
        return false;
    }
}

internal static class RequestCoalescer
{
    public static async Task<T> CoalesceAsync<T>(
        ConcurrentDictionary<string, Lazy<Task<T>>> inflight,
        string key,
        Func<Task<T>> factory)
    {
        var lazy = inflight.GetOrAdd(
            key,
            _ => new Lazy<Task<T>>(factory, LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            inflight.TryRemove(key, out _);
        }
    }
}

internal sealed class ProviderThrottle : IDisposable
{
    private readonly SemaphoreSlim _concurrency;
    private readonly TimeSpan _minSpacing;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private DateTimeOffset _nextAllowedAt = DateTimeOffset.MinValue;

    public ProviderThrottle(
        int maxConcurrency,
        TimeSpan minSpacing,
        TimeProvider? timeProvider = null)
    {
        _concurrency = new SemaphoreSlim(Math.Max(1, maxConcurrency), Math.Max(1, maxConcurrency));
        _minSpacing = minSpacing < TimeSpan.Zero ? TimeSpan.Zero : minSpacing;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task WaitTurnAsync(CancellationToken cancellationToken)
    {
        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

        var delay = TimeSpan.Zero;
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (now < _nextAllowedAt)
                delay = _nextAllowedAt - now;

            var baseline = now > _nextAllowedAt ? now : _nextAllowedAt;
            _nextAllowedAt = baseline + _minSpacing;
        }

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    public void Release() => _concurrency.Release();

    public void Dispose() => _concurrency.Dispose();
}

internal static class PublicApiUrlGuard
{
    public static async Task<(bool Allowed, Uri? Uri, string? Error)> NormalizeAndValidateAsync(
        string rawUrl,
        bool blockPrivateTargets,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return (false, null, "URL is required.");

        var normalized = NormalizeUrl(rawUrl);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            return (false, null, "URL is invalid.");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return (false, null, "Only HTTP/HTTPS URLs are supported.");

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            return (false, null, "User-info in URL is not allowed.");

        if (!blockPrivateTargets)
            return (true, uri, null);

        if (LooksLikeLocalHostName(uri.Host))
            return (false, null, "Localhost/private hostnames are blocked.");

        if (IPAddress.TryParse(uri.Host, out var directIp))
        {
            if (IsBlockedAddress(directIp))
                return (false, null, "Private/loopback IP targets are blocked.");

            return (true, uri, null);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is SocketException or ArgumentException or OperationCanceledException)
        {
            if (ex is OperationCanceledException)
                throw;

            return (false, null, $"DNS lookup failed for host '{uri.Host}'.");
        }

        if (addresses.Length == 0)
            return (false, null, $"Host '{uri.Host}' did not resolve.");

        if (addresses.Any(IsBlockedAddress))
            return (false, null, "Resolved host maps to private/loopback address space.");

        return (true, uri, null);
    }

    public static string NormalizeUrl(string value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0)
            return "";

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://{trimmed}";
        }

        return trimmed;
    }

    private static bool LooksLikeLocalHostName(string host)
    {
        var h = (host ?? "").Trim().ToLowerInvariant();
        if (h.Length == 0)
            return true;

        return h == "localhost" ||
               h == "localhost.localdomain" ||
               h.EndsWith(".local", StringComparison.Ordinal) ||
               h.EndsWith(".lan", StringComparison.Ordinal) ||
               h.EndsWith(".internal", StringComparison.Ordinal);
    }

    private static bool IsBlockedAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b.Length != 4)
                return true;

            // 10.0.0.0/8
            if (b[0] == 10)
                return true;

            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                return true;

            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168)
                return true;

            // 127.0.0.0/8
            if (b[0] == 127)
                return true;

            // 169.254.0.0/16 link-local
            if (b[0] == 169 && b[1] == 254)
                return true;

            // 0.0.0.0/8 or multicast/reserved blocks
            if (b[0] == 0 || b[0] >= 224)
                return true;

            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Loopback))
                return true;

            if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal)
                return true;

            var b = ip.GetAddressBytes();
            if (b.Length != 16)
                return true;

            // fc00::/7 unique local
            if ((b[0] & 0xFE) == 0xFC)
                return true;

            // fe80::/10 link-local
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)
                return true;

            return false;
        }

        return true;
    }
}

internal sealed record CacheEntry<T>(T Value, DateTimeOffset StoredAt);
