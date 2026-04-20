using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Thaddeus.Runtime.Hosting;

/// <summary>
/// Bearer-token authentication middleware for loopback API and WebSocket traffic.
/// Tokens may be supplied via the standard <c>Authorization: Bearer</c> header or,
/// for WebSocket clients that cannot set headers, via an <c>access_token</c> query
/// parameter (per RFC 6750 §2.3, scoped only to the WebSocket upgrade endpoint).
/// </summary>
public sealed class RuntimeBearerAuthMiddleware
{
    /// <summary>Paths that bypass auth entirely. Currently: index.html (bootstrap) and health probe.</summary>
    private static readonly HashSet<string> AnonymousPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/",
        "/index.html",
        "/health",
        "/favicon.ico",
    };

    private readonly RequestDelegate _next;
    private readonly RuntimeOptions _options;
    private readonly AuthFailureTracker _tracker;
    private readonly ILogger<RuntimeBearerAuthMiddleware> _logger;

    /// <summary>Constructs the middleware with its dependencies.</summary>
    public RuntimeBearerAuthMiddleware(
        RequestDelegate next,
        RuntimeOptions options,
        AuthFailureTracker tracker,
        ILogger<RuntimeBearerAuthMiddleware> logger)
    {
        _next = next;
        _options = options;
        _tracker = tracker;
        _logger = logger;
    }

    /// <summary>ASP.NET middleware invocation.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Static asset roots and bootstrap paths skip auth. The bootstrap response embeds
        // the token via meta tag so the SPA can authenticate subsequent requests.
        if (IsAnonymous(path) || IsSpaBootstrapRequest(context, path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_tracker.IsLockedOut(now))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        if (!TryExtractToken(context, out var presented) || !ConstantTimeEquals(presented, _options.BearerToken))
        {
            var trippedLockout = _tracker.RecordFailure(now);
            _logger.LogWarning(
                "auth.failure path={Path} remote={Remote} trippedLockout={Tripped}",
                path,
                context.Connection.RemoteIpAddress,
                trippedLockout);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        _tracker.RecordSuccess();
        await _next(context).ConfigureAwait(false);
    }

    private static bool IsAnonymous(string path)
    {
        if (AnonymousPaths.Contains(path)) return true;

        // Static asset extensions served before the SPA has a token. The SPA asset
        // hash makes them effectively unguessable from outside; loopback further
        // restricts who can request them. API and /ws routes are NOT in this set.
        return path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".map", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Anonymous bypass for SPA history-API routes (e.g. <c>/onboarding</c>, <c>/chat/123</c>).
    /// The bootstrap HTML these requests resolve to is harmless without a token: it
    /// only carries the meta-tag bootstrap that the SPA itself uses to authenticate
    /// subsequent API/WS calls. Restricted to GET requests with no file extension and
    /// no <c>/api</c> or <c>/ws</c> prefix so we never accidentally expose data routes.
    /// </summary>
    private static bool IsSpaBootstrapRequest(HttpContext context, string path)
    {
        if (!HttpMethods.IsGet(context.Request.Method)) return false;
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWith("/ws", StringComparison.OrdinalIgnoreCase)) return false;
        if (Path.HasExtension(path)) return false;
        return true;
    }

    private static bool TryExtractToken(HttpContext context, out string token)
    {
        var auth = context.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = auth["Bearer ".Length..].Trim();
            return token.Length > 0;
        }

        // WebSocket upgrade fallback (RFC 6750 §2.3) — only honoured on /ws.
        // We can't rely on context.WebSockets.IsWebSocketRequest here because
        // UseWebSockets() is registered after this middleware in the pipeline.
        if (context.Request.Path.StartsWithSegments("/ws"))
        {
            var fromQuery = context.Request.Query["access_token"].ToString();
            if (!string.IsNullOrEmpty(fromQuery))
            {
                token = fromQuery;
                return true;
            }
        }

        token = string.Empty;
        return false;
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
