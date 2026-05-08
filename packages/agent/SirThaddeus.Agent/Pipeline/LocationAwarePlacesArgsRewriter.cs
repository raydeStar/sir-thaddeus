using System.Text.Json;
using System.Text.Json.Nodes;

namespace SirThaddeus.Agent.Pipeline;

public sealed class LocationAwarePlacesArgsRewriter : IToolArgsRewriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Func<string?> _resolveLocationHint;

    public LocationAwarePlacesArgsRewriter(Func<string?> resolveLocationHint)
    {
        _resolveLocationHint = resolveLocationHint ?? throw new ArgumentNullException(nameof(resolveLocationHint));
    }

    public string Rewrite(TurnContext context, string toolName, string argumentsJson)
    {
        if (!IsPlacesTool(toolName) || string.IsNullOrWhiteSpace(argumentsJson))
            return argumentsJson;

        var configuredLocation = NormalizeLocation(_resolveLocationHint());
        if (string.IsNullOrWhiteSpace(configuredLocation))
            return argumentsJson;

        JsonObject? args;
        try
        {
            args = JsonNode.Parse(argumentsJson) as JsonObject;
        }
        catch (JsonException)
        {
            return argumentsJson;
        }

        if (args is null)
            return argumentsJson;

        var query = GetString(args, "query");
        var currentHint = GetString(args, "userLocationHint");
        if (!ShouldApplyLocationHint(context.UserText, query, currentHint))
            return argumentsJson;

        args["userLocationHint"] = configuredLocation;
        return args.ToJsonString(JsonOptions);
    }

    private static bool IsPlacesTool(string toolName)
        => string.Equals(toolName, ToolNames.PlacesDiscover, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(toolName, ToolNames.PlacesDiscoverAlt, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(toolName, ToolNames.PlacesLookup, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(toolName, ToolNames.PlacesLookupAlt, StringComparison.OrdinalIgnoreCase);

    private static bool ShouldApplyLocationHint(string? userText, string query, string currentHint)
    {
        if (!string.IsNullOrWhiteSpace(currentHint) && !IsGenericLocationHint(currentHint))
            return false;

        if (IsGenericLocationHint(currentHint))
            return true;

        if (ContainsRelativeLocationCue(query))
            return true;

        return ContainsRelativeLocationCue(userText ?? string.Empty) && !ContainsConcreteLocationCue(query);
    }

    private static string GetString(JsonObject args, string propertyName)
    {
        if (!args.TryGetPropertyValue(propertyName, out var node) || node is null)
            return string.Empty;

        try
        {
            return node.GetValue<string>() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string NormalizeLocation(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static bool ContainsRelativeLocationCue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var lower = value.ToLowerInvariant();
        return lower.Contains("near me", StringComparison.Ordinal) ||
               lower.Contains("nearby", StringComparison.Ordinal) ||
               lower.Contains("around here", StringComparison.Ordinal) ||
               lower.Contains("my area", StringComparison.Ordinal) ||
               lower.Contains("where i am", StringComparison.Ordinal) ||
               lower.Contains("close to me", StringComparison.Ordinal) ||
               lower.Contains("local", StringComparison.Ordinal);
    }

    private static bool ContainsConcreteLocationCue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var lower = value.ToLowerInvariant();
        if (lower.Contains("near me", StringComparison.Ordinal) ||
            lower.Contains("nearby", StringComparison.Ordinal) ||
            lower.Contains("around here", StringComparison.Ordinal))
        {
            return false;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(
            value,
            @"\b(?:in|near|around)\s+[A-Za-z][A-Za-z .'-]+(?:,\s*[A-Z]{2})?\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static bool IsGenericLocationHint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "me" or "near me" or "nearby" or "here" or "around here" or "my area" or "current location" or "this area" or "local";
    }
}