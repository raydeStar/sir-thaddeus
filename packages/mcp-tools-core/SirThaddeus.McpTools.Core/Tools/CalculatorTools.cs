using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using NCalc;
using NCalc.Handlers;

namespace SirThaddeus.McpServer.Tools;

// ─────────────────────────────────────────────────────────────────────────
// Calculator Tool
//
// Evaluates a mathematical expression deterministically on the CPU. The model
// decides WHAT to compute and writes the expression; this tool does the
// arithmetic exactly. The point is offloading: a small model should never do
// multi-digit or multi-step math in its head — it should set up the formula
// and let the machine evaluate it. No I/O, no side effects, no permission.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// MCP tool that evaluates math expressions (arithmetic, powers, roots,
/// factorial, combinations/permutations, gcd/lcm, logs, constants) and returns
/// the exact numeric result.
/// </summary>
[McpServerToolType]
public static class CalculatorTools
{
    private const int MaxExpressionLength = 500;

    [McpServerTool, Description(
        "Evaluate a math expression and return the exact result. ALWAYS use this " +
        "instead of doing multi-digit or multi-step arithmetic yourself. " +
        "Operators: + - * / % and ^ for powers. Functions: sqrt, cbrt, abs, " +
        "round, floor, ceil, min, max, log, ln, log10, pow(a,b), factorial(n) " +
        "or n!, comb(n,k) for combinations, perm(n,k) for permutations, " +
        "gcd(a,b), lcm(a,b). Constants: pi, e. " +
        "Examples: \"comb(12,5)\" returns 792; \"2^10 % 7\" returns 2; " +
        "\"22 + 19 - 8\" returns 33; \"sqrt(8^2 + 15^2)\" returns 17. " +
        "Returns JSON {\"expression\":\"...\",\"result\":\"...\"} or {\"error\":\"...\"}.")]
    public static string Calculator(
        [Description("The math expression to evaluate, e.g. \"comb(12,5)\" or \"2^10 % 7\".")]
        string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return ErrorJson("Expression is empty.");

        var trimmed = expression.Trim();
        if (trimmed.Length > MaxExpressionLength)
            return ErrorJson($"Expression is too long (max {MaxExpressionLength} characters).");

        try
        {
            var normalized = RewritePowerOperator(RewriteFactorial(trimmed));
            var expr = new Expression(normalized, ExpressionOptions.IgnoreCaseAtBuiltInFunctions);
            expr.Parameters["Pi"] = Math.PI;
            expr.Parameters["Tau"] = Math.PI * 2;
            expr.Parameters["E"] = Math.E;
            expr.EvaluateFunction += EvaluateExtraFunctions;

            var value = expr.Evaluate();
            if (expr.Error is not null)
                return ErrorJson($"Could not evaluate expression: {expr.Error}");

            var formatted = FormatNumericAnswer(value);
            if (formatted is null)
                return ErrorJson("Result is not a finite number (check for overflow or invalid arguments).");

            return JsonSerializer.Serialize(new { expression = trimmed, result = formatted });
        }
        catch (Exception ex)
        {
            return ErrorJson($"Could not evaluate expression: {ex.Message}");
        }
    }

    // n! → Factorial(n) (NCalc has no factorial operator).
    private static string RewriteFactorial(string input) =>
        Regex.Replace(input, @"(?<val>\d+(?:\.\d+)?|\([^()]+\))\s*!", "Factorial(${val})");

    // a^b → Pow(a, b): NCalc binds ^ as bitwise XOR, so rewrite for the
    // scientific-calculator meaning the model intends. Repeated application
    // resolves nested powers like 2^(3^2); capped so it cannot loop.
    private static string RewritePowerOperator(string input)
    {
        var pattern = new Regex(
            @"(?<base>\d+(?:\.\d+)?|\([^()]+\)|[A-Za-z_][A-Za-z0-9_]*(?:\([^()]*\))?)" +
            @"\s*\^\s*" +
            @"(?<exp>\d+(?:\.\d+)?|\([^()]+\)|[A-Za-z_][A-Za-z0-9_]*(?:\([^()]*\))?)",
            RegexOptions.None);

        for (var i = 0; i < 8; i++)
        {
            var replaced = pattern.Replace(input, "Pow(${base}, ${exp})");
            if (replaced == input)
                break;
            input = replaced;
        }

        return input;
    }

