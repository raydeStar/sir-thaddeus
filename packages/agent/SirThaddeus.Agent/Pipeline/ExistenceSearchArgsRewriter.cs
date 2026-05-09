using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Agent.Pipeline;

public sealed class ExistenceSearchArgsRewriter : IToolArgsRewriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Rewrite(TurnContext context, string toolName, string argumentsJson)
    {
        if (!IsWebSearchTool(toolName) ||
            string.IsNullOrWhiteSpace(argumentsJson) ||
            string.IsNullOrWhiteSpace(context.UserText) ||
            !IntentFeatureExtractor.LooksLikeReleasedProductExistenceLookup(context.UserText.ToLowerInvariant()))
        {
            return argumentsJson;
        }

        var subject = ExtractReleasedProductSubject(context.UserText);
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

        payload["query"] = $"{subject} official release date specifications model list";
        payload["recency"] = "any";

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static bool IsWebSearchTool(string toolName)
        => toolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
           toolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase);

    private static string ExtractReleasedProductSubject(string userText)
    {
        var normalized = userText.Trim().TrimEnd('?', '.', '!');
        var match = Regex.Match(
            normalized,
            @"^(?:does|did|is)\s+(.+?)\s+exist(?:\s+as\s+a[n]?\s+.+)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return match.Success
            ? Regex.Replace(match.Groups[1].Value.Trim(), @"\s+", " ")
            : string.Empty;
    }
}
