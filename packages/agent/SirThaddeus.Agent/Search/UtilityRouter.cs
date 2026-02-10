using System.Data;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Search;

// ─────────────────────────────────────────────────────────────────────────
// Utility Router — Bypass Lane for Non-Search Queries
//
// Intercepts utility intents BEFORE SearchOrchestrator. Weather, time,
// calculator, and unit conversion queries are handled inline without
// touching the web search pipeline.
//
// Design constraint: HIGH PRECISION. False positives are worse than
// false negatives. "What's the weather like in the political climate?"
// must NOT trigger the weather handler. When in doubt, return null
// and let the search pipeline handle it.
// ─────────────────────────────────────────────────────────────────────────

public static class UtilityRouter
{
    /// <summary>
    /// Result of a utility match. If null, the message is not a utility
    /// query and should proceed to SearchOrchestrator.
    /// </summary>
    public sealed record UtilityResult
    {
        public required string Category { get; init; }  // "weather", "time", "calculator", "conversion"
        public required string Answer   { get; init; }  // Inline answer text
        public string? McpToolName      { get; init; }  // If non-null, route to this MCP tool instead
        public string? McpToolArgs      { get; init; }  // JSON args for the MCP tool
    }

    // ── Weather patterns ─────────────────────────────────────────────
    // Must include a location component to avoid false positives.
    private static readonly Regex WeatherPattern = new(
        @"(?:what(?:'s| is)\s+the\s+)?(?:weather|forecast|temperature|temp)(?:\s+like)?\s+(?:in|for|at|near)\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WillItRainPattern = new(
        @"(?:will it|is it going to)\s+(?:rain|snow|storm|be (?:hot|cold|warm|sunny|cloudy))\s+(?:in|at|near)\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Time zone patterns ───────────────────────────────────────────
    private static readonly Regex TimeInPattern = new(
        @"(?:what(?:'s| is) the )?time\s+(?:in|at|for)\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimeZonePattern = new(
        @"time\s*zone\s+(?:for|of|in)\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Calculator patterns ──────────────────────────────────────────
    // Matches "what is 15% of 230", "calculate 5 * 12", basic arithmetic.
    private static readonly Regex CalcPattern = new(
        @"^(?:what(?:'s| is)\s+)?(\d[\d\s\.\+\-\*\/\%\(\)]+\d)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PercentOfPattern = new(
        @"(?:what(?:'s| is)\s+)?(\d+(?:\.\d+)?)\s*%\s*(?:of)\s*(\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Conversion patterns ──────────────────────────────────────────
    private static readonly Regex ConvertPattern = new(
        @"(?:convert\s+)?(\d+(?:\.\d+)?)\s*(miles?|km|kilometers?|feet|ft|meters?|m|inches?|in|cm|centimeters?|lbs?|pounds?|kg|kilograms?|oz|ounces?|grams?|g|liters?|l|gallons?|gal|fahrenheit|celsius|f|c)\s+(?:to|in|into)\s+(\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HowManyPattern = new(
        @"how many\s+(\w+)\s+(?:in|per)\s+(?:a |an )?(\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LetterCountPattern = new(
        @"(?:how many|count)\s+([a-zA-Z])(?:['’]s|s)?\s+(?:are\s+)?in\s+(?:the\s+word\s+)?[""']?([a-zA-Z]+)[""']?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Anti-patterns (things that look like utility but aren't) ─────
    private static readonly string[] WeatherFalsePositives =
    [
        "political climate", "business climate", "economic climate",
        "climate change", "climate crisis", "investment climate",
        "social climate", "weather the storm", "weather this"
    ];

    /// <summary>
    /// Attempts to handle the message as a utility query. Returns null
    /// if the message is not a utility intent.
    /// </summary>
    public static UtilityResult? TryHandle(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return null;

        var trimmed = userMessage.Trim();

        return TryWeather(trimmed)
            ?? TryTime(trimmed)
            ?? TryLetterCount(trimmed)
            ?? TryCalculator(trimmed)
            ?? TryConversion(trimmed);
    }

    // ─────────────────────────────────────────────────────────────────
    // Weather
    // ─────────────────────────────────────────────────────────────────

    private static UtilityResult? TryWeather(string message)
    {
        var lower = message.ToLowerInvariant();

        // Reject false positives first
        foreach (var fp in WeatherFalsePositives)
        {
            if (lower.Contains(fp, StringComparison.Ordinal))
                return null;
        }

        var match = WeatherPattern.Match(message);
        if (!match.Success)
            match = WillItRainPattern.Match(message);
        if (!match.Success)
            return null;

        var location = NormalizeLocation(match.Groups[1].Value);
        if (string.IsNullOrWhiteSpace(location) || location.Length < 2)
            return null;

        // Route to a web search with a constrained weather query.
        // If a dedicated weather MCP tool exists in the future, use
        // McpToolName/McpToolArgs instead.
        return new UtilityResult
        {
            Category    = "weather",
            Answer      = $"[weather lookup for: {location}]",
            McpToolName = "web_search",
            McpToolArgs = System.Text.Json.JsonSerializer.Serialize(new
            {
                query      = $"{location} weather forecast today",
                maxResults = 3,
                recency    = "day"
            })
        };
    }

    private static UtilityResult? TryLetterCount(string message)
    {
        var match = LetterCountPattern.Match(message);
        if (!match.Success)
            return null;

        var rawLetter = match.Groups[1].Value;
        var rawWord = match.Groups[2].Value;
        if (string.IsNullOrWhiteSpace(rawLetter) || string.IsNullOrWhiteSpace(rawWord))
            return null;

        var letter = rawLetter[0];
        var word = rawWord.Trim();
        var count = CountLetterOccurrences(word, letter);

        return new UtilityResult
        {
            Category = "text",
            Answer = $"The word \"{word}\" contains **{count}** '{char.ToLowerInvariant(letter)}' characters."
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Time / Time Zones
    // ─────────────────────────────────────────────────────────────────

    private static UtilityResult? TryTime(string message)
    {
        var match = TimeInPattern.Match(message);
        if (!match.Success)
            match = TimeZonePattern.Match(message);
        if (!match.Success)
            return null;

        var location = match.Groups[1].Value.Trim().TrimEnd('?', '.', '!');
        if (string.IsNullOrWhiteSpace(location) || location.Length < 2)
            return null;

        // Try to resolve the timezone from well-known city mappings.
        var tz = TryResolveTimeZone(location);
        if (tz is null)
        {
            return new UtilityResult
            {
                Category = "time",
                Answer   = $"I don't have a timezone mapping for \"{location}\" — " +
                           "let me look that up.",
                McpToolName = "web_search",
                McpToolArgs = System.Text.Json.JsonSerializer.Serialize(new
                {
                    query      = $"current time in {location}",
                    maxResults = 2,
                    recency    = "any"
                })
            };
        }

        var now = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow, tz);
        var formatted = now.ToString("h:mm tt on dddd, MMMM d");

        return new UtilityResult
        {
            Category = "time",
            Answer   = $"It's currently **{formatted}** in {location} ({tz.StandardName})."
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Calculator
    // ─────────────────────────────────────────────────────────────────

    private static UtilityResult? TryCalculator(string message)
    {
        // Percentage: "what's 15% of 230"
        var pctMatch = PercentOfPattern.Match(message);
        if (pctMatch.Success &&
            double.TryParse(pctMatch.Groups[1].Value, out var pct) &&
            double.TryParse(pctMatch.Groups[2].Value, out var baseVal))
        {
            var result = baseVal * (pct / 100.0);
            return new UtilityResult
            {
                Category = "calculator",
                Answer   = $"{pct}% of {baseVal} = **{result:N2}**"
            };
        }

        // Basic arithmetic: "5 * 12", "100 + 50 - 20"
        var calcMatch = CalcPattern.Match(message.Trim());
        if (!calcMatch.Success)
            return null;

        var expr = calcMatch.Groups[1].Value.Trim();
        // Security: only allow digits, operators, spaces, parens, decimal
        if (!Regex.IsMatch(expr, @"^[\d\s\.\+\-\*\/\(\)]+$"))
            return null;

        try
        {
            var dt = new DataTable();
            var result = dt.Compute(expr, null);
            return new UtilityResult
            {
                Category = "calculator",
                Answer   = $"{expr} = **{result}**"
            };
        }
        catch
        {
            return null; // Fall through to search pipeline
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Unit Conversion
    // ─────────────────────────────────────────────────────────────────

    private static UtilityResult? TryConversion(string message)
    {
        var match = ConvertPattern.Match(message);
        if (match.Success &&
            double.TryParse(match.Groups[1].Value, out var value))
        {
            var fromUnit = NormalizeUnit(match.Groups[2].Value);
            var toUnit   = NormalizeUnit(match.Groups[3].Value);

            var converted = TryConvert(value, fromUnit, toUnit);
            if (converted is not null)
            {
                return new UtilityResult
                {
                    Category = "conversion",
                    Answer   = $"{value} {fromUnit} = **{converted:N4} {toUnit}**"
                };
            }
        }

        // "How many X in a Y" — common conversion questions
        var hmMatch = HowManyPattern.Match(message);
        if (hmMatch.Success)
        {
            var smallUnit = NormalizeUnit(hmMatch.Groups[1].Value);
            var bigUnit   = NormalizeUnit(hmMatch.Groups[2].Value);

            var converted = TryConvert(1.0, bigUnit, smallUnit);
            if (converted is not null)
            {
                return new UtilityResult
                {
                    Category = "conversion",
                    Answer   = $"There are **{converted:N4} {smallUnit}** in 1 {bigUnit}."
                };
            }
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────
    // Conversion Tables
    // ─────────────────────────────────────────────────────────────────

    private static readonly Dictionary<(string From, string To), double> ConversionFactors = new()
    {
        // Distance
        [("miles", "km")]     = 1.60934,     [("km", "miles")]     = 0.621371,
        [("feet", "meters")]  = 0.3048,      [("meters", "feet")]  = 3.28084,
        [("inches", "cm")]    = 2.54,        [("cm", "inches")]    = 0.393701,
        [("miles", "meters")] = 1609.34,     [("meters", "miles")] = 0.000621371,
        [("feet", "inches")]  = 12.0,        [("inches", "feet")]  = 1.0 / 12.0,
        [("km", "meters")]    = 1000.0,      [("meters", "km")]    = 0.001,

        // Weight
        [("lbs", "kg")]       = 0.453592,    [("kg", "lbs")]       = 2.20462,
        [("oz", "grams")]     = 28.3495,     [("grams", "oz")]     = 0.035274,
        [("lbs", "oz")]       = 16.0,        [("oz", "lbs")]       = 0.0625,
        [("kg", "grams")]     = 1000.0,      [("grams", "kg")]     = 0.001,

        // Volume
        [("liters", "gallons")] = 0.264172,  [("gallons", "liters")] = 3.78541,

        // Temperature handled separately (not a simple factor)
    };

    private static double? TryConvert(double value, string from, string to)
    {
        if (from == to)
            return value;

        // Temperature
        if (from == "fahrenheit" && to == "celsius")
            return (value - 32) * 5.0 / 9.0;
        if (from == "celsius" && to == "fahrenheit")
            return value * 9.0 / 5.0 + 32;

        if (ConversionFactors.TryGetValue((from, to), out var factor))
            return value * factor;

        return null;
    }

    private static string NormalizeUnit(string unit)
    {
        var lower = unit.Trim().ToLowerInvariant();

        // Match known units BEFORE depluralization to avoid mangling
        // words like "celsius" / "fahrenheit" whose trailing 's' is
        // part of the word, not a plural suffix.
        return lower switch
        {
            "fahrenheit" => "fahrenheit",
            "celsius"    => "celsius",
            "mile" or "miles"       => "miles",
            "kilometer" or "kilometers" or "km" => "km",
            "meter" or "meters" or "m" => "meters",
            "foot" or "feet" or "ft" => "feet",
            "inch" or "inches" or "in" => "inches",
            "centimeter" or "centimeters" or "cm" => "cm",
            "lb" or "lbs" or "pound" or "pounds" => "lbs",
            "kilogram" or "kilograms" or "kg" => "kg",
            "ounce" or "ounces" or "oz" => "oz",
            "gram" or "grams" or "g" => "grams",
            "liter" or "liters" or "l" => "liters",
            "gallon" or "gallons" or "gal" => "gallons",
            "f"          => "fahrenheit",
            "c"          => "celsius",
            _            => lower.TrimEnd('s') // Safe fallback depluralize
        };
    }

    private static int CountLetterOccurrences(string word, char letter)
    {
        var normalizedLetter = char.ToLowerInvariant(letter);
        var count = 0;
        foreach (var ch in word)
        {
            if (char.ToLowerInvariant(ch) == normalizedLetter)
                count++;
        }
        return count;
    }

    private static string NormalizeLocation(string value)
    {
        var location = (value ?? "").Trim();
        location = location.TrimEnd('?', '.', '!', ',');

        // Strip conversational tails that hurt search precision.
        location = Regex.Replace(
            location,
            @"\s+(?:for me|please|pls|thanks|thank you)\s*$",
            "",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        return location.Trim().TrimEnd('?', '.', '!', ',');
    }

    // ─────────────────────────────────────────────────────────────────
    // Time Zone Mapping (common cities/regions)
    //
    // Not exhaustive — if the city isn't here, we fall back to a web
    // search. This avoids importing a full geo-lookup library.
    // ─────────────────────────────────────────────────────────────────

    private static TimeZoneInfo? TryResolveTimeZone(string location)
    {
        var lower = location.ToLowerInvariant();

        var id = lower switch
        {
            "new york" or "nyc" or "boston" or "miami" or "atlanta"
                or "washington dc" or "dc" or "philadelphia"
                => "Eastern Standard Time",

            "chicago" or "dallas" or "houston" or "austin"
                or "minneapolis" or "nashville" or "st louis"
                => "Central Standard Time",

            "denver" or "phoenix" or "salt lake city"
                or "boise" or "albuquerque"
                => "Mountain Standard Time",

            "los angeles" or "la" or "san francisco" or "seattle"
                or "portland" or "san diego" or "las vegas"
                => "Pacific Standard Time",

            "anchorage" or "alaska"
                => "Alaskan Standard Time",

            "honolulu" or "hawaii"
                => "Hawaiian Standard Time",

            "london" or "uk" or "united kingdom"
                => "GMT Standard Time",

            "paris" or "berlin" or "rome" or "madrid"
                or "amsterdam" or "brussels" or "vienna"
                => "W. Europe Standard Time",

            "moscow" or "russia"
                => "Russian Standard Time",

            "tokyo" or "japan"
                => "Tokyo Standard Time",

            "sydney" or "melbourne" or "australia"
                => "AUS Eastern Standard Time",

            "auckland" or "new zealand"
                => "New Zealand Standard Time",

            "beijing" or "shanghai" or "china"
                => "China Standard Time",

            "mumbai" or "delhi" or "india"
                => "India Standard Time",

            "dubai" or "abu dhabi" or "uae"
                => "Arabian Standard Time",

            "rexburg" or "idaho falls" or "pocatello" or "idaho"
                => "Mountain Standard Time",

            _ => null
        };

        if (id is null)
            return null;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch
        {
            return null;
        }
    }
}
