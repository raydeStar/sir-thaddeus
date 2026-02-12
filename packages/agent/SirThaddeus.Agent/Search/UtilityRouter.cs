using System.Data;
using System.Globalization;
using System.Text.Json;
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
        public required string Category { get; init; }  // "weather", "time", "holiday", "feed", "status", "calculator", "conversion", "fact"
        public required string Answer   { get; init; }  // Inline answer text
        public string? McpToolName      { get; init; }  // If non-null, route to this MCP tool instead
        public string? McpToolArgs      { get; init; }  // JSON args for the MCP tool
        public string? ContextKey       { get; init; }  // Optional follow-up context marker
    }

    // ── Weather patterns ─────────────────────────────────────────────
    // Must include a location component to avoid false positives.
    private static readonly Regex WeatherPattern = new(
        @"(?:what(?:'s| is)\s+the\s+)?(?:weather|forecast|temperature|temp)(?:\s+(?:is|like))?\s+(?:in|for|at|near)\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Handles chatty forms like:
    // "can you tell me what the weather is in rexburg, id?"
    private static readonly Regex WeatherLoosePattern = new(
        @"\b(?:weather|forecast|temperature|temp)\b.*?\b(?:in|for|at|near)\b\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WillItRainPattern = new(
        @"(?:will it|is it going to)\s+(?:rain|snow|storm|be (?:hot|cold|warm|sunny|cloudy))\s+(?:in|at|near)\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Time zone patterns ───────────────────────────────────────────
    private static readonly Regex TimeInPattern = new(
        @"(?:what(?:'s| is)\s+(?:the\s+)?time(?:\s+is\s+it)?|what\s+time\s+is\s+it|time)\s+(?:in|at|for)\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimeZonePattern = new(
        @"time\s*zone\s+(?:for|of|in)\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Holiday patterns ─────────────────────────────────────────────
    private static readonly Regex HolidayTodayPattern = new(
        @"(?:is\s+today\s+(?:a\s+)?(?:public\s+)?holiday)\s+(?:in|for)\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HolidayNextPattern = new(
        @"(?:next|upcoming)\s+(?:public\s+)?holiday\s+(?:in|for)\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HolidayListPattern = new(
        @"(?:public\s+holidays?|holidays?)\s+(?:in|for)\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex YearPattern = new(
        @"\b(19\d{2}|20\d{2})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Feed / status patterns ───────────────────────────────────────
    private static readonly Regex FeedIntentPattern = new(
        @"\b(?:rss|atom|feed)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StatusIntentPattern = new(
        @"\b(?:is|check|status|uptime)\b.+\b(?:up|online|reachable|down)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UrlLikePattern = new(
        @"((?:https?://)?(?:[a-z0-9](?:[a-z0-9\-]{0,61}[a-z0-9])?\.)+[a-z]{2,}(?:/[^\s\]\[\(\)""']*)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Calculator patterns ──────────────────────────────────────────
    // Matches "what is 15% of 230", "calculate 5 * 12", basic arithmetic.
    private static readonly Regex CalcPattern = new(
        @"^(?:what(?:'s| is)\s+|calculate\s+)?(\d[\d\s\.\+\-\*\/\%\(\)]+\d)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PercentOfPattern = new(
        @"(?:what(?:'s| is)\s+)?(\d+(?:\.\d+)?)\s*%\s*(?:of)\s*(\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CalculatorLeadInPattern = new(
        @"\b(?:what(?:'s| is)|calculate|compute|solve)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Conversion patterns ──────────────────────────────────────────
    private static readonly Regex ConvertPattern = new(
        @"(?:convert\s+)?(\d+(?:\.\d+)?)\s*(miles?|km|kilometers?|feet|ft|meters?|m|inches?|in|cm|centimeters?|lbs?|pounds?|kg|kilograms?|oz|ounces?|grams?|g|liters?|l|gallons?|gal|fahrenheit|celsius|f|c)\s+(?:to|in|into)\s+(\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RecipeBakeTemperaturePattern = new(
        @"\b(?:recipe\s+says\s+)?(?:bake|preheat)\s+(?:at\s+)?(?<temp>\d{2,4}(?:\.\d+)?)\s*(?:°\s*)?(?<unit>fahrenheit|celsius|f|c)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HowManyPattern = new(
        @"how many\s+(\w+)\s+(?:in|per)\s+(?:a |an )?(\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LetterCountPattern = new(
        @"(?:how many|count)\s+([a-zA-Z])(?:['’]s|s)?\s+(?:are\s+)?in\s+(?:the\s+word\s+)?[""']?([a-zA-Z]+)[""']?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MoonDistancePattern = new(
        @"(?:how\s+far\s+(?:is|to)\s+(?:the\s+)?moon|distance\s+(?:to|from)\s+(?:the\s+)?moon|how\s+many\s+\w+\s+(?:is|are)\s+(?:it\s+)?(?:from\s+(?:the\s+)?earth\s+)?(?:to\s+)?(?:the\s+)?moon|earth\s+to\s+moon)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SpeedOfLightPattern = new(
        @"(?:what(?:'s| is)\s+)?(?:the\s+)?speed of light(?:\s+in\s+(?:vacuum|space))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BoilingPointWaterPattern = new(
        @"(?:what(?:'s| is)\s+)?(?:the\s+)?boiling point of water(?:\s+(?:at|in)\s+sea\s+level)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FreezingPointWaterPattern = new(
        @"(?:what(?:'s| is)\s+)?(?:the\s+)?freezing point of water(?:\s+(?:at|in)\s+sea\s+level)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DaysInYearPattern = new(
        @"(?:how many|number of)\s+days\s+(?:are\s+)?(?:in|per)\s+(?:a|one)\s+year|how many\s+days\s+in\s+(?:a|one)\s+year",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Strip tail temporal markers that should influence forecast day,
    // not geocoder place matching (e.g. "Rexburg today" -> "Rexburg").
    private static readonly Regex LocationTemporalTailPattern = new(
        @"\s+(?:for\s+)?(?:today|tomorrow|tonight|now|right now|currently|this\s+(?:morning|afternoon|evening|week|weekend)|next\s+week)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TemporalOnlyLocationPattern = new(
        @"^(?:for\s+)?(?:today|tomorrow|tonight|now|right now|currently|this\s+(?:morning|afternoon|evening|week|weekend)|next\s+week)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const double AvgEarthMoonDistanceKm = 384_400;

    // ── Anti-patterns (things that look like utility but aren't) ─────
    private static readonly string[] WeatherFalsePositives =
    [
        "political climate", "business climate", "economic climate",
        "climate change", "climate crisis", "investment climate",
        "social climate", "weather the storm", "weather this"
    ];

    private static readonly Dictionary<string, string> CountryCodeMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["us"] = "US",
        ["usa"] = "US",
        ["united states"] = "US",
        ["united states of america"] = "US",
        ["america"] = "US",
        ["canada"] = "CA",
        ["ca"] = "CA",
        ["uk"] = "GB",
        ["great britain"] = "GB",
        ["united kingdom"] = "GB",
        ["england"] = "GB",
        ["japan"] = "JP",
        ["jp"] = "JP",
        ["france"] = "FR",
        ["fr"] = "FR",
        ["germany"] = "DE",
        ["de"] = "DE",
        ["spain"] = "ES",
        ["es"] = "ES",
        ["italy"] = "IT",
        ["it"] = "IT",
        ["mexico"] = "MX",
        ["mx"] = "MX",
        ["australia"] = "AU",
        ["au"] = "AU",
        ["new zealand"] = "NZ",
        ["nz"] = "NZ",
        ["india"] = "IN",
        ["in"] = "IN",
        ["china"] = "CN",
        ["cn"] = "CN",
        ["brazil"] = "BR",
        ["br"] = "BR"
    };

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
            ?? TryHoliday(trimmed)
            ?? TryStatus(trimmed)
            ?? TryFeed(trimmed)
            ?? TryLetterCount(trimmed)
            ?? TrySimpleFact(trimmed)
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
            match = WeatherLoosePattern.Match(message);
        if (!match.Success)
            return null;

        var location = NormalizeLocation(match.Groups[1].Value);
        if (string.IsNullOrWhiteSpace(location) || location.Length < 2)
            return null;

        // Route to coordinate-first weather tools:
        //   weather_geocode(place) -> weather_forecast(lat,lon)
        // The orchestrator executes the second step using geocode output.
        return new UtilityResult
        {
            Category    = "weather",
            Answer      = $"[weather lookup for: {location}]",
            McpToolName = "weather_geocode",
            McpToolArgs = System.Text.Json.JsonSerializer.Serialize(new
            {
                place      = location,
                maxResults = 3
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

        var location = NormalizeLocationCandidate(match.Groups[1].Value);
        if (string.IsNullOrWhiteSpace(location) || location.Length < 2)
            return null;

        return new UtilityResult
        {
            Category = "time",
            Answer = $"[time lookup for: {location}]",
            McpToolName = "weather_geocode",
            McpToolArgs = JsonSerializer.Serialize(new
            {
                place = location,
                maxResults = 3
            })
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Holidays
    // ─────────────────────────────────────────────────────────────────

    private static UtilityResult? TryHoliday(string message)
    {
        var todayMatch = HolidayTodayPattern.Match(message);
        if (todayMatch.Success)
        {
            var scope = NormalizeHolidayScope(todayMatch.Groups[1].Value);
            if (!TryParseCountryAndRegion(scope, out var countryCode, out var regionCode))
                return null;

            return new UtilityResult
            {
                Category = "holiday",
                Answer = $"[holiday lookup for: {countryCode}]",
                McpToolName = "holidays_is_today",
                McpToolArgs = JsonSerializer.Serialize(new
                {
                    countryCode,
                    regionCode
                })
            };
        }

        var nextMatch = HolidayNextPattern.Match(message);
        if (nextMatch.Success)
        {
            var scope = NormalizeHolidayScope(nextMatch.Groups[1].Value);
            if (!TryParseCountryAndRegion(scope, out var countryCode, out var regionCode))
                return null;

            return new UtilityResult
            {
                Category = "holiday",
                Answer = $"[next holiday lookup for: {countryCode}]",
                McpToolName = "holidays_next",
                McpToolArgs = JsonSerializer.Serialize(new
                {
                    countryCode,
                    regionCode,
                    maxItems = 5
                })
            };
        }

        var listMatch = HolidayListPattern.Match(message);
        if (!listMatch.Success)
            return null;

        var rawScope = listMatch.Groups[1].Value;
        var year = ResolveHolidayYear(rawScope);
        var scopeWithoutYear = StripHolidayYearHints(rawScope);
        if (!TryParseCountryAndRegion(scopeWithoutYear, out var listCountryCode, out var listRegionCode))
            return null;

        return new UtilityResult
        {
            Category = "holiday",
            Answer = $"[holiday list lookup for: {listCountryCode}]",
            McpToolName = "holidays_get",
            McpToolArgs = JsonSerializer.Serialize(new
            {
                countryCode = listCountryCode,
                regionCode = listRegionCode,
                year,
                maxItems = 25
            })
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Status / Reachability
    // ─────────────────────────────────────────────────────────────────

    private static UtilityResult? TryStatus(string message)
    {
        if (!StatusIntentPattern.IsMatch(message) &&
            !message.Contains(" is ", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains(" up", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!TryExtractUrlLike(message, out var url))
            return null;

        return new UtilityResult
        {
            Category = "status",
            Answer = $"[status check for: {url}]",
            McpToolName = "status_check_url",
            McpToolArgs = JsonSerializer.Serialize(new
            {
                url
            })
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // RSS / Atom
    // ─────────────────────────────────────────────────────────────────

    private static UtilityResult? TryFeed(string message)
    {
        if (!TryExtractUrlLike(message, out var url))
            return null;

        var lower = message.ToLowerInvariant();
        var urlLooksFeedy =
            url.Contains("/feed", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("rss", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("atom", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

        if (!FeedIntentPattern.IsMatch(message) && !urlLooksFeedy)
            return null;

        return new UtilityResult
        {
            Category = "feed",
            Answer = $"[feed fetch for: {url}]",
            McpToolName = "feed_fetch",
            McpToolArgs = JsonSerializer.Serialize(new
            {
                url,
                maxItems = 5
            })
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Calculator
    // ─────────────────────────────────────────────────────────────────

    private static UtilityResult? TryCalculator(string message)
    {
        var normalized = NormalizeCalculatorMessage(message).TrimEnd('?', '!', '.');
        var normalizedPercent = Regex.Replace(
            normalized,
            @"\bpercent\b",
            "%",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Percentage: "what's 15% of 230"
        var pctMatch = PercentOfPattern.Match(normalizedPercent);
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

        // Basic arithmetic: "5 * 12", "100 + 50 - 20",
        // and word operators like "what is 6 plus 7".
        var arithmetic = NormalizeArithmeticExpressionWords(normalized);
        var calcMatch = CalcPattern.Match(arithmetic);
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

    private static string NormalizeArithmeticExpressionWords(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var expr = value.Trim().ToLowerInvariant();
        expr = Regex.Replace(
            expr,
            @"^(?:what(?:'s| is)\s+|calculate\s+|compute\s+|solve\s+)+",
            "",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        expr = expr.Replace(",", "");

        expr = Regex.Replace(expr, @"\bmultiplied\s+by\b", "*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        expr = Regex.Replace(expr, @"\bdivided\s+by\b", "/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        expr = Regex.Replace(expr, @"\bplus\b", "+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        expr = Regex.Replace(expr, @"\bminus\b", "-", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        expr = Regex.Replace(expr, @"\btimes\b", "*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        expr = Regex.Replace(expr, @"\bover\b", "/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // "6 x 7" shorthand for multiplication.
        expr = Regex.Replace(expr, @"(?<=\d)\s*x\s*(?=\d)", " * ", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        expr = Regex.Replace(expr, @"\s+", " ", RegexOptions.Compiled).Trim();
        return expr;
    }

    private static string NormalizeCalculatorMessage(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = value.Trim();

        // Chatty inputs like "Hey, Thaddeus, what's 6x7?"
        // should route exactly like "what's 6x7?".
        var cue = CalculatorLeadInPattern.Match(normalized);
        if (cue.Success && cue.Index > 0)
            normalized = normalized[cue.Index..];

        return normalized.Trim();
    }

    private static UtilityResult? TrySimpleFact(string message)
    {
        var lower = message.ToLowerInvariant();

        if (MoonDistancePattern.IsMatch(lower))
        {
            var (value, unitLabel) = ResolveMoonDistanceUnit(lower);
            var formatted = FormatMoonDistance(value, unitLabel);

            return new UtilityResult
            {
                Category = "fact",
                Answer = $"The average Earth-Moon distance is about **{formatted}**.",
                ContextKey = "moon_distance"
            };
        }

        if (SpeedOfLightPattern.IsMatch(lower))
        {
            return new UtilityResult
            {
                Category = "fact",
                Answer = "The speed of light in vacuum is **299,792,458 meters per second** (about **299,792 km/s**)."
            };
        }

        if (BoilingPointWaterPattern.IsMatch(lower))
        {
            return new UtilityResult
            {
                Category = "fact",
                Answer = "At sea level, water boils at **100C** (**212F**)."
            };
        }

        if (FreezingPointWaterPattern.IsMatch(lower))
        {
            return new UtilityResult
            {
                Category = "fact",
                Answer = "At sea level, water freezes at **0C** (**32F**)."
            };
        }

        if (DaysInYearPattern.IsMatch(lower))
        {
            if (lower.Contains("mars", StringComparison.Ordinal) ||
                lower.Contains("martian", StringComparison.Ordinal))
            {
                return null;
            }

            return new UtilityResult
            {
                Category = "fact",
                Answer = "A standard year has **365 days**; leap years have **366**."
            };
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────
    // Unit Conversion
    // ─────────────────────────────────────────────────────────────────

    private static UtilityResult? TryConversion(string message)
    {
        if (TryResolveRecipeTemperatureConversion(message, out var recipeTemperatureResult))
            return recipeTemperatureResult;

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
                    Answer   = $"{FormatQuantity(value, fromUnit)} equals **{FormatQuantity(converted.Value, toUnit)}**."
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
                    Answer   = $"{FormatQuantity(1.0, bigUnit)} equals **{FormatQuantity(converted.Value, smallUnit)}**."
                };
            }
        }

        return null;
    }

    private static bool TryResolveRecipeTemperatureConversion(
        string message,
        out UtilityResult result)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var lower = message.ToLowerInvariant();
        var hasRecipeCue =
            lower.Contains("recipe", StringComparison.Ordinal) ||
            lower.Contains("bake", StringComparison.Ordinal) ||
            lower.Contains("preheat", StringComparison.Ordinal) ||
            lower.Contains("oven", StringComparison.Ordinal);
        if (!hasRecipeCue)
            return false;

        var match = RecipeBakeTemperaturePattern.Match(message);
        if (!match.Success ||
            !double.TryParse(match.Groups["temp"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sourceValue))
        {
            return false;
        }

        if (!TryInferRecipeTemperatureUnits(
                lower,
                sourceValue,
                match.Groups["unit"].Value,
                out var fromUnit,
                out var toUnit))
        {
            return false;
        }

        var converted = TryConvert(sourceValue, fromUnit, toUnit);
        if (converted is null)
            return false;

        var rounded = Math.Round(converted.Value, 0, MidpointRounding.AwayFromZero);
        var toDisplay = TemperatureUnitDisplay(toUnit);
        var fromDisplay = TemperatureUnitDisplay(fromUnit);

        result = new UtilityResult
        {
            Category = "conversion",
            Answer =
                $"Set the oven to about **{rounded:0} {toDisplay}** " +
                $"(exact: {converted.Value:0.##} {toDisplay} from {sourceValue:0.##} {fromDisplay})."
        };
        return true;
    }

    private static bool TryInferRecipeTemperatureUnits(
        string lowerMessage,
        double sourceValue,
        string explicitUnitToken,
        out string fromUnit,
        out string toUnit)
    {
        fromUnit = "";
        toUnit = "";

        var wantsCelsius =
            lowerMessage.Contains("set to celsius", StringComparison.Ordinal) ||
            lowerMessage.Contains("set in celsius", StringComparison.Ordinal) ||
            lowerMessage.Contains("to celsius", StringComparison.Ordinal) ||
            lowerMessage.Contains("in celsius", StringComparison.Ordinal) ||
            lowerMessage.Contains("oven is set to celsius", StringComparison.Ordinal) ||
            lowerMessage.Contains("europe", StringComparison.Ordinal);
        var wantsFahrenheit =
            lowerMessage.Contains("set to fahrenheit", StringComparison.Ordinal) ||
            lowerMessage.Contains("set in fahrenheit", StringComparison.Ordinal) ||
            lowerMessage.Contains("to fahrenheit", StringComparison.Ordinal) ||
            lowerMessage.Contains("in fahrenheit", StringComparison.Ordinal) ||
            lowerMessage.Contains("oven is set to fahrenheit", StringComparison.Ordinal) ||
            lowerMessage.Contains("united states", StringComparison.Ordinal) ||
            lowerMessage.Contains("usa", StringComparison.Ordinal);

        if (wantsCelsius == wantsFahrenheit)
            return false;

        toUnit = wantsCelsius ? "celsius" : "fahrenheit";

        if (TryNormalizeTemperatureUnit(explicitUnitToken, out var normalizedExplicit))
        {
            fromUnit = normalizedExplicit;
            if (string.Equals(fromUnit, toUnit, StringComparison.Ordinal))
                return false;
            return true;
        }

        // If no explicit source unit is present, infer from the target mode.
        // Recipe prompts that mention switching oven scales are almost always
        // asking for cross-scale conversion.
        fromUnit = toUnit == "celsius" ? "fahrenheit" : "celsius";

        // Guard against obviously incompatible values.
        if (fromUnit == "celsius" && sourceValue > 350)
            return false;
        if (fromUnit == "fahrenheit" && sourceValue < 90)
            return false;

        return true;
    }

    private static bool TryNormalizeTemperatureUnit(string raw, out string normalized)
    {
        normalized = "";
        var token = (raw ?? "").Trim().ToLowerInvariant();
        if (token.Length == 0)
            return false;

        if (token is "c" or "celsius")
        {
            normalized = "celsius";
            return true;
        }

        if (token is "f" or "fahrenheit")
        {
            normalized = "fahrenheit";
            return true;
        }

        return false;
    }

    private static string TemperatureUnitDisplay(string normalizedUnit) =>
        string.Equals(normalizedUnit, "fahrenheit", StringComparison.OrdinalIgnoreCase) ? "F" : "C";

    private static string FormatQuantity(double value, string normalizedUnit)
    {
        return $"{FormatConversionNumber(value)} {ToDisplayUnit(normalizedUnit, value)}";
    }

    private static string FormatConversionNumber(double value)
    {
        var rounded = Math.Round(value);
        if (Math.Abs(value - rounded) < 0.0000001)
            return rounded.ToString("N0", CultureInfo.InvariantCulture);

        return value.ToString("N4", CultureInfo.InvariantCulture)
            .TrimEnd('0')
            .TrimEnd('.');
    }

    private static string ToDisplayUnit(string normalizedUnit, double value)
    {
        var singular = Math.Abs(value - 1.0) < 0.0000001;
        return normalizedUnit switch
        {
            "miles"   => singular ? "mile"   : "miles",
            "feet"    => singular ? "foot"   : "feet",
            "meters"  => singular ? "meter"  : "meters",
            "inches"  => singular ? "inch"   : "inches",
            "lbs"     => singular ? "lb"     : "lbs",
            "grams"   => singular ? "gram"   : "grams",
            "liters"  => singular ? "liter"  : "liters",
            "gallons" => singular ? "gallon" : "gallons",
            _ => normalizedUnit
        };
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
        [("miles", "feet")]   = 5280.0,      [("feet", "miles")]   = 1.0 / 5280.0,
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

    private static (double Value, string UnitLabel) ResolveMoonDistanceUnit(string lowerMessage)
    {
        if (lowerMessage.Contains("meter", StringComparison.Ordinal))
            return (AvgEarthMoonDistanceKm * 1_000.0, "meters");
        if (lowerMessage.Contains("mile", StringComparison.Ordinal))
            return (AvgEarthMoonDistanceKm * 0.621371, "miles");

        // Default to kilometers when no explicit unit is requested.
        return (AvgEarthMoonDistanceKm, "kilometers");
    }

    private static string FormatMoonDistance(double value, string unitLabel)
    {
        var decimals = unitLabel == "kilometers" ? 0 : 0;
        return $"{Math.Round(value, decimals):N0} {unitLabel}";
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

        location = LocationTemporalTailPattern.Replace(location, "");
        if (TemporalOnlyLocationPattern.IsMatch(location.Trim()))
            return "";

        return location.Trim().TrimEnd('?', '.', '!', ',');
    }

    private static string NormalizeLocationCandidate(string value)
    {
        var location = (value ?? "").Trim().TrimEnd('?', '.', '!', ',');
        location = Regex.Replace(
            location,
            @"\s+(?:right now|currently|please|pls|thanks|thank you)\s*$",
            "",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        return location.Trim().TrimEnd('?', '.', '!', ',');
    }

    private static string NormalizeHolidayScope(string value)
    {
        return (value ?? "")
            .Trim()
            .TrimEnd('?', '.', '!', ',')
            .Replace("the ", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string StripHolidayYearHints(string value)
    {
        var stripped = YearPattern.Replace(value ?? "", "");
        stripped = Regex.Replace(
            stripped,
            @"\b(?:this|next)\s+year\b",
            "",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        return NormalizeHolidayScope(stripped);
    }

    private static int ResolveHolidayYear(string value)
    {
        var nowYear = DateTime.UtcNow.Year;
        if (value.Contains("next year", StringComparison.OrdinalIgnoreCase))
            return nowYear + 1;

        var yearMatch = YearPattern.Match(value ?? "");
        if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out var parsedYear))
            return Math.Clamp(parsedYear, 1900, 2100);

        return nowYear;
    }

    private static bool TryParseCountryAndRegion(
        string scope,
        out string countryCode,
        out string? regionCode)
    {
        countryCode = "";
        regionCode = null;

        var cleaned = NormalizeHolidayScope(scope);
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;

        // Explicit region token, e.g. US-ID
        var explicitRegion = cleaned.ToUpperInvariant();
        if (Regex.IsMatch(explicitRegion, @"^[A-Z]{2}-[A-Z0-9]{2,3}$"))
        {
            countryCode = explicitRegion[..2];
            regionCode = explicitRegion;
            return true;
        }

        // "Idaho, US" -> country=US, region=US-ID (best effort when 2-letter region)
        var parts = cleaned.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2 &&
            TryResolveCountryCode(parts[^1], out var parsedCountry))
        {
            countryCode = parsedCountry;
            var regionCandidate = parts[0].Trim().ToUpperInvariant();
            if (Regex.IsMatch(regionCandidate, @"^[A-Z]{2}$"))
                regionCode = $"{countryCode}-{regionCandidate}";
            return true;
        }

        if (TryResolveCountryCode(cleaned, out var directCountry))
        {
            countryCode = directCountry;
            return true;
        }

        // Last-token fallback: "holidays in canada please"
        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length > 0 && TryResolveCountryCode(tokens[^1], out var lastCountry))
        {
            countryCode = lastCountry;
            return true;
        }

        return false;
    }

    private static bool TryResolveCountryCode(string raw, out string countryCode)
    {
        countryCode = "";
        var cleaned = (raw ?? "").Trim().TrimEnd('?', '.', '!', ',');
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;

        var upper = cleaned.ToUpperInvariant();
        if (upper.Length == 2 && upper.All(char.IsLetter))
        {
            countryCode = upper;
            return true;
        }

        if (CountryCodeMap.TryGetValue(cleaned.ToLowerInvariant(), out var mapped))
        {
            countryCode = mapped;
            return true;
        }

        return false;
    }

    private static bool TryExtractUrlLike(string message, out string normalizedUrl)
    {
        normalizedUrl = "";
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var match = UrlLikePattern.Match(message);
        if (!match.Success)
            return false;

        var raw = match.Groups[1].Value
            .Trim()
            .TrimEnd('?', '.', '!', ',', ';', ':', ')', ']', '}');

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = $"https://{raw}";
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        normalizedUrl = uri.ToString();
        return true;
    }
}
