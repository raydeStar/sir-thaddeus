using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Thaddeus.Runtime.Hosting;

namespace Thaddeus.Runtime.Api;

/// <summary>
/// Serves the workspace SPA <c>index.html</c> with the bearer token injected as a
/// meta tag, per spec §6.2. All other static assets pass through normal static-file
/// middleware.
/// </summary>
public static class WorkspaceHostingExtensions
{
    /// <summary>Resolves the on-disk wwwroot path, falling back to the publish layout.</summary>
    public static string ResolveWebRoot(IWebHostEnvironment env)
    {
        // In development, the workspace SPA may not yet have been built. Returning a
        // placeholder directory keeps Kestrel happy; the bootstrap endpoint then
        // serves a development-friendly page.
        var configured = env.WebRootPath;
        if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        var fallback = Path.Combine(env.ContentRootPath, "wwwroot");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>
    /// Maps both the static asset middleware (for hashed JS/CSS) and the bootstrap
    /// endpoints that serve <c>/</c>, <c>/index.html</c>, <c>/workspace</c>, and
    /// <c>/compact</c> with the meta-tag-injected bearer token.
    /// </summary>
    public static void MapWorkspaceHosting(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var webRoot = ResolveWebRoot(app.Environment);
        var fileProvider = new PhysicalFileProvider(webRoot);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            ServeUnknownFileTypes = false,
        });

        Task ServeBootstrap(HttpContext context, string route)
        {
            var opts = context.RequestServices.GetRequiredService<RuntimeOptions>();
            var indexPath = Path.Combine(webRoot, "index.html");
            string html;
            if (File.Exists(indexPath))
            {
                html = File.ReadAllText(indexPath);
                html = InjectBootstrapMeta(html, opts, route);
            }
            else
            {
                html = BuildPlaceholderHtml(opts, route);
            }
            context.Response.ContentType = "text/html; charset=utf-8";
            // Bootstrap response is per-token, never cache.
            context.Response.Headers.CacheControl = "no-store";
            return context.Response.WriteAsync(html);
        }

        app.MapGet("/", ctx => ServeBootstrap(ctx, "workspace"));
        app.MapGet("/index.html", ctx => ServeBootstrap(ctx, "workspace"));
        app.MapGet("/compact", ctx => ServeBootstrap(ctx, "compact"));

        // SPA fallback. Anything that isn't an /api, /ws, or static asset request and
        // falls through to the end of the pipeline gets the bootstrap so that the
        // client-side router can pick the route up. This is required because the web
        // workspace uses History-API routing for /chat, /settings, /history, etc.
        app.Use(async (ctx, next) =>
        {
            await next();

            if (ctx.Response.HasStarted) return;
            if (ctx.Response.StatusCode != StatusCodes.Status404NotFound) return;
            if (!HttpMethods.IsGet(ctx.Request.Method)) return;

            var path = ctx.Request.Path.Value ?? "/";
            if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) return;
            if (path.StartsWith("/ws", StringComparison.OrdinalIgnoreCase)) return;
            if (Path.HasExtension(path)) return; // real static asset miss

            // Reset status and serve the bootstrap.
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ServeBootstrap(ctx, "workspace");
        });
    }

    private static string InjectBootstrapMeta(string html, RuntimeOptions opts, string route)
    {
        // Drop a single meta block right before </head>. Idempotent: if a previous
        // injection is present we still append; the latest meta wins per browser rules.
        var meta = BuildMetaBlock(opts, route);
        var idx = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return meta + html;
        }
        return html.Insert(idx, meta);
    }

    private static string BuildMetaBlock(RuntimeOptions opts, string route)
    {
        return $"""

<meta name="thaddeus-runtime-token" content="{opts.BearerToken}">
<meta name="thaddeus-runtime-port" content="{opts.Port}">
<meta name="thaddeus-runtime-version" content="{opts.Version}">
<meta name="thaddeus-runtime-route" content="{System.Net.WebUtility.HtmlEncode(route)}">

""";
    }

    private static string BuildPlaceholderHtml(RuntimeOptions opts, string route)
    {
        // Shown during development before the SPA has been built into wwwroot.
        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Sir Thaddeus runtime</title>
{{BuildMetaBlock(opts, route)}}<style>
body { font-family: -apple-system, Segoe UI, sans-serif; padding: 2rem; max-width: 40rem; margin: 0 auto; color: #1f2937; }
code { background: #f3f4f6; padding: 0.1rem 0.3rem; border-radius: 0.25rem; }
.banner { background: #fef3c7; border: 1px solid #fbbf24; padding: 0.75rem 1rem; border-radius: 0.5rem; }
</style>
</head>
<body>
<h1>Sir Thaddeus runtime is up.</h1>
<p>Version <code>{{opts.Version}}</code>, port <code>{{opts.Port}}</code>, requested route <code>{{route}}</code>.</p>
<p class="banner">No workspace SPA was found in <code>wwwroot/</code>. Run <code>npm run build</code> in <code>web/</code> and copy the output here, or use the dev script which symlinks the Vite dist.</p>
</body>
</html>
""";
    }
}