    // Functions NCalc doesn't provide out of the box. Built-ins (sqrt, abs,
    // round, floor, min, max, pow, log10, …) are handled by NCalc itself.
    private static void EvaluateExtraFunctions(string name, FunctionArgs args)
    {
        switch (name.ToLowerInvariant())
        {
            case "factorial":
                if (TryArg(args, 0, out var fv) && IsIntegral(fv) && fv is >= 0 and <= 170)
                {
                    double f = 1;
                    for (var i = 2; i <= (int)fv; i++) f *= i;
                    args.Result = f;
                }
                break;
            case "comb":
            case "choose":
            case "ncr":
                if (TryArg(args, 0, out var cn) && TryArg(args, 1, out var ck))
                    args.Result = Combinations(cn, ck);
                break;
            case "perm":
            case "npr":
                if (TryArg(args, 0, out var pn) && TryArg(args, 1, out var pk))
                    args.Result = Permutations(pn, pk);
                break;
            case "gcd":
                if (TryArg(args, 0, out var ga) && TryArg(args, 1, out var gb) && IsIntegral(ga) && IsIntegral(gb))
                    args.Result = (double)Gcd((long)Math.Round(ga), (long)Math.Round(gb));
                break;
            case "lcm":
                if (TryArg(args, 0, out var la) && TryArg(args, 1, out var lb) && IsIntegral(la) && IsIntegral(lb))
                {
                    long a = (long)Math.Round(la), b = (long)Math.Round(lb);
                    var g = Gcd(a, b);
                    args.Result = g == 0 ? 0d : (double)Math.Abs(a / g * b);
                }
                break;
            case "cbrt":
                if (TryArg(args, 0, out var cbv)) args.Result = Math.Cbrt(cbv);
                break;
            case "ln":
                if (TryArg(args, 0, out var lnv)) args.Result = Math.Log(lnv);
                break;
            case "log":
                if (args.Parameters.Length == 1 && TryArg(args, 0, out var lg)) args.Result = Math.Log10(lg);
                break;
            case "ceil":
                if (TryArg(args, 0, out var ce)) args.Result = Math.Ceiling(ce);
                break;
        }
    }

    private static double Combinations(double nd, double kd)
    {
        if (!IsIntegral(nd) || !IsIntegral(kd))
            return double.NaN;
        long n = (long)Math.Round(nd), k = (long)Math.Round(kd);
        if (n < 0 || k < 0 || k > n)
            return double.NaN;
        k = Math.Min(k, n - k);
        double result = 1;
        for (long i = 1; i <= k; i++)
            result = result * (n - k + i) / i;
        return Math.Round(result);
    }

    private static double Permutations(double nd, double kd)
    {
        if (!IsIntegral(nd) || !IsIntegral(kd))
            return double.NaN;
        long n = (long)Math.Round(nd), k = (long)Math.Round(kd);
        if (n < 0 || k < 0 || k > n)
            return double.NaN;
        double result = 1;
        for (long i = n - k + 1; i <= n; i++)
            result *= i;
        return Math.Round(result);
    }

    private static long Gcd(long a, long b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }

    private static bool TryArg(FunctionArgs args, int index, out double value)
    {
        value = 0;
        if (args.Parameters.Length <= index)
            return false;
        var raw = args.Parameters[index].Evaluate();
        return raw is not null &&
               double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsIntegral(double value) =>
        Math.Abs(value - Math.Round(value)) < 1e-9;

    private static string? FormatNumericAnswer(object? value)
    {
        if (value is null)
            return null;

        double number;
        switch (value)
        {
            case double d: number = d; break;
            case float f: number = f; break;
            case decimal dec: number = (double)dec; break;
            case int i: number = i; break;
            case long l: number = l; break;
            case bool b: return b ? "true" : "false";
            default:
                if (!double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    return null;
                break;
        }

        if (double.IsNaN(number) || double.IsInfinity(number))
            return null;

        if (Math.Abs(number - Math.Round(number)) < 1e-9 && Math.Abs(number) < 1e15)
            return ((long)Math.Round(number)).ToString(CultureInfo.InvariantCulture);

        return number.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    private static string ErrorJson(string message) =>
        JsonSerializer.Serialize(new { error = message });
}
