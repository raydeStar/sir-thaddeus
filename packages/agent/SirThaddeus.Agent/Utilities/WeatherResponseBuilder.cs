using System.Text.Json;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Utilities;

/// <summary>
/// Pure-function builder for deterministic weather response text. No
/// instance state; all methods are static and the caller supplies any
/// runtime values (e.g. <c>preferredUnits</c>) as parameters.
/// </summary>
public static partial class WeatherResponseBuilder
{
    /// <summary>
    /// Builds a short deterministic weather response from the normalized
    /// weather_forecast MCP JSON output.
    /// </summary>
    public static string? TryBuildBriefFromForecastJson(
        string forecastJson,
        string userMessage,
        string fallbackLocation,
        string? preferredUnits)
    {
        if (string.IsNullOrWhiteSpace(forecastJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(forecastJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(err.GetString()))
            {
                return null;
            }

            var fromMessage = ExtractLocationFromMessage(userMessage);
            var location = !string.IsNullOrWhiteSpace(fromMessage) ? fromMessage! : fallbackLocation;
            if (root.TryGetProperty("location", out var loc) &&
                loc.ValueKind == JsonValueKind.Object &&
                loc.TryGetProperty("name", out var ln) &&
                !string.IsNullOrWhiteSpace(ln.GetString()) &&
                string.IsNullOrWhiteSpace(fromMessage))
            {
                var providerLocation = ln.GetString()!;
                location = LooksLikeCoordinateLabel(providerLocation) && !string.IsNullOrWhiteSpace(fromMessage)
                    ? fromMessage!
                    : providerLocation;
            }

            int? currentTemp = null;
            string unit = "";
            string condition = "";

            if (root.TryGetProperty("current", out var current) &&
                current.ValueKind == JsonValueKind.Object)
            {
                if (current.TryGetProperty("temperature", out var t) && t.TryGetInt32(out var ti))
                    currentTemp = ti;
                if (current.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String)
                    unit = u.GetString() ?? "";
                if (current.TryGetProperty("condition", out var c) && c.ValueKind == JsonValueKind.String)
                    condition = c.GetString() ?? "";
            }

            int? avgTemp = null;
            if (root.TryGetProperty("daily", out var daily) &&
                daily.ValueKind == JsonValueKind.Array)
            {
                foreach (var day in daily.EnumerateArray())
                {
                    if (day.TryGetProperty("avgTemp", out var avg) && avg.TryGetInt32(out var av))
                    {
                        avgTemp = av;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(location))
                location = "there";

            var normalizedUnit = NormalizeTemperatureUnit(unit);
            var shouldRespectExplicitTempUnit = HasExplicitTemperatureUnitRequest(userMessage);
            var normalizedPreferred = NormalizeUnitPreference(preferredUnits);

            if (!shouldRespectExplicitTempUnit)
            {
                if (normalizedPreferred == "metric" && string.Equals(normalizedUnit, "F", StringComparison.Ordinal))
                {
                    currentTemp = ConvertTemperature(currentTemp, "F", "C");
                    avgTemp = ConvertTemperature(avgTemp, "F", "C");
                    normalizedUnit = "C";
                }
                else if (normalizedPreferred == "imperial" && string.Equals(normalizedUnit, "C", StringComparison.Ordinal))
                {
                    currentTemp = ConvertTemperature(currentTemp, "C", "F");
                    avgTemp = ConvertTemperature(avgTemp, "C", "F");
                    normalizedUnit = "F";
                }
            }

            var unitSuffix = normalizedUnit;
            var avgSuffix = string.IsNullOrWhiteSpace(unitSuffix) ? "" : unitSuffix;
            if (LooksLikeActivityAdviceRequest(userMessage))
            {
                return BuildActivityAdvice(
                    location,
                    currentTemp,
                    unitSuffix,
                    condition,
                    avgTemp,
                    avgSuffix);
            }

            if (currentTemp.HasValue && !string.IsNullOrWhiteSpace(condition))
            {
                var line = $"Today in {location}, it's about **{currentTemp}{unitSuffix}** and **{condition}** right now.";
                return avgTemp.HasValue
                    ? $"{line} Avg temp: **{avgTemp}{avgSuffix}**."
                    : line;
            }

            if (currentTemp.HasValue)
            {
                var line = $"Today in {location}, it's about **{currentTemp}{unitSuffix}** right now.";
                return avgTemp.HasValue
                    ? $"{line} Avg temp: **{avgTemp}{avgSuffix}**."
                    : line;
            }

            if (!string.IsNullOrWhiteSpace(condition))
            {
                var line = $"Today in {location}, conditions are **{condition}** right now.";
                return avgTemp.HasValue
                    ? $"{line} Avg temp: **{avgTemp}{avgSuffix}**."
                    : line;
            }

            if (avgTemp.HasValue)
                return $"In {location}, avg temp is **{avgTemp}{avgSuffix}**.";

            return null;
        }
        catch
        {
            return null;
        }
    }

    public static string? TryBuildBriefFromForecastSummary(
        string forecastSummary,
        string userMessage,
        string fallbackLocation)
    {
        if (string.IsNullOrWhiteSpace(forecastSummary))
            return null;

        var current = Regex.Match(
            forecastSummary,
            @"\bcurrent\s*=\s*(?<temp>-?\d{1,3})\s*(?<unit>[FC])\s+(?<condition>[^\]\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!current.Success)
            return null;

        var fromMessage = ExtractLocationFromMessage(userMessage);
        var location = !string.IsNullOrWhiteSpace(fromMessage) ? fromMessage! : fallbackLocation;
        if (string.IsNullOrWhiteSpace(location))
            location = "there";

        var temp = current.Groups["temp"].Value;
        var unit = NormalizeTemperatureUnit(current.Groups["unit"].Value);
        var condition = current.Groups["condition"].Value.Trim().TrimEnd('.', ';', ',');
        if (string.IsNullOrWhiteSpace(condition))
            return $"Today in {location}, it's about **{temp}{unit}** right now.";

        return $"Today in {location}, it's about **{temp}{unit}** and **{condition}** right now.";
    }

    public static string BuildActivityAdvice(
        string location,
        int? currentTemp,
        string unitSuffix,
        string? condition,
        int? avgTemp,
        string avgSuffix)
    {
        var conditionLower = (condition ?? "").ToLowerInvariant();
        var tempForHeuristic = ToFahrenheit(currentTemp ?? avgTemp, unitSuffix);

        var isWet =
            conditionLower.Contains("rain", StringComparison.Ordinal) ||
            conditionLower.Contains("snow", StringComparison.Ordinal) ||
            conditionLower.Contains("sleet", StringComparison.Ordinal) ||
            conditionLower.Contains("drizzle", StringComparison.Ordinal) ||
            conditionLower.Contains("shower", StringComparison.Ordinal) ||
            conditionLower.Contains("storm", StringComparison.Ordinal);

        var isIcy =
            conditionLower.Contains("ice", StringComparison.Ordinal) ||
            conditionLower.Contains("freez", StringComparison.Ordinal);

        var isWindy =
            conditionLower.Contains("wind", StringComparison.Ordinal) ||
            conditionLower.Contains("gust", StringComparison.Ordinal);

        var isCold = tempForHeuristic is <= 45;
        var isHot = tempForHeuristic is >= 85;

        var snapshot = BuildSnapshot(location, currentTemp, unitSuffix, condition, avgTemp, avgSuffix);
        var plan = "Good options: a short walk, errands on foot, or light outdoor activity.";
        var caution = "Bring a layer and check conditions before heading out.";

        if (isWet || isIcy || isCold)
        {
            plan = "Best fit right now: mostly indoor plans (gym/rec center, cafe + reading, movie/museum).";
            caution = "If you go outside, keep it short and use warm waterproof layers plus good traction.";
        }
        else if (isHot)
        {
            plan = "Best fit right now: early/late outdoor time, shaded spots, or indoor options with AC.";
            caution = "Bring water and avoid long midday exposure.";
        }
        else if (isWindy)
        {
            plan = "Good options: low-exposure outdoor plans or indoor activities with easy fallback.";
            caution = "Avoid long exposed routes if gusts pick up.";
        }

        return $"{snapshot} {plan} {caution}";
    }

    public static string BuildSnapshot(
        string location,
        int? currentTemp,
        string unitSuffix,
        string? condition,
        int? avgTemp,
        string avgSuffix)
    {
        if (currentTemp.HasValue && !string.IsNullOrWhiteSpace(condition))
            return $"Today in {location}, it's about {currentTemp}{unitSuffix} with {condition.ToLowerInvariant()} right now.";

        if (currentTemp.HasValue)
            return $"Today in {location}, it's about {currentTemp}{unitSuffix} right now.";

        if (!string.IsNullOrWhiteSpace(condition))
            return $"Today in {location}, conditions are {condition.ToLowerInvariant()} right now.";

        if (avgTemp.HasValue)
            return $"In {location}, average temp is around {avgTemp}{avgSuffix}.";

        return $"In {location}, weather conditions are available.";
    }

    public static string NormalizeTemperatureUnit(string? rawUnit)
    {
        var lower = (rawUnit ?? "").Trim().ToLowerInvariant();
        return lower switch
        {
            "f" or "fahrenheit" => "F",
            "c" or "celsius" => "C",
            _ => ""
        };
    }

    public static int? ConvertTemperature(int? value, string fromUnit, string toUnit)
    {
        if (!value.HasValue)
            return null;

        if (string.Equals(fromUnit, toUnit, StringComparison.OrdinalIgnoreCase))
            return value.Value;

        if (string.Equals(fromUnit, "C", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(toUnit, "F", StringComparison.OrdinalIgnoreCase))
        {
            return (int)Math.Round((value.Value * 9.0 / 5.0) + 32.0, MidpointRounding.AwayFromZero);
        }

        if (string.Equals(fromUnit, "F", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(toUnit, "C", StringComparison.OrdinalIgnoreCase))
        {
            return (int)Math.Round((value.Value - 32.0) * 5.0 / 9.0, MidpointRounding.AwayFromZero);
        }

        return value.Value;
    }

    public static bool HasExplicitTemperatureUnitRequest(string userMessage)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        if (lower.Contains("celsius", StringComparison.Ordinal) ||
            lower.Contains("fahrenheit", StringComparison.Ordinal) ||
            lower.Contains("°c", StringComparison.Ordinal) ||
            lower.Contains("°f", StringComparison.Ordinal))
        {
            return true;
        }

        return Regex.IsMatch(lower, @"\bin\s+c\b|\bin\s+f\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public static double? ToFahrenheit(int? temp, string unitSuffix)
    {
        if (!temp.HasValue)
            return null;

        if (string.Equals(unitSuffix, "C", StringComparison.OrdinalIgnoreCase))
            return (temp.Value * 9.0 / 5.0) + 32.0;

        return temp.Value;
    }

    public static bool LooksLikeActivityAdviceRequest(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var lowerMessage = message.ToLowerInvariant();

        var hasWeatherCue =
            lowerMessage.Contains("weather", StringComparison.Ordinal) ||
            lowerMessage.Contains("forecast", StringComparison.Ordinal) ||
            lowerMessage.Contains("temperature", StringComparison.Ordinal) ||
            lowerMessage.Contains("temp", StringComparison.Ordinal) ||
            lowerMessage.Contains("rain", StringComparison.Ordinal) ||
            lowerMessage.Contains("snow", StringComparison.Ordinal);

        if (!hasWeatherCue)
            return false;

        return lowerMessage.Contains("activity", StringComparison.Ordinal) ||
               lowerMessage.Contains("activities", StringComparison.Ordinal) ||
               lowerMessage.Contains("what can i do", StringComparison.Ordinal) ||
               lowerMessage.Contains("could i do", StringComparison.Ordinal) ||
               lowerMessage.Contains("what should i do", StringComparison.Ordinal) ||
                             lowerMessage.Contains("plan for the day", StringComparison.Ordinal) ||
                             lowerMessage.Contains("plan for today", StringComparison.Ordinal) ||
                             lowerMessage.Contains("plan my day", StringComparison.Ordinal) ||
                             lowerMessage.Contains("useful plan", StringComparison.Ordinal) ||
                             lowerMessage.Contains("day plan", StringComparison.Ordinal) ||
             lowerMessage.Contains("kinds of things", StringComparison.Ordinal) ||
               lowerMessage.Contains("kind of things", StringComparison.Ordinal) ||
               lowerMessage.Contains("things to do", StringComparison.Ordinal) ||
               lowerMessage.Contains("ideas", StringComparison.Ordinal) ||
               lowerMessage.Contains("recommend", StringComparison.Ordinal) ||
               lowerMessage.Contains("suggest", StringComparison.Ordinal);
    }

    public static string? ExtractLocationFromMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var match = LocationRegex().Match(message);
        if (!match.Success)
            return null;

        var location = match.Groups["location"].Value
            .Trim()
            .TrimEnd('?', '.', '!', ',');

        return string.IsNullOrWhiteSpace(location) ? null : location;
    }

    public static string NormalizeUnitPreference(string? value)
    {
        var lower = (value ?? "").Trim().ToLowerInvariant();
        return lower switch
        {
            "imperial" => "imperial",
            "metric" => "metric",
            _ => "auto"
        };
    }

    private static bool LooksLikeCoordinateLabel(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        return Regex.IsMatch(
            trimmed,
            @"^-?\d{1,3}(?:\.\d+)?\s*,\s*-?\d{1,3}(?:\.\d+)?$",
            RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"\b(?:in|for|at|near)\s+(?<location>[A-Za-z][A-Za-z0-9 .'-]{1,60}?)(?:\s+and\b|\s+to\b|[?.!,]|$)", RegexOptions.IgnoreCase)]
    internal static partial Regex LocationRegex();
}
