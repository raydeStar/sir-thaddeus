using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Agent.Pipeline;

public sealed class FactSearchArgsRewriter : IToolArgsRewriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Rewrite(TurnContext context, string toolName, string argumentsJson)
    {
        if (!IsWebSearchTool(toolName) ||
            string.IsNullOrWhiteSpace(argumentsJson) ||
            string.IsNullOrWhiteSpace(context.UserText) ||
            !LooksLikeVersionFactLookup(context.UserText))
        {
            return argumentsJson;
        }

        var subject = ExtractVersionSubject(context.UserText);
        if (string.IsNullOrWhiteSpace(subject))
            return argumentsJson;

        Dictionary<string, object?> payload;
        try
        {
            payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            payload = [];
        }

        payload["query"] = $"latest stable version of {FormatSearchSubject(subject)} official documentation release notes";
        payload["recency"] = "any";
        if (!payload.ContainsKey("maxResults"))
            payload["maxResults"] = 5;

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string FormatSearchSubject(string subject)
    {
        var normalized = subject.Trim();
        if (normalized.Length > 1 &&
            normalized[0] == '.' &&
            normalized[1..].All(char.IsLetterOrDigit))
        {
            return "dot" + normalized[1..].ToLowerInvariant();
        }

        if (Regex.IsMatch(normalized, @"[^A-Za-z0-9 +#-]", RegexOptions.CultureInvariant))
            return $"\"{normalized.Replace("\"", string.Empty, StringComparison.Ordinal)}\"";

        return normalized;
    }

    private static bool IsWebSearchTool(string toolName)
        => toolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
           toolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeVersionFactLookup(string userText)
    {
        var lower = userText.ToLowerInvariant();
        return (lower.Contains("latest", StringComparison.Ordinal) ||
                lower.Contains("current", StringComparison.Ordinal)) &&
               lower.Contains("version", StringComparison.Ordinal) &&
               (lower.Contains("stable", StringComparison.Ordinal) ||
                lower.Contains("release", StringComparison.Ordinal));
    }

    private static string ExtractVersionSubject(string userText)
    {
        var normalized = userText.Trim().TrimEnd('?', '.', '!');
        var match = Regex.Match(
            normalized,
            @"\bversion\s+of\s+(?<subject>.+?)(?:\s+as\s+of\b|\s+right\s+now\b|\s+today\b|[?.!]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            match = Regex.Match(
                normalized,
                @"\b(?:latest|current)\s+(?:stable\s+)?(?:release\s+)?version\s+(?<subject>[A-Za-z0-9_.#+ -]{1,80}?)(?:\s+as\s+of\b|\s+right\s+now\b|\s+today\b|[?.!]|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (!match.Success)
            return string.Empty;

        var subject = Regex.Replace(match.Groups["subject"].Value, @"\s+", " ").Trim();
        subject = Regex.Replace(subject, @"\b(?:answer|respond|keep|line)\b.*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        return subject.Trim(',', ':', ';', '-', ' ');
    }
}
