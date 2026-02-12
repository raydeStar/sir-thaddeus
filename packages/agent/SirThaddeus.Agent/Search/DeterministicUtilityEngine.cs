using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Search;

public enum DeterministicMatchConfidence
{
    None = 0,
    Medium = 1,
    High = 2
}

public sealed record DeterministicUtilityResult
{
    public required string Category { get; init; }
    public required string Answer { get; init; }
}

public sealed record DeterministicUtilityMatch
{
    public required DeterministicUtilityResult Result { get; init; }
    public required DeterministicMatchConfidence Confidence { get; init; }
}

/// <summary>
/// Pure deterministic parser/evaluator for simple utility skills.
/// No model calls, no I/O, no tool calls.
/// </summary>
public static class DeterministicUtilityEngine
{
    private static readonly Regex StrictConversionPattern = new(
        @"(?:convert\s+)?(?<value>-?\d+(?:\.\d+)?)\s*(?:°\s*)?(?<from>fahrenheit|celsius|kelvin|f|c|k|lbs?|pounds?|kg|kilograms?|oz|ounces?|grams?|g|miles?|mi|km|kilometers?|inches?|in|cm|centimeters?)\s+(?:to|in|into)\s*(?:°\s*)?(?<to>fahrenheit|celsius|kelvin|f|c|k|lbs?|pounds?|kg|kilograms?|oz|ounces?|grams?|g|miles?|mi|km|kilometers?|inches?|in|cm|centimeters?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WrapperTemperaturePattern = new(
        @"(?:if\s+i\s+set\s+it\s+to|set\s+it\s+to|set\s+to)\s*(?<value>-?\d+(?:\.\d+)?)\s*(?:°\s*)?(?<from>fahrenheit|celsius|kelvin|f|c|k)\b.*?\b(?:to|in|into)\s*(?:°\s*)?(?<to>fahrenheit|celsius|kelvin|f|c|k)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ValueUnitPattern = new(
        @"(?<value>-?\d+(?:\.\d+)?)\s*(?:°\s*)?(?<unit>fahrenheit|celsius|kelvin|f|c|k|lbs?|pounds?|kg|kilograms?|oz|ounces?|grams?|g|miles?|mi|km|kilometers?|inches?|in|cm|centimeters?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TargetUnitPattern = new(
        @"\b(?:to|in|into)\s*(?:°\s*)?(?<unit>fahrenheit|celsius|kelvin|f|c|k|lbs?|pounds?|kg|kilograms?|oz|ounces?|grams?|g|miles?|mi|km|kilometers?|inches?|in|cm|centimeters?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PercentOfPattern = new(
        @"(?:what(?:'s| is)\s+)?(?<pct>\d+(?:\.\d+)?)\s*%\s*(?:of)\s*(?<base>\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CalcPattern = new(
        @"^(?:what(?:'s| is)\s+|calculate\s+|compute\s+|solve\s+)?(?<expr>\d[\d\s\.\+\-\*\/\%\(\)]+\d)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HasNumberPattern = new(
        @"\d",
        RegexOptions.Compiled);

