using System.Text.Json;
using SirThaddeus.McpServer.Tools;

namespace SirThaddeus.Tests.MCP;

public class CalculatorToolsTests
{
    [Theory]
    [InlineData("comb(12,5)", "792")]
    [InlineData("22 + 19 - 8", "33")]
    [InlineData("2^10 % 7", "2")]
    [InlineData("sqrt(8^2 + 15^2)", "17")]
    [InlineData("perm(5,2)", "20")]
    [InlineData("gcd(48, 36)", "12")]
    [InlineData("lcm(4, 6)", "12")]
    [InlineData("factorial(5)", "120")]
    [InlineData("5!", "120")]
    [InlineData("sum(6, 12, 18, 24, 30, 36, 42, 48)", "216")]
    [InlineData("pow(2, 10)", "1024")]
    [InlineData("(9 + 4) * 3", "39")]
    [InlineData("abs(-7)", "7")]
    public void Calculator_evaluates_expressions(string expression, string expected)
    {
        var json = CalculatorTools.Calculator(expression);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("error", out _), $"unexpected error for '{expression}': {json}");
        Assert.Equal(expected, doc.RootElement.GetProperty("result").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("2 +")]
    [InlineData("comb(3, 5)")] // k > n is undefined → error, not a wrong number
    public void Calculator_returns_error_on_invalid_input(string expression)
    {
        var json = CalculatorTools.Calculator(expression);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _), $"expected error for '{expression}': {json}");
    }

    [Fact]
    public void Calculator_error_guides_model_to_retry_with_arithmetic_expression()
    {
        var json = CalculatorTools.Calculator("sum of all positive multiples of 6 less than 50");
        using var doc = JsonDocument.Parse(json);

        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.Contains("pure arithmetic expression", error);
        // The example must stay disjoint from benchmark fixtures (the probe item
        // is multiples of 6 below 50) so guidance never doubles as an answer key.
        Assert.Contains("4+8+12+16+20+24+28", error);
        Assert.DoesNotContain("6+12+18+24+30+36+42+48", error);
    }
}
