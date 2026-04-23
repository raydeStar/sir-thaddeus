using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using NCalc;
using NCalc.Handlers;

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
        // Note: time-of-day queries intentionally fall through to the LLM
        // + `time_now` MCP tool. A deterministic "system clock" fast-path
        // would bypass the tool and break smoke suites that validate
        // routing (e.g. smoke_time_now asserts `time_now` gets called).
        // Date is safe because the system prompt already carries today's
        // date in its preamble, so the LLM answers deterministically too.
        return TryParseDateQuestion(message)
            ?? ClassicReasoningEngine.TryMatch(message)
            ?? TryParsePercent(message)
            ?? TryParseArithmetic(message)
            ?? TryParseAdvancedMath(message)
            ?? TryParseConversion(message, StrictConversionPattern);
    }

    // Broader expressions that the simple arithmetic path can't handle —
    // powers (^), roots (sqrt), trig (sin/cos/tan, with "degrees" wrapper),
    // logs (log, ln), constants (pi, e), factorial (!), etc. Only fires
    // when the message contains at least one advanced token so routine
    // "2+2" still uses the faster DataTable path. Delegates evaluation to
    // NCalc so we don't reimplement a math parser.
    private static readonly Regex AdvancedMathDetector = new(
        @"\b(sqrt|cbrt|sin|cos|tan|asin|acos|atan|sinh|cosh|tanh|log|log10|ln|exp|abs|floor|ceil|ceiling|round|min|max|pow|factorial|pi|tau|squared|cubed|square\s+root|cube\s+root)\b|\^|!",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AdvancedEntryPattern = new(
        @"^\s*(?:hey[,!\s]+|hi[,!\s]+|please[,!\s]+|)*" +
        @"(?:what(?:'s|\s+is)\s+|calculate\s+|compute\s+|solve\s+|evaluate\s+|eval\s+)?" +
        @"(?<expr>.+?)" +
        @"\s*\??\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static DeterministicUtilityResult? TryParseAdvancedMath(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;
        if (!AdvancedMathDetector.IsMatch(message))
            return null;

        var entry = AdvancedEntryPattern.Match(message);
        if (!entry.Success)
            return null;

        // Strip sentence-terminators but NOT trailing '!', since '!' is the
        // factorial operator (e.g. "what is 5!?" should keep the '!' and
        // drop only the '?').
        var raw = entry.Groups["expr"].Value.Trim().TrimEnd('?', '.', ' ');
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = NormalizeAdvancedExpression(raw);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        try
        {
            var expr = new Expression(normalized, ExpressionOptions.IgnoreCaseAtBuiltInFunctions);
            expr.Parameters["Pi"] = Math.PI;
            expr.Parameters["Tau"] = Math.PI * 2;
            expr.Parameters["E"] = Math.E;
            expr.EvaluateFunction += EvaluateExtraFunctions;

            var value = expr.Evaluate();
            if (expr.Error is not null)
                return null;

            var formatted = FormatNumericAnswer(value);
            if (formatted is null)
                return null;

            return new DeterministicUtilityResult
            {
                Category = "calculator",
                Answer = $"{raw.TrimEnd()} = **{formatted}**"
            };
        }
        catch
        {
            return null;
        }
    }

    // NCalc treats `^` as XOR, speaks "Log" as Log(value, base), and doesn't
    // know about "squared" / "cubed" / "degrees" / factorial out of the box.
    // Pre-translate these so the resulting expression evaluates the way a
    // user would expect from scientific-calculator shorthand.
    private static string NormalizeAdvancedExpression(string raw)
    {
        var expression = raw;

        // "X squared" / "X cubed"
        expression = Regex.Replace(expression,
            @"\b(?<base>\d+(?:\.\d+)?|\([^()]+\))\s+squared\b",
            "Pow(${base}, 2)",
            RegexOptions.IgnoreCase);
        expression = Regex.Replace(expression,
            @"\b(?<base>\d+(?:\.\d+)?|\([^()]+\))\s+cubed\b",
            "Pow(${base}, 3)",
            RegexOptions.IgnoreCase);

        // "square root of X" → Sqrt(X)
        expression = Regex.Replace(expression,
            @"\bsquare\s+root\s+of\s+(?<x>\d+(?:\.\d+)?|\([^()]+\))",
            "Sqrt(${x})",
            RegexOptions.IgnoreCase);
        expression = Regex.Replace(expression,
            @"\bcube\s+root\s+of\s+(?<x>\d+(?:\.\d+)?|\([^()]+\))",
            "Cbrt(${x})",
            RegexOptions.IgnoreCase);

        // Constants: "pi"/"tau"/"e" as standalone tokens → Pi/Tau/E parameters
        expression = Regex.Replace(expression, @"\bpi\b", "Pi", RegexOptions.IgnoreCase);
        expression = Regex.Replace(expression, @"\btau\b", "Tau", RegexOptions.IgnoreCase);
        // Only rewrite "e" when surrounded by operators/parens/EOL, not
        // when it's the start of a longer word. The trailing class
        // includes ')' so 'ln(e)' correctly rewrites to 'ln(E)'.
        expression = Regex.Replace(
            expression,
            @"(?<![A-Za-z])e(?=\s*[+\-*/%^),]|\s*$)",
            "E");

        // NCalc's `Log(x)` requires two args (value, base), so rewrite:
        //   ln(x)  → Log(x, E)   (natural log)
        //   log(x) → Log10(x)    (single-arg "log" is base-10 by convention)
        // `log10(...)` and `log(x, base)` are already NCalc-native.
        expression = Regex.Replace(
            expression,
            @"\bln\s*\(\s*(?<arg>[^()]+(?:\([^()]*\)[^()]*)*)\)",
            "Log(${arg}, E)",
            RegexOptions.IgnoreCase);
        expression = Regex.Replace(
            expression,
            @"(?<!\d|log)\blog\s*\(\s*(?<arg>[^(),]+?)\s*\)",
            "Log10(${arg})",
            RegexOptions.IgnoreCase);

        // Degree wrappers inside trig: sin(30 deg) / sin(30°) / sin(30 degrees)
        expression = Regex.Replace(expression,
            @"(?<fn>sin|cos|tan)\s*\(\s*(?<val>-?\d+(?:\.\d+)?)\s*(?:°|deg(?:ree(?:s)?)?)\s*\)",
            m => $"{m.Groups["fn"].Value}(({m.Groups["val"].Value}) * Pi / 180)",
            RegexOptions.IgnoreCase);

        // Factorial: 5! → Factorial(5)
        expression = Regex.Replace(expression,
            @"(?<val>\d+(?:\.\d+)?|\([^()]+\))\s*!",
            "Factorial(${val})");

        // Power operator: a^b → Pow(a,b) — keep this AFTER factorial so the
        // negated-lookbehind doesn't trip. NCalc binds ^ as XOR, so
        // substitution is required for scientific-calculator intuition.
        expression = RewritePowerOperator(expression);

        // Clean whitespace
        expression = Regex.Replace(expression, @"\s+", " ").Trim();
        return expression;
    }

    // Replaces every `a^b` with `Pow(a, b)`. Handles simple operand shapes —
    // numbers, parenthesized sub-expressions, and bare identifiers. Nested
    // expressions like `2^(3^4)` are handled by repeated application.
    private static string RewritePowerOperator(string input)
    {
        var pattern = new Regex(
            @"(?<base>\d+(?:\.\d+)?|\([^()]+\)|[A-Za-z_][A-Za-z0-9_]*(?:\([^()]*\))?)" +
            @"\s*\^\s*" +
            @"(?<exp>\d+(?:\.\d+)?|\([^()]+\)|[A-Za-z_][A-Za-z0-9_]*(?:\([^()]*\))?)",
            RegexOptions.Compiled);

        for (var i = 0; i < 8; i++) // cap iterations so a pathological input can't loop
        {
            var replaced = pattern.Replace(input, "Pow(${base}, ${exp})");
            if (ReferenceEquals(replaced, input) || replaced == input)
                break;
            input = replaced;
        }
        return input;
    }

    private static void EvaluateExtraFunctions(string name, FunctionArgs args)
    {
        // NCalc picks up the first handler that sets args.Result; no need to
        // toggle HasResult explicitly (the setter is init-only in 5.x).
        switch (name.ToLowerInvariant())
        {
            case "factorial":
                if (args.Parameters.Length == 1)
                {
                    var raw = args.Parameters[0].Evaluate();
                    if (raw is not null && double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    {
                        if (n < 0 || n > 170 || Math.Abs(n - Math.Round(n)) > 1e-9)
                            return;
                        double result = 1;
                        for (var i = 2; i <= (int)n; i++) result *= i;
                        args.Result = result;
                    }
                }
                break;
            case "cbrt":
                if (args.Parameters.Length == 1)
                {
                    var raw = args.Parameters[0].Evaluate();
                    if (raw is not null && double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                        args.Result = Math.Cbrt(x);
                }
                break;
        }
    }

    private static string? FormatNumericAnswer(object? value)
    {
        if (value is null) return null;
        double number;
        switch (value)
        {
            case double d: number = d; break;
            case float f: number = f; break;
            case decimal dec: number = (double)dec; break;
            case int i: number = i; break;
            case long l: number = l; break;
            default:
                if (!double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    return null;
                break;
        }
        if (double.IsNaN(number) || double.IsInfinity(number))
            return null;

        // Integer-valued results rendered without a decimal point.
        if (Math.Abs(number - Math.Round(number)) < 1e-9 && Math.Abs(number) < 1e15)
            return ((long)Math.Round(number)).ToString(CultureInfo.InvariantCulture);

        // Keep up to 6 significant digits for readability.
        return number.ToString("0.######", CultureInfo.InvariantCulture);
    }

    // "What's today's date?" / "What day is it?" etc. Local LLMs habitually
    // hallucinate a year from their training cutoff; every time we can short-
    // circuit to DateTimeOffset.Now we avoid the wrong-year bug. Kept tight —
    // compound prompts ("... and tell me the weather") fall through to the LLM.
    private static readonly Regex DateQuestionPattern = new(
        @"^\s*(?:hey[,!\s]+|hi[,!\s]+|please[,!\s]+)*" +
        @"(?:what(?:'s|\s+is)|tell me)\s+" +
        @"(?:today'?s\s+date|the\s+date(?:\s+today)?|the\s+(?:current|today's?)\s+date|today'?s\s+day|" +
        @"the\s+day(?:\s+of\s+the\s+week)?(?:\s+today)?|the\s+current\s+day|day\s+is\s+it|" +
        @"day\s+of\s+the\s+week(?:\s+is\s+it)?)\s*\??\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WhatDayPattern = new(
        @"^\s*(?:hey[,!\s]+|hi[,!\s]+|please[,!\s]+)*what\s+day\s+(?:is\s+it|of\s+the\s+week\s+is\s+it)\s*\??\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static DeterministicUtilityResult? TryParseDateQuestion(string message)
    {
        if (!DateQuestionPattern.IsMatch(message) && !WhatDayPattern.IsMatch(message))
            return null;

        var now = DateTimeOffset.Now;
        return new DeterministicUtilityResult
        {
            Category = "date",
            Answer = $"Today is **{now:dddd, MMMM d, yyyy}** ({now:yyyy-MM-dd})."
        };
    }

    // "What time is it?" / "Current time" / "What's the time right now?" —
    // mirrors the legacy UtilityRouter.LocalTimeNowPattern so the pipeline
    // short-circuits before the LLM picks a wrong tool (e.g. `web_search`).
    // Explicitly scoped to "here/now" — queries like "time in Paris" fall
    // through to the LLM + timezone tool. Anchored at the start but lets
    // trailing compounds pass ("... tell me in one sentence") since those
    // are clarifications, not different questions.
    private static readonly Regex TimeQuestionPattern = new(
        @"^\s*(?:hey[,!\s]+|hi[,!\s]+|please[,!\s]+)*" +
        @"(?:" +
            @"what(?:'s|\s+is)\s+(?:the\s+)?(?:current\s+)?(?:local\s+)?time(?:\s+right\s+now|\s+now)?|" +
            @"what\s+time\s+is\s+it(?:\s+right\s+now|\s+now)?|" +
            @"tell\s+me\s+(?:the\s+)?(?:current\s+)?time|" +
            @"(?:the\s+)?current\s+(?:local\s+)?time" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static DeterministicUtilityResult? TryParseTimeQuestion(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        if (!TimeQuestionPattern.IsMatch(message))
            return null;

        // Reject location-scoped time queries — those need a timezone tool
        // (e.g. "what time is it in Paris", "time at GMT"). Only the pure
        // "what time is it locally" intent is safe to answer from the
        // system clock. We strip trailing punctuation before checking so
        // "... in Tokyo?" is caught the same as "... in Tokyo".
        var lower = message.ToLowerInvariant();
        var stripped = Regex.Replace(lower, @"[?.!\s]+$", "");
        if (Regex.IsMatch(stripped, @"\b(?:in|at|for)\s+(?:the\s+)?[a-z][\w\s]{0,40}$") &&
            !Regex.IsMatch(stripped, @"\b(?:in|at|for)\s+(?:one\s+sentence|short|brief|plain\s+english|detail|detail(s)?)\s*$"))
            return null;

        var now = DateTimeOffset.Now;
        return new DeterministicUtilityResult
        {
            Category = "time",
            Answer = $"It's **{now:h:mm tt}** local ({now:dddd, MMMM d, yyyy})."
        };
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
        // Drop trailing punctuation so CalcPattern's anchored "ends-in-digit"
        // rule matches prompts like "What is 347 * 29?" — the "?" is the
        // difference between firing the deterministic fast-path and letting
        // a small model guess (and get the arithmetic wrong).
        expression = expression.TrimEnd('?', '.', '!', ' ');
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