    private static readonly Regex UnitTokenPattern = new(
        @"\b(fahrenheit|celsius|kelvin|f|c|k|lbs?|pounds?|kg|kilograms?|oz|ounces?|grams?|g|miles?|mi|km|kilometers?|inches?|in|cm|centimeters?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (string From, string To, double Factor)[] LinearConversions =
    [
        ("lbs", "kg", 0.453592),
        ("kg", "lbs", 2.20462),
        ("oz", "grams", 28.3495),
        ("grams", "oz", 0.035274),
        ("miles", "km", 1.60934),
        ("km", "miles", 0.621371),
        ("inches", "cm", 2.54),
        ("cm", "inches", 0.393701),
    ];

    public static DeterministicUtilityMatch? TryMatch(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return null;

        var message = userMessage.Trim();

        var highResult = TryParseStrict(message);
        if (highResult is not null)
        {
            return new DeterministicUtilityMatch
            {
                Result = highResult,
                Confidence = DeterministicMatchConfidence.High
            };
        }

        if (!LooksLikeMediumConfidenceCandidate(message))
            return null;

        var mediumResult = TryParseConversational(message);
        if (mediumResult is null)
            return null;

        return new DeterministicUtilityMatch
        {
            Result = mediumResult,
            Confidence = DeterministicMatchConfidence.Medium
        };
    }

    private static DeterministicUtilityResult? TryParseStrict(string message)
    {
        return TryParsePercent(message)
            ?? TryParseArithmetic(message)
            ?? TryParseConversion(message, StrictConversionPattern);
    }

    private static DeterministicUtilityResult? TryParseConversational(string message)
    {
        var wrapperTemp = TryParseWrapperTemperature(message);
        if (wrapperTemp is not null)
            return wrapperTemp;

        var extractedConversion = TryParseValueAndTargetUnits(message);
        if (extractedConversion is not null)
            return extractedConversion;

        var normalized = StripConversationalWrappers(message);
        return TryParsePercent(normalized)
            ?? TryParseArithmetic(normalized);
    }

    private static bool LooksLikeMediumConfidenceCandidate(string message)
    {
        var lower = message.ToLowerInvariant();
        if (WrapperTemperaturePattern.IsMatch(message))
            return true;

        var hasConversationalCue =
            lower.Contains("if i set it to", StringComparison.Ordinal) ||
            lower.Contains("set it to", StringComparison.Ordinal) ||
            lower.Contains("what is that in", StringComparison.Ordinal) ||
            lower.Contains("what's that in", StringComparison.Ordinal) ||
            lower.Contains("convert", StringComparison.Ordinal) ||
            lower.Contains("calculate", StringComparison.Ordinal) ||
            lower.Contains("compute", StringComparison.Ordinal) ||
            lower.Contains("solve", StringComparison.Ordinal);

        if (!hasConversationalCue)
            return false;

        return HasNumberPattern.IsMatch(message) && UnitTokenPattern.IsMatch(message) ||
               lower.Contains("+", StringComparison.Ordinal) ||
               lower.Contains("-", StringComparison.Ordinal) ||
               lower.Contains("*", StringComparison.Ordinal) ||
               lower.Contains("/", StringComparison.Ordinal) ||
               lower.Contains("plus", StringComparison.Ordinal) ||
               lower.Contains("minus", StringComparison.Ordinal) ||
               lower.Contains("times", StringComparison.Ordinal) ||
               lower.Contains("divided by", StringComparison.Ordinal) ||
               lower.Contains("percent", StringComparison.Ordinal) ||
               lower.Contains("%", StringComparison.Ordinal);
    }

    private static DeterministicUtilityResult? TryParsePercent(string message)
    {
        var normalizedPercent = Regex.Replace(
            message,
            @"\bpercent\b",
            "%",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var match = PercentOfPattern.Match(normalizedPercent);
        if (!match.Success ||
            !double.TryParse(match.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) ||
            !double.TryParse(match.Groups["base"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var baseValue))
        {
            return null;
        }

        var result = baseValue * (pct / 100.0);
        return new DeterministicUtilityResult
        {
            Category = "calculator",
            Answer = $"{pct}% of {baseValue} = **{result:N2}**"
        };
    }

    private static DeterministicUtilityResult? TryParseArithmetic(string message)
    {
        var arithmetic = NormalizeArithmeticExpression(message);
        var calcMatch = CalcPattern.Match(arithmetic);
        if (!calcMatch.Success)
            return null;

        var expression = calcMatch.Groups["expr"].Value.Trim();
        if (!Regex.IsMatch(expression, @"^[\d\s\.\+\-\*\/\(\)]+$"))
            return null;

        try
        {
            var dt = new DataTable();
            var result = dt.Compute(expression, null);
            return new DeterministicUtilityResult
            {
                Category = "calculator",
                Answer = $"{expression} = **{result}**"
            };
        }
        catch
        {
            return null;
        }
    }

    private static DeterministicUtilityResult? TryParseConversion(string message, Regex pattern)
    {
        var match = pattern.Match(message);
        if (!match.Success ||
            !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var fromUnit = NormalizeUnit(match.Groups["from"].Value);
        var toUnit = NormalizeUnit(match.Groups["to"].Value);
        return TryBuildConversionResult(value, fromUnit, toUnit);
    }

    private static DeterministicUtilityResult? TryParseWrapperTemperature(string message)
    {
        var match = WrapperTemperaturePattern.Match(message);
        if (!match.Success ||
            !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var fromUnit = NormalizeUnit(match.Groups["from"].Value);
        var toUnit = NormalizeUnit(match.Groups["to"].Value);
        return TryBuildConversionResult(value, fromUnit, toUnit);
    }

    private static DeterministicUtilityResult? TryParseValueAndTargetUnits(string message)
    {
        var source = ValueUnitPattern.Match(message);
        if (!source.Success ||
            !double.TryParse(source.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var targetMatches = TargetUnitPattern.Matches(message);
        if (targetMatches.Count == 0)
            return null;

        var target = targetMatches[^1];
        var fromUnit = NormalizeUnit(source.Groups["unit"].Value);
        var toUnit = NormalizeUnit(target.Groups["unit"].Value);
        return TryBuildConversionResult(value, fromUnit, toUnit);
    }

    private static DeterministicUtilityResult? TryBuildConversionResult(
        double value,
        string fromUnit,
        string toUnit)
    {
        if (string.IsNullOrWhiteSpace(fromUnit) ||
            string.IsNullOrWhiteSpace(toUnit) ||
            string.Equals(fromUnit, toUnit, StringComparison.Ordinal))
        {
            return null;
        }

        var converted = TryConvert(value, fromUnit, toUnit);
        if (converted is null)
            return null;

        var isTemperature = IsTemperatureUnit(fromUnit) || IsTemperatureUnit(toUnit);
        var answer = isTemperature
            ? $"{FormatTemperature(value, fromUnit)} equals **{FormatTemperature(converted.Value, toUnit)}**."
            : $"{FormatLinearQuantity(value, fromUnit)} equals **{FormatLinearQuantity(converted.Value, toUnit)}**.";

        return new DeterministicUtilityResult
        {
            Category = "conversion",
            Answer = answer
        };
    }

    private static double? TryConvert(double value, string fromUnit, string toUnit)
    {
        if (fromUnit == toUnit)
            return value;

        // Temperature pairs for v1 (F↔C, C↔K).
        if (fromUnit == "fahrenheit" && toUnit == "celsius")
            return (value - 32.0) * 5.0 / 9.0;
        if (fromUnit == "celsius" && toUnit == "fahrenheit")
            return value * 9.0 / 5.0 + 32.0;
        if (fromUnit == "celsius" && toUnit == "kelvin")
            return value + 273.15;
        if (fromUnit == "kelvin" && toUnit == "celsius")
            return value - 273.15;

        foreach (var (from, to, factor) in LinearConversions)
        {
            if (from == fromUnit && to == toUnit)
                return value * factor;
        }

        return null;
    }

    private static bool IsTemperatureUnit(string unit) =>
        unit is "fahrenheit" or "celsius" or "kelvin";

    private static string NormalizeUnit(string rawUnit)
    {
        var unit = (rawUnit ?? "").Trim().ToLowerInvariant();
        return unit switch
        {
            "f" or "fahrenheit" => "fahrenheit",
            "c" or "celsius" => "celsius",
            "k" or "kelvin" => "kelvin",
            "lb" or "lbs" or "pound" or "pounds" => "lbs",
            "kg" or "kilogram" or "kilograms" => "kg",
            "oz" or "ounce" or "ounces" => "oz",
            "g" or "gram" or "grams" => "grams",
            "mi" or "mile" or "miles" => "miles",
            "km" or "kilometer" or "kilometers" => "km",
            "in" or "inch" or "inches" => "inches",
            "cm" or "centimeter" or "centimeters" => "cm",
            _ => unit
        };
    }

    private static string FormatTemperature(double value, string unit)
    {
        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return unit switch
        {
            "fahrenheit" => $"{rounded:0.0}°F",
            "celsius" => $"{rounded:0.0}°C",
            "kelvin" => $"{rounded:0.0}K",
            _ => $"{rounded:0.0} {unit}"
        };
    }

    private static string FormatLinearQuantity(double value, string unit)
    {
        var rounded = Math.Round(value, 4, MidpointRounding.AwayFromZero);
        var numberText = rounded.ToString("0.####", CultureInfo.InvariantCulture);
        return $"{numberText} {ToDisplayUnit(unit)}";
    }

    private static string ToDisplayUnit(string unit) => unit switch
    {
        "lbs" => "lb",
        "kg" => "kg",
        "oz" => "oz",
        "grams" => "g",
        "miles" => "mi",
        "km" => "km",
        "inches" => "in",
        "cm" => "cm",
        _ => unit
    };

    private static string NormalizeArithmeticExpression(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "";

        var expression = message.Trim().ToLowerInvariant();
        expression = Regex.Replace(
            expression,
            @"^(?:can you\s+|could you\s+|please\s+|hey[,!\s]+|hi[,!\s]+|well[,!\s]+)*",
            "",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        expression = Regex.Replace(
            expression,
            @"^(?:what(?:'s| is)\s+|calculate\s+|compute\s+|solve\s+)+",
            "",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        expression = Regex.Replace(
            expression,
            @"\s+(?:for me|please|thanks|thank you)\s*$",
            "",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        expression = expression.Replace(",", "");
        expression = Regex.Replace(expression, @"\bmultiplied\s+by\b", "*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        expression = Regex.Replace(expression, @"\bdivided\s+by\b", "/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        expression = Regex.Replace(expression, @"\bplus\b", "+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        expression = Regex.Replace(expression, @"\bminus\b", "-", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        expression = Regex.Replace(expression, @"\btimes\b", "*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        expression = Regex.Replace(expression, @"\bover\b", "/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        expression = Regex.Replace(expression, @"(?<=\d)\s*x\s*(?=\d)", " * ", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        expression = Regex.Replace(expression, @"\s+", " ", RegexOptions.Compiled).Trim();
        return expression;
    }

    private static string StripConversationalWrappers(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "";

        var cleaned = message.Trim();
        cleaned = Regex.Replace(
            cleaned,
            @"\b(?:if i set it to|set it to|what is that in|what's that in)\b",
            "",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        cleaned = Regex.Replace(cleaned, @"\s+", " ", RegexOptions.Compiled).Trim();
        return cleaned;
    }
}
