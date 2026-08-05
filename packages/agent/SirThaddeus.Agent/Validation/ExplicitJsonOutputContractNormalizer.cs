using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Validation;

/// <summary>
/// Enforces answer-blind JSON value types declared by an explicit caller-owned
/// output template. It never derives or verifies answer values.
/// </summary>
public static partial class ExplicitJsonOutputContractNormalizer
{
    /// <summary>
    /// Converts unambiguous numeric strings only at template paths whose value
    /// begins with <c>[NUMBER]</c>. Invalid JSON, prose values, undeclared paths,
    /// and already-correct values are preserved exactly.
    /// </summary>
    public static bool TryNormalize(
        string? response,
        JsonElement? outputTemplate,
        out string normalized,
        out int changeCount)
    {
        normalized = response ?? string.Empty;
        changeCount = 0;
        if (string.IsNullOrWhiteSpace(response) || outputTemplate is null)
            return false;

        var candidate = ExtractJson(response);
        if (candidate is null)
            return false;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(candidate);
        }
        catch (JsonException)
        {
            return false;
        }

        if (root is null)
            return false;

        root = NormalizeNode(root, outputTemplate.Value, ref changeCount);
        if (changeCount == 0)
            return false;

        normalized = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false
        });
        return true;
    }

    private static JsonNode NormalizeNode(
        JsonNode node,
        JsonElement template,
        ref int changeCount)
    {
        if (IsNumberTemplate(template) &&
            node is JsonValue value &&
            value.TryGetValue<string>(out var text) &&
            TryParseUnambiguousNumber(text, out var number))
        {
            changeCount++;
            return number;
        }

        if (node is JsonObject obj && template.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in obj.ToArray())
            {
                if (property.Value is null ||
                    !template.TryGetProperty(property.Key, out var childTemplate))
                {
                    continue;
                }

                var normalizedChild = NormalizeNode(
                    property.Value,
                    childTemplate,
                    ref changeCount);
                if (!ReferenceEquals(normalizedChild, property.Value))
                    obj[property.Key] = normalizedChild;
            }
        }
        else if (node is JsonArray array &&
                 template.ValueKind == JsonValueKind.Array &&
                 template.GetArrayLength() > 0)
        {
            var itemTemplate = template[0];
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is { } item)
                {
                    var normalizedItem = NormalizeNode(item, itemTemplate, ref changeCount);
                    if (!ReferenceEquals(normalizedItem, item))
                        array[index] = normalizedItem;
                }
            }
        }

        return node;
    }

    private static bool IsNumberTemplate(JsonElement template)
        => template.ValueKind == JsonValueKind.String &&
           template.GetString()?.TrimStart().StartsWith(
               "[NUMBER]",
               StringComparison.OrdinalIgnoreCase) == true;

    private static bool TryParseUnambiguousNumber(string text, out JsonNode number)
    {
        number = null!;
        var match = NumericTextRegex().Match(text);
        if (!match.Success)
            return false;

        var numericText = match.Groups["number"].Value.Replace(",", string.Empty);
        if (!numericText.Contains('.', StringComparison.Ordinal) &&
            !numericText.Contains('e', StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(
                numericText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var integer))
        {
            number = JsonValue.Create(integer)!;
            return true;
        }

        if (!decimal.TryParse(
                numericText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var decimalValue))
        {
            return false;
        }

        number = JsonValue.Create(decimalValue)!;
        return true;
    }

    private static string? ExtractJson(string response)
    {
        var candidate = response.Trim();
        if (!candidate.StartsWith("```", StringComparison.Ordinal) ||
            !candidate.EndsWith("```", StringComparison.Ordinal))
        {
            return candidate;
        }

        var firstNewline = candidate.IndexOf('\n');
        return firstNewline < 0
            ? null
            : candidate[(firstNewline + 1)..^3].Trim();
    }

    [GeneratedRegex(
        @"^\s*(?:[$€£])?\s*(?<number>[+-]?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?(?:[eE][+-]?\d+)?)\s*%?\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NumericTextRegex();
}
