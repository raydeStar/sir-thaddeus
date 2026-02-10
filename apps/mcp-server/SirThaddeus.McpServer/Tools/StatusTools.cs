using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SirThaddeus.McpServer.Tools;

// ─────────────────────────────────────────────────────────────────────────
// Status Tools
//
// Lightweight website/service reachability probe:
//   - HEAD first
//   - GET fallback when HEAD is blocked
//
// Safety:
//   - SSRF guard blocks localhost/private targets by default
//   - Bounded timeout + short cache TTL
// ─────────────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class StatusTools
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    [McpServerTool, Description(
        "Checks whether a URL is reachable via HTTP. " +
        "Uses HEAD first with GET fallback and returns status/latency. " +
        "Local/private network targets are blocked by default.")]
    public static async Task<string> StatusCheckUrl(
        [Description("Target URL or domain (HTTP/HTTPS).")] string url,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Json(new { error = "url is required." });

        try
        {
            var result = await PublicApiToolContext.StatusProbe.Value.CheckAsync(
                url,
                cancellationToken);

            return Json(new
            {
                url = result.Url,
                reachable = result.Reachable,
                httpStatus = result.HttpStatus,
                method = result.Method,
                latencyMs = result.LatencyMs,
                error = result.Error,
                checkedAt = result.CheckedAt.ToString("o"),
                source = result.Source,
                cache = new
                {
                    hit = result.Cache.Hit,
                    ageSeconds = result.Cache.AgeSeconds
                }
            });
        }
        catch (OperationCanceledException)
        {
            return Json(new
            {
                error = "Status probe cancelled.",
                url
            });
        }
        catch (Exception ex)
        {
            return Json(new
            {
                error = $"Status probe failed: {ex.Message}",
                url
            });
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOpts);
}
