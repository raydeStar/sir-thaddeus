using System.Net;

namespace SirThaddeus.WebSearch;

// ─────────────────────────────────────────────────────────────────────────
// Retry Helper
//
// Shared transient-failure retry for HTTP operations across all search
// providers. One retry after a short backoff is enough to survive most
// transient blips (DNS hiccups, 502/503/504 from upstream proxies,
// momentary network drops) without adding noticeable latency.
//
// What counts as "transient":
//   - HttpRequestException (connection refused, DNS failure, etc.)
//   - HTTP 5xx status codes (server-side errors)
//   - TaskCanceledException that fires BEFORE the caller's own token
//     is cancelled (indicates an internal HttpClient.Timeout hit)
//
// Non-transient failures (4xx, auth errors, etc.) are NOT retried.
// ─────────────────────────────────────────────────────────────────────────

public static class RetryHelper
{
    /// <summary>
    /// Default backoff between retries (milliseconds).
    /// </summary>
    private const int DefaultBackoffMs = 500;

    /// <summary>
    /// Default maximum retry count (1 retry = 2 total attempts).
    /// </summary>
    private const int DefaultMaxRetries = 1;

    /// <summary>
    /// Executes <paramref name="operation"/> with automatic retry on
    /// transient HTTP failures. Returns the result on the first success,
    /// or rethrows the final exception if all attempts fail.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken,
        int maxRetries = DefaultMaxRetries,
        int backoffMs  = DefaultBackoffMs)
    {
        var attempt = 0;

        while (true)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransient(ex, cancellationToken))
            {
                attempt++;
                await Task.Delay(backoffMs * attempt, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Determines whether an exception represents a transient failure
    /// that is worth retrying.
    /// </summary>
    internal static bool IsTransient(Exception ex, CancellationToken callerToken)
    {
        // If the CALLER cancelled, don't retry — they want to stop.
        if (callerToken.IsCancellationRequested)
            return false;

        // Connection-level failures (DNS, refused, reset, etc.)
        if (ex is HttpRequestException httpEx)
        {
            // 4xx errors are NOT transient (bad request, auth, not found)
            if (httpEx.StatusCode.HasValue)
            {
                var code = (int)httpEx.StatusCode.Value;
                return code >= 500; // 5xx only
            }

            // No status code → connection-level failure → transient
            return true;
        }

        // Internal HttpClient.Timeout fires TaskCanceledException with
        // an inner TimeoutException. Worth retrying once.
        if (ex is TaskCanceledException { InnerException: TimeoutException })
            return true;

        // OperationCanceledException from a linked CTS that timed out
        // (not the caller's token) — also worth one retry.
        if (ex is OperationCanceledException && !callerToken.IsCancellationRequested)
            return true;

        return false;
    }
}
