using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.PostProcessing;

/// <summary>
/// Cleans source sections and upgrades numbered source items to HTML links
/// using trusted URLs from web tool metadata.
/// </summary>
internal static partial class SourceCitationFormatter
{
    private const string SourcesDelimiter = "<!-- SOURCES_JSON -->";

    public static string Apply(string text, IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? "";

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();

        var sourcesHeaderIndex = FindSourcesHeader(lines);
        if (sourcesHeaderIndex < 0)
            return text.Trim();

        var sources = ExtractLatestSources(toolCallsMade);
        var sawSourceItem = false;

        for (var i = sourcesHeaderIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;

            var numbered = NumberedLineRegex().Match(line);
            if (!numbered.Success)
                continue;

            sawSourceItem = true;
            var number = numbered.Groups["num"].Value;
            var body = numbered.Groups["body"].Value.Trim();

            // Removes dangling placeholders like "3.".
            if (string.IsNullOrWhiteSpace(body))
            {
                lines[i] = "";
                continue;
            }

            if (body.Contains("<a ", StringComparison.OrdinalIgnoreCase))
                continue;

            var source = ResolveSource(number, body, sources);
            if (source is null)
                continue;

            var href = BuildCompactHref(source);
            if (string.IsNullOrWhiteSpace(href))
                continue;

            lines[i] = $"{number}. <a href=\"{WebUtility.HtmlEncode(href)}\">{WebUtility.HtmlEncode(body)}</a>";
        }

        if (!sawSourceItem)
            return text.Trim();

        return CompactLines(lines);
    }

    private static int FindSourcesHeader(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var normalized = lines[i].Trim().Trim('*').Trim();
            if (normalized.Equals("sources", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("sources:", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("source", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("source:", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static SourceLink? ResolveSource(
        string sourceNumber,
        string lineBody,
        IReadOnlyList<SourceLink> sources)
    {
        if (sources.Count == 0)
            return null;

        if (int.TryParse(sourceNumber, out var number) &&
            number >= 1 &&
            number <= sources.Count)
        {
            return sources[number - 1];
        }

        var lowered = lineBody.ToLowerInvariant();
        foreach (var source in sources)
        {
            if (!string.IsNullOrWhiteSpace(source.Title) &&
                lowered.Contains(source.Title, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }

            if (!string.IsNullOrWhiteSpace(source.Domain) &&
                lowered.Contains(source.Domain, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
        }

        return null;
    }

    private static string BuildCompactHref(SourceLink source)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
            return "";

        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri))
            return source.Url;

        if (uri.Host.Contains("news.google.com", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(source.Domain))
        {
            return EnsureHttps(source.Domain);
        }

        if (source.Url.Length > 160)
            return $"{uri.Scheme}://{uri.Host}";

        return source.Url;
    }

    private static string EnsureHttps(string domainOrUrl)
    {
        var value = domainOrUrl.Trim();
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return $"https://{value}";
    }

    private static IReadOnlyList<SourceLink> ExtractLatestSources(
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        for (var i = toolCallsMade.Count - 1; i >= 0; i--)
        {
            var toolCall = toolCallsMade[i];
            if (!IsWebSearchTool(toolCall.ToolName))
                continue;

            var parsed = ParseSources(toolCall.Result);
            if (parsed.Count > 0)
                return parsed;
        }

        return [];
    }

    private static bool IsWebSearchTool(string toolName)
    {
        return toolName.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("WebSearch", StringComparison.OrdinalIgnoreCase);
    }

    private static List<SourceLink> ParseSources(string toolResult)
    {
        var sources = new List<SourceLink>();
        if (string.IsNullOrWhiteSpace(toolResult))
            return sources;

        var delimiterIndex = toolResult.IndexOf(SourcesDelimiter, StringComparison.Ordinal);
        if (delimiterIndex < 0)
            return sources;

        var json = toolResult[(delimiterIndex + SourcesDelimiter.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(json))
            return sources;

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement itemsElement;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                itemsElement = doc.RootElement;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                     doc.RootElement.TryGetProperty("sources", out var sourcesElement) &&
                     sourcesElement.ValueKind == JsonValueKind.Array)
            {
                itemsElement = sourcesElement;
            }
            else
            {
                return sources;
            }

            foreach (var item in itemsElement.EnumerateArray())
            {
                var url = item.TryGetProperty("url", out var urlEl)
                    ? urlEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var title = item.TryGetProperty("title", out var titleEl)
                    ? titleEl.GetString() ?? ""
                    : "";
                var domain = item.TryGetProperty("domain", out var domainEl)
                    ? domainEl.GetString() ?? ""
                    : "";

                sources.Add(new SourceLink(url, title, domain));
            }
        }
        catch
        {
            // Source parsing is best-effort only.
        }

        return sources;
    }

    private static string CompactLines(IReadOnlyList<string> lines)
    {
        var compact = new List<string>(lines.Count);
        var previousBlank = false;
        foreach (var line in lines)
        {
            var isBlank = string.IsNullOrWhiteSpace(line);
            if (isBlank && previousBlank)
                continue;

            compact.Add(line);
            previousBlank = isBlank;
        }

        return string.Join('\n', compact).Trim();
    }

    private sealed record SourceLink(string Url, string Title, string Domain);

    [GeneratedRegex(@"^(?<num>\d+)\.\s*(?<body>.*)$", RegexOptions.Compiled)]
    private static partial Regex NumberedLineRegex();
}
