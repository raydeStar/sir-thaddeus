using System.Text;
using System.Text.Json;

namespace SirThaddeus.Agent.Search;

internal static class WebSearchFollowUpSupport
{
    internal const string SourcesJsonDelimiter = "<!-- SOURCES_JSON -->";

    public static string BuildExtractiveSummary(string toolResult, string query)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
            return $"I found some results for \"{query}\" but couldn't generate a summary. The source links should be visible below.";

        var jsonIdx = toolResult.IndexOf(SourcesJsonDelimiter, StringComparison.Ordinal);
        var contentPart = jsonIdx > 0 ? toolResult[..jsonIdx] : toolResult;

        var lines = contentPart.Split('\n');
        var entries = new List<(string Title, string Source, string Excerpt)>();
        string? currentTitle = null;
        string? currentSource = null;
        var excerptBuilder = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || IsInstructionLine(trimmed))
                continue;

            if (trimmed.Length > 3 && char.IsDigit(trimmed[0]) &&
                (trimmed[1] == '.' || (char.IsDigit(trimmed[1]) && trimmed[2] == '.')))
            {
                if (currentTitle is not null)
                    entries.Add((currentTitle, currentSource ?? "", excerptBuilder.ToString().Trim()));

                excerptBuilder.Clear();
                var dotIdx = trimmed.IndexOf('.');
                var body = trimmed[(dotIdx + 1)..].Trim();
                var dashIdx = body.IndexOf(" — ", StringComparison.Ordinal);

                if (dashIdx > 0)
                {
                    currentTitle = body[..dashIdx].Trim().Trim('"');
                    currentSource = body[(dashIdx + 3)..].Trim();
                }
                else
                {
                    currentTitle = body.Trim('"');
                    currentSource = "";
                }
            }
            else if (currentTitle is not null && line.StartsWith("   ", StringComparison.Ordinal))
            {
                if (excerptBuilder.Length < 300)
                {
                    if (excerptBuilder.Length > 0)
                        excerptBuilder.Append(' ');
                    excerptBuilder.Append(trimmed);
                }
            }
        }

        if (currentTitle is not null)
            entries.Add((currentTitle, currentSource ?? "", excerptBuilder.ToString().Trim()));

        if (entries.Count == 0)
            return $"I found some results for \"{query}\" but couldn't generate a summary. The source links should be visible below.";

        var sb = new StringBuilder();
        sb.AppendLine($"Here's what I found for \"{query}\":");
        sb.AppendLine();

        foreach (var (title, source, excerpt) in entries.Take(5))
        {
            var attribution = string.IsNullOrWhiteSpace(source) ? "" : $" ({source})";
            sb.AppendLine($"**{title}**{attribution}");

            if (!string.IsNullOrWhiteSpace(excerpt))
                sb.AppendLine(TrimToSentence(excerpt, 280));

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    public static List<(string Url, string Title)> ParseSourceUrls(string toolResult)
    {
        var sources = new List<(string Url, string Title)>();
        if (string.IsNullOrWhiteSpace(toolResult))
            return sources;

        var delimIdx = toolResult.IndexOf(SourcesJsonDelimiter, StringComparison.Ordinal);
        if (delimIdx < 0)
            return sources;

        var jsonPart = toolResult[(delimIdx + SourcesJsonDelimiter.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(jsonPart))
            return sources;

        try
        {
            using var doc = JsonDocument.Parse(jsonPart);
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
                var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                var title = item.TryGetProperty("title", out var t) ? t.GetString() : "";
                if (!string.IsNullOrWhiteSpace(url))
                    sources.Add((url!, title ?? ""));
            }
        }
        catch
        {
        }

        return sources;
    }

    public static string StripSourcesJsonSection(string toolResult)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
            return "";

        var idx = toolResult.IndexOf(SourcesJsonDelimiter, StringComparison.Ordinal);
        return idx >= 0 ? toolResult[..idx].TrimEnd() : toolResult.TrimEnd();
    }

    public static bool LooksLikeFollowUpDepthRequest(string userMessage)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var asksForMore =
            lower.Contains("tell me more") ||
            lower.Contains("more info") ||
            lower.Contains("more information") ||
            lower.Contains("more detail") ||
            lower.Contains("more details") ||
            lower.Contains("more about") ||
            lower.Contains("more on") ||
            lower.Contains("go deeper") ||
            lower.Contains("dig into") ||
            lower.Contains("elaborate") ||
            lower.Contains("expand on") ||
            lower.StartsWith("more ", StringComparison.Ordinal);

        if (!asksForMore)
            return false;

        var pointsAtPriorContext =
            lower.Contains("this ") ||
            lower.Contains("that ") ||
            lower.Contains("it ") ||
            lower.Contains("these ") ||
            lower.Contains("those ");

        return pointsAtPriorContext || lower.Contains("tell me more") || lower.StartsWith("more ", StringComparison.Ordinal);
    }

    public static List<(string Url, string Title)> PickRelevantSources(
        string userMessage,
        IReadOnlyList<(string Url, string Title)> sources,
        int maxUrls)
    {
        if (sources.Count == 0)
            return [];

        var keywords = ExtractFollowUpKeywords(userMessage);
        if (keywords.Count == 0)
            return [];

        int Score(string title)
        {
            var tl = (title ?? "").ToLowerInvariant();
            var score = 0;
            foreach (var keyword in keywords)
            {
                if (tl.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    score++;
            }

            return score;
        }

        return sources
            .Select(source => (Source: source, Score: Score(source.Title)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Source.Title.Length)
            .Take(Math.Max(1, maxUrls))
            .Select(item => item.Source)
            .ToList();
    }

    public static bool IsLowSignalBrowserNavigateContent(string? content)
    {
        var lower = (content ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return true;

        var isBasic = lower.Contains("extraction: basic (non-article page)");
        var wordCount = TryParseBrowserNavigateWordCount(content) ?? 0;

        if (isBasic && wordCount < 120)
            return true;

        if (lower.Contains("source: news.google.com") && wordCount < 300)
            return true;

        return false;
    }

    public static string? TryParseFirstBrowserNavigateTitle(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
                continue;

            var raw = trimmed["Title:".Length..].Trim().Trim();
            if (raw.StartsWith('"') && raw.EndsWith('"') && raw.Length >= 2)
                raw = raw[1..^1].Trim();

            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }

        return null;
    }

    public static string BuildExtractiveSummaryFromContent(string content, string? userMessage = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "I fetched the source, but couldn't extract usable content.";

        var lines = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Count == 0)
            return "I fetched the source, but couldn't extract usable content.";

        var bottomLine = lines[0];
        var details = string.Join('\n', lines.Skip(1).Take(4));
        var body = string.IsNullOrWhiteSpace(details)
            ? $"Bottom line:\n{bottomLine}"
            : $"Bottom line:\n{bottomLine}\n\nDetails:\n{details}";

        if (!string.IsNullOrWhiteSpace(userMessage))
        {
            var q = userMessage.Length > 200 ? userMessage[..200] + "\u2026" : userMessage;
            return $"Regarding \"{q}\":\n\n{body}";
        }

        return body;
    }

    private static bool IsInstructionLine(string trimmed) =>
        trimmed.StartsWith("Synthesize", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Summarize", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Cross-reference", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Lead with", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("No URLs", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("ONLY state", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("If a detail", StringComparison.OrdinalIgnoreCase);

    private static string TrimToSentence(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;

        var window = text[..maxChars];
        var lastEnd = Math.Max(
            Math.Max(window.LastIndexOf(". ", StringComparison.Ordinal), window.LastIndexOf("? ", StringComparison.Ordinal)),
            window.LastIndexOf("! ", StringComparison.Ordinal));

        if (lastEnd > maxChars / 2)
            return text[..(lastEnd + 1)];

        var lastSpace = window.LastIndexOf(' ');
        return lastSpace > maxChars / 2 ? text[..lastSpace] + "..." : text[..maxChars] + "...";
    }

    private static IReadOnlyList<string> ExtractFollowUpKeywords(string text)
    {
        var normalized = SearchQueryText.Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(Math.Min(tokens.Length, 8));

        foreach (var token in tokens)
        {
            var lower = token.ToLowerInvariant();
            if (SearchQueryText.IsBannedToken(lower))
                continue;

            if (lower is
                "more" or "info" or "information" or "detail" or "details" or
                "news" or "headline" or "headlines" or
                "story" or "article" or "source" or "sources" or
                "today" or "week" or "month" or "year" or
                "latest" or "recent" or "recently" or "breaking")
                continue;

            kept.Add(lower);
            if (kept.Count >= 6)
                break;
        }

        return kept;
    }

    private static int? TryParseBrowserNavigateWordCount(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Word Count:", StringComparison.OrdinalIgnoreCase))
                continue;

            var raw = trimmed["Word Count:".Length..].Trim().Replace(",", "", StringComparison.Ordinal);
            if (int.TryParse(raw, out var wordCount))
                return wordCount;
        }

        return null;
    }
}