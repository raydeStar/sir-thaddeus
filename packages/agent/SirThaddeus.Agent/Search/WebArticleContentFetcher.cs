using System.Text;
using System.Text.Json;
using SirThaddeus.Agent.Tools;

namespace SirThaddeus.Agent.Search;

internal sealed class WebArticleContentFetcher
{
    private const string BrowseToolName = "browser_navigate";
    private const string BrowseToolNameAlt = "BrowserNavigate";

    private readonly IMcpToolClient _mcp;
    private readonly Action<string, string>? _logEvent;
    private readonly int _maxArticleChars;

    public WebArticleContentFetcher(
        IMcpToolClient mcp,
        Action<string, string>? logEvent = null,
        int maxArticleChars = 3000)
    {
        _mcp = mcp;
        _logEvent = logEvent;
        _maxArticleChars = maxArticleChars;
    }

    public async Task<string?> FetchAsync(
        IReadOnlyList<(string Url, string Title)> sourcesToFetch,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken cancellationToken)
    {
        if (sourcesToFetch.Count == 0)
            return null;

        var fetchTasks = sourcesToFetch.Select(async source =>
        {
            var args = JsonSerializer.Serialize(new { url = source.Url });
            string? content = null;
            var resolvedToolName = BrowseToolName;

            try
            {
                LogToolCall(BrowseToolName, args);
                content = await _mcp.CallToolAsync(BrowseToolName, args, cancellationToken);
            }
            catch
            {
                try
                {
                    resolvedToolName = BrowseToolNameAlt;
                    LogToolCall(BrowseToolNameAlt, args);
                    content = await _mcp.CallToolAsync(BrowseToolNameAlt, args, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logEvent?.Invoke("AGENT_FOLLOWUP_FETCH_FAIL", $"browser_navigate failed for {source.Url}: {ex.Message}");

                    toolCallsMade.Add(new ToolCallRecord
                    {
                        ToolName = resolvedToolName,
                        Arguments = args,
                        Result = $"Error: {ex.Message}",
                        Success = false
                    });

                    return (source.Title, Content: (string?)null, Ok: false);
                }
            }

            toolCallsMade.Add(new ToolCallRecord
            {
                ToolName = resolvedToolName,
                Arguments = args,
                Result = content!.Length > 200 ? content[..200] + "…" : content,
                Success = true
            });

            if (content!.Length > _maxArticleChars)
                content = content[.._maxArticleChars] + "\n[…truncated]";

            return (source.Title, Content: content, Ok: true);
        });

        var results = await Task.WhenAll(fetchTasks);
        var sb = new StringBuilder();

        foreach (var (title, content, ok) in results)
        {
            if (!ok || string.IsNullOrWhiteSpace(content))
                continue;

            if (WebSearchFollowUpSupport.IsLowSignalBrowserNavigateContent(content))
                continue;

            sb.AppendLine($"=== {title} ===");
            sb.AppendLine(content);
            sb.AppendLine();
        }

        var combined = sb.ToString().TrimEnd();
        if (string.IsNullOrWhiteSpace(combined))
            return null;

        _logEvent?.Invoke("AGENT_FOLLOWUP_FETCH_DONE", $"Fetched {results.Count(result => result.Ok)} article(s), {combined.Length} chars total");
        return combined;
    }

    private void LogToolCall(string toolName, string args)
    {
        if (_logEvent is null)
            return;

        var redactedInput = ToolCallRedactor.RedactInput(toolName, args);
        _logEvent("AGENT_TOOL_CALL", $"{toolName}({redactedInput})");
    }
}