using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SirThaddeus.McpServer.Tools;

// ─────────────────────────────────────────────────────────────────────────
// Feed Tools
//
// Fetches and parses RSS/Atom feeds directly (no third-party API keys).
// Bounded by strict timeout, payload size limit, and max returned items.
// ─────────────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class FeedTools
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    [McpServerTool, Description(
        "Fetches and parses an RSS/Atom feed URL. " +
        "Returns bounded feed metadata and latest items. " +
        "Local/private network targets are blocked by default.")]
    public static async Task<string> FeedFetch(
        [Description("Feed URL (HTTP/HTTPS). Can be RSS or Atom.")] string url,
        [Description("Max items to return (1-20, default 5)")] int maxItems = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Json(new { error = "url is required." });

        maxItems = Math.Clamp(maxItems, 1, 20);

        try
        {
            var result = await PublicApiToolContext.FeedProvider.Value.FetchAsync(
                url,
                maxItems,
                cancellationToken);

            return Json(new
            {
                url = result.Url,
                feedTitle = result.FeedTitle,
                description = result.Description,
                sourceHost = result.SourceHost,
                source = result.Source,
                truncated = result.Truncated,
                cache = new
                {
                    hit = result.Cache.Hit,
                    ageSeconds = result.Cache.AgeSeconds
                },
                items = result.Items.Select(i => new
                {
                    title = i.Title,
                    link = i.Link,
                    summary = i.Summary,
                    author = i.Author,
                    publishedAt = i.PublishedAt?.ToString("o")
                }).ToArray()
            });
        }
        catch (OperationCanceledException)
        {
            return Json(new
            {
                error = "Feed fetch cancelled.",
                url
            });
        }
        catch (Exception ex)
        {
            return Json(new
            {
                error = $"Feed fetch failed: {ex.Message}",
                url
            });
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOpts);
}
