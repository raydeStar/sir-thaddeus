using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Detects a narrow end-exclusive range mismatch without knowing the tool or
/// domain. The caller remains responsible for proving the tool is non-mutating
/// and for making at most one audited correction attempt.
/// </summary>
public static partial class RequestedTerminalDateEvidenceCoveragePolicy
{
    public sealed record Correction(
        DateOnly RequestedDate,
        string CorrectedArguments);

    public static bool TryBuildCorrection(
        string? userText,
        string arguments,
        string result,
        out Correction correction)
    {
        correction = null!;
        if (!TryReadRequestedMarketCloseDate(userText, out var requestedDate) ||
            !TryReadEndDate(arguments, out var argumentDate, out var argumentsObject) ||
            argumentDate != requestedDate ||
            !TryReadReturnedDates(result, out var returnedDates) ||
            returnedDates.Contains(requestedDate) ||
            returnedDates.Max() >= requestedDate)
        {
            return false;
        }

        argumentsObject["end_date"] = requestedDate.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        correction = new Correction(
            requestedDate,
            argumentsObject.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        return true;
    }

    public static bool ContainsRequestedDate(string result, DateOnly requestedDate)
        => TryReadReturnedDates(result, out var returnedDates) &&
           returnedDates.Contains(requestedDate);

    private static bool TryReadRequestedMarketCloseDate(
        string? userText,
        out DateOnly requestedDate)
    {
        requestedDate = default;
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var match = MarketCloseDateRegex().Match(userText);
        return match.Success && DateOnly.TryParseExact(
            match.Groups["date"].Value,
            "MMMM d, yyyy",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out requestedDate);
    }

    private static bool TryReadEndDate(
        string arguments,
        out DateOnly endDate,
        out JsonObject argumentsObject)
    {
        endDate = default;
        argumentsObject = null!;
        try
        {
            argumentsObject = JsonNode.Parse(arguments) as JsonObject ?? null!;
            return argumentsObject is not null &&
                   argumentsObject["end_date"] is JsonValue value &&
                   value.TryGetValue<string>(out var rawDate) &&
                   DateOnly.TryParseExact(
                       rawDate,
                       "yyyy-MM-dd",
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.None,
                       out endDate);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadReturnedDates(
        string result,
        out HashSet<DateOnly> dates)
    {
        dates = [];
        try
        {
            using var document = JsonDocument.Parse(result);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    return false;

                var dateProperty = row.EnumerateObject().FirstOrDefault(property =>
                    string.Equals(property.Name, "Date", StringComparison.OrdinalIgnoreCase));
                if (dateProperty.Value.ValueKind != JsonValueKind.String)
                    return false;

                var rawDate = dateProperty.Value.GetString();
                if (string.IsNullOrWhiteSpace(rawDate) || rawDate.Length < 10 ||
                    !DateOnly.TryParseExact(
                        rawDate[..10],
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var parsedDate))
                {
                    return false;
                }
                dates.Add(parsedDate);
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return dates.Count > 0;
    }

    [GeneratedRegex(
        @"\bmarket\s+clos(?:e|ed)\s+on\s+(?<date>[A-Za-z]+\s+\d{1,2},\s+\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarketCloseDateRegex();
}
