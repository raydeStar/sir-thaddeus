using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace SirThaddeus.McpServer.Tools;

// ─────────────────────────────────────────────────────────────────────────
// Browser / Page Reading Tool
//
// Fetches a single web page and extracts its full readable content.
// Delegates to ContentExtractor for the heavy lifting.
//
// Use this when you have a specific URL to read in detail.
// For search-and-read in one step, use WebSearch instead.
//
// Bounds:
//   - HTTP timeout: 20 seconds
//   - Content truncated to ~4000 chars for LLM context
//   - Single page per call, no crawling
// ─────────────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class BrowserTools
{
    private const int MaxContentChars = 4_000;
    private const int HttpTimeoutSecs = 20;

    [McpServerTool, Description(
        "Fetches a specific web page URL and extracts its full readable content " +
        "(title, author, date, and clean text). Use this when you already have a " +
        "URL to read in detail. For searching and reading, use WebSearch instead.")]
    public static async Task<string> BrowserNavigate(
        [Description("The fully qualified URL to fetch (e.g. https://example.com)")] string url,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "Error: URL is required.";

        var result = await ContentExtractor.ExtractAsync(url, HttpTimeoutSecs, cancellationToken);

        if (result.Error is not null)
            return $"Error reading {url}: {result.Error}";

        // ── Format the full-page output ──────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine("=== Web Page Content ===");
        sb.AppendLine($"Title: \"{result.Title}\"");

        if (!string.IsNullOrWhiteSpace(result.Author))
            sb.AppendLine($"Author: {result.Author}");

        if (result.PublishedDate.HasValue)
            sb.AppendLine($"Date: {result.PublishedDate.Value:yyyy-MM-dd}");

        sb.AppendLine($"Source: {result.Domain}");
        sb.AppendLine($"Word Count: {result.WordCount:N0}");

        if (!result.IsArticle)
            sb.AppendLine("Extraction: basic (non-article page)");

        sb.AppendLine();
        sb.AppendLine("=== Content ===");

        var content = ContentExtractor.Truncate(result.TextContent, MaxContentChars);
        sb.AppendLine(content);

        return sb.ToString();
    }
}
