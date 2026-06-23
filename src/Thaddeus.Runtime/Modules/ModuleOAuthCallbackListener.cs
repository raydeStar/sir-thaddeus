using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Thaddeus.Runtime.Modules;

public sealed class ModuleOAuthCallbackListener : BackgroundService
{
    public const int CallbackPort = 8787;

    private readonly ModuleRuntimeService _modules;
    private readonly ILogger<ModuleOAuthCallbackListener> _logger;
    private HttpListener? _listener;

    public ModuleOAuthCallbackListener(
        ModuleRuntimeService modules,
        ILogger<ModuleOAuthCallbackListener> logger)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{CallbackPort}/");

        try
        {
            listener.Start();
            _listener = listener;
            _logger.LogInformation("module.oauth_callback.ready url=http://localhost:{Port}/oauth/callback", CallbackPort);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "module.oauth_callback.unavailable port={Port}", CallbackPort);
            listener.Close();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "module.oauth_callback.accept_failed");
                continue;
            }

            _ = Task.Run(() => HandleAsync(context, stoppingToken), CancellationToken.None);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _listener?.Close();
        }
        catch
        {
            // Best-effort shutdown.
        }

        return base.StopAsync(cancellationToken);
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        if (!string.Equals(request.Url?.AbsolutePath, "/oauth/callback", StringComparison.OrdinalIgnoreCase))
        {
            await WritePageAsync(context.Response, 404, "Sir Thaddeus did not recognize this local callback.", "Return to the app and start auth again.").ConfigureAwait(false);
            return;
        }

        if (!IsLoopback(request.RemoteEndPoint?.Address))
        {
            await WritePageAsync(context.Response, 403, "Sir Thaddeus blocked this OAuth callback.", "Only loopback callbacks are accepted.").ConfigureAwait(false);
            return;
        }

        if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            await WritePageAsync(context.Response, 405, "Sir Thaddeus OAuth callback only accepts GET.", "Return to the app and start auth again.").ConfigureAwait(false);
            return;
        }

        var oauthError = request.QueryString["error"];
        if (!string.IsNullOrWhiteSpace(oauthError))
        {
            var description = request.QueryString["error_description"] ?? "Google did not authorize the request.";
            _logger.LogWarning("module.oauth_callback.provider_error error={Error}", oauthError);
            await WritePageAsync(context.Response, 400, "Google authorization was not completed.", description).ConfigureAwait(false);
            return;
        }

        var code = request.QueryString["code"];
        var state = request.QueryString["state"];
        if (string.IsNullOrWhiteSpace(code))
        {
            await WritePageAsync(context.Response, 400, "Sir Thaddeus did not receive an OAuth code.", "Return to the app and start auth again.").ConfigureAwait(false);
            return;
        }

        try
        {
            using var args = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                code,
                state = string.IsNullOrWhiteSpace(state) ? null : state
            }));

            await _modules.InvokeToolAsync(
                    ModuleRuntimeService.HealthPackModuleId,
                    "health.complete_provider_auth",
                    args.RootElement,
                    ct)
                .ConfigureAwait(false);

            await WritePageAsync(
                    context.Response,
                    200,
                    "Health Pack is connected.",
                    "You can close this tab and return to Sir Thaddeus.")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "module.oauth_callback.complete_failed");
            await WritePageAsync(
                    context.Response,
                    500,
                    "Sir Thaddeus could not complete authorization.",
                    ex.Message)
                .ConfigureAwait(false);
        }
    }

    private static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
            return false;
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.IsIPv4MappedToIPv6)
            return IPAddress.IsLoopback(address.MapToIPv4());
        return false;
    }

    private static async Task WritePageAsync(HttpListenerResponse response, int statusCode, string title, string message)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";

        var body = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{Html(title)}}</title>
              <style>
                :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
                body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: Canvas; color: CanvasText; }
                main { width: min(36rem, calc(100vw - 2rem)); }
                h1 { font-size: 1.5rem; margin: 0 0 .75rem; }
                p { color: color-mix(in srgb, CanvasText 74%, transparent); line-height: 1.55; }
              </style>
            </head>
            <body>
              <main>
                <h1>{{Html(title)}}</h1>
                <p>{{Html(message)}}</p>
              </main>
            </body>
            </html>
            """;

        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);
}
