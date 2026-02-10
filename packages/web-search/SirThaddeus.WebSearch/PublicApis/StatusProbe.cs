using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;

namespace SirThaddeus.WebSearch;

// ─────────────────────────────────────────────────────────────────────────
// Status Probe
//
// Reachability checks with bounded requests:
//   - HEAD first
//   - Fallback GET (range request) when HEAD is blocked/unsupported
//
// Guarantees:
//   - No API keys
//   - URL safety guard (SSRF protections)
//   - Short timeout + retry on transient transport failures
//   - In-memory short TTL cache
// ─────────────────────────────────────────────────────────────────────────

public sealed class StatusProbe : IStatusProbe, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly PublicApiServiceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly ProviderThrottle _throttle;

    private readonly ConcurrentDictionary<string, CacheEntry<StatusProbeResult>> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<StatusProbeResult>>> _inflight =
        new(StringComparer.OrdinalIgnoreCase);

    public StatusProbe(
        PublicApiServiceOptions? options = null,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new PublicApiServiceOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheTtl = TimeSpan.FromSeconds(Math.Max(5, _options.StatusCacheSeconds));

        _http = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _throttle = new ProviderThrottle(
            _options.MaxConcurrentRequestsPerProvider,
            TimeSpan.FromMilliseconds(Math.Max(0, _options.MinRequestSpacingMs)),
            _timeProvider);

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            var userAgent = string.IsNullOrWhiteSpace(_options.UserAgent)
                ? "SirThaddeusCopilot/1.0 (contact: local-runtime@localhost)"
                : _options.UserAgent.Trim();
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        }
    }

    public async Task<StatusProbeResult> CheckAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var checkedAt = _timeProvider.GetUtcNow();
        var guard = await PublicApiUrlGuard.NormalizeAndValidateAsync(
            url,
            _options.BlockPrivateNetworkTargets,
            cancellationToken).ConfigureAwait(false);

        if (!guard.Allowed || guard.Uri is null)
        {
            return new StatusProbeResult
            {
                Url = PublicApiUrlGuard.NormalizeUrl(url),
                Reachable = false,
                Method = "none",
                LatencyMs = 0,
                Error = guard.Error ?? "Target URL not allowed.",
                CheckedAt = checkedAt,
                Source = "direct",
                Cache = new PublicApiCacheMetadata()
            };
        }

        var normalizedUrl = guard.Uri.ToString();
        if (PublicApiCacheHelper.TryGetFresh(_cache, normalizedUrl, _cacheTtl, checkedAt, out var cached, out var age))
        {
            return cached with
            {
                Cache = new PublicApiCacheMetadata { Hit = true, AgeSeconds = age }
            };
        }

        return await RequestCoalescer.CoalesceAsync(
            _inflight,
            normalizedUrl,
            async () =>
            {
                var insideNow = _timeProvider.GetUtcNow();
                if (PublicApiCacheHelper.TryGetFresh(_cache, normalizedUrl, _cacheTtl, insideNow, out var second, out var secondAge))
                {
                    return second with
                    {
                        Cache = new PublicApiCacheMetadata { Hit = true, AgeSeconds = secondAge }
                    };
                }

                var probed = await ProbeAsync(guard.Uri, cancellationToken).ConfigureAwait(false);
                var normalized = probed with
                {
                    Cache = new PublicApiCacheMetadata { Hit = false, AgeSeconds = 0 }
                };
                _cache[normalizedUrl] = new CacheEntry<StatusProbeResult>(normalized, insideNow);
                return normalized;
            }).ConfigureAwait(false);
    }

    private async Task<StatusProbeResult> ProbeAsync(Uri target, CancellationToken cancellationToken)
    {
        var head = await SendProbeAsync(HttpMethod.Head, target, useRangeHeader: false, cancellationToken).ConfigureAwait(false);
        if (head.Reachable && !ShouldFallbackToGet(head.HttpStatus))
        {
            return new StatusProbeResult
            {
                Url = target.ToString(),
                Reachable = true,
                HttpStatus = head.HttpStatus,
                Method = "HEAD",
                LatencyMs = head.LatencyMs,
                Error = null,
                CheckedAt = _timeProvider.GetUtcNow(),
                Source = "direct",
                Cache = new PublicApiCacheMetadata()
            };
        }

        if (ShouldFallbackToGet(head.HttpStatus) || !head.Reachable)
        {
            var get = await SendProbeAsync(HttpMethod.Get, target, useRangeHeader: true, cancellationToken).ConfigureAwait(false);
            return new StatusProbeResult
            {
                Url = target.ToString(),
                Reachable = get.Reachable,
                HttpStatus = get.HttpStatus,
                Method = "GET",
                LatencyMs = get.LatencyMs,
                Error = get.Error,
                CheckedAt = _timeProvider.GetUtcNow(),
                Source = "direct",
                Cache = new PublicApiCacheMetadata()
            };
        }

        return new StatusProbeResult
        {
            Url = target.ToString(),
            Reachable = head.Reachable,
            HttpStatus = head.HttpStatus,
            Method = "HEAD",
            LatencyMs = head.LatencyMs,
            Error = head.Error,
            CheckedAt = _timeProvider.GetUtcNow(),
            Source = "direct",
            Cache = new PublicApiCacheMetadata()
        };
    }

    private async Task<ProbeAttempt> SendProbeAsync(
        HttpMethod method,
        Uri target,
        bool useRangeHeader,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        await _throttle.WaitTurnAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(Math.Clamp(_options.RequestTimeoutMs, 1_500, 15_000));

            var response = await RetryHelper.ExecuteAsync(async () =>
            {
                var req = new HttpRequestMessage(method, target);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                if (useRangeHeader && method == HttpMethod.Get)
                    req.Headers.Range = new RangeHeaderValue(0, 0);

                return await _http.SendAsync(
                    req,
                    HttpCompletionOption.ResponseHeadersRead,
                    linked.Token).ConfigureAwait(false);
            }, linked.Token).ConfigureAwait(false);

            using (response)
            {
                sw.Stop();
                return new ProbeAttempt(
                    Reachable: true,
                    HttpStatus: (int)response.StatusCode,
                    LatencyMs: (int)Math.Max(0, sw.ElapsedMilliseconds),
                    Error: null);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return new ProbeAttempt(
                Reachable: false,
                HttpStatus: null,
                LatencyMs: (int)Math.Max(0, sw.ElapsedMilliseconds),
                Error: "Request timed out.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeAttempt(
                Reachable: false,
                HttpStatus: null,
                LatencyMs: (int)Math.Max(0, sw.ElapsedMilliseconds),
                Error: ex.Message);
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static bool ShouldFallbackToGet(int? statusCode)
    {
        if (!statusCode.HasValue)
            return true;

        return statusCode.Value is 400 or 403 or 405 or 501;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
        _throttle.Dispose();
    }

    private sealed record ProbeAttempt(
        bool Reachable,
        int? HttpStatus,
        int LatencyMs,
        string? Error);
}
