using System.Text.Json;
using SirThaddeus.Agent.Validation;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Validation;

public class ToolArgumentRepairTests
{
    private static ToolDefinition CalculatorDef() => new()
    {
        Function = new FunctionDefinition
        {
            Name = "calculator",
            Description = "Evaluate a math expression.",
            Parameters = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { expression = new { type = "string", description = "A math expression." } },
                required = new[] { "expression" },
            }),
        },
    };

    [Fact]
    public void Validate_raw_json_flags_missing_required_param()
    {
        // Model used the wrong parameter name ("query" instead of "expression").
        var result = ToolArgumentValidator.Validate("{\"query\":\"what is 2+2\"}", CalculatorDef());

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i =>
            i.Contains("expression", StringComparison.OrdinalIgnoreCase)
            && i.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_raw_json_passes_well_formed_args()
    {
        var result = ToolArgumentValidator.Validate("{\"expression\":\"comb(12,5)\"}", CalculatorDef());
        Assert.True(result.IsValid);
    }

    [Fact]
    public void BuildStructuredError_is_valid_json_naming_the_valid_params()
    {
        var repair = ToolArgumentRepair.BuildStructuredError(
            "calculator",
            CalculatorDef(),
            ["Required parameter 'expression' is missing", "Unknown parameter 'query' not in tool schema"]);

        using var doc = JsonDocument.Parse(repair); // must stay structured (require_structured_errors)
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("invalid_arguments", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retriable").GetBoolean());

        var message = error.GetProperty("message").GetString()!;
        Assert.Contains("calculator", message);
        Assert.Contains("expression", message); // names the valid parameter
        Assert.Contains("query", message);       // echoes the offending one
    }

    [Theory]
    [InlineData("Required parameter 'expression' is missing", true)]
    [InlineData("Required parameter 'x' is empty", true)]
    [InlineData("Invalid JSON arguments: unexpected token", true)]
    [InlineData("Arguments must be a JSON object", true)]
    [InlineData("Unknown parameter 'query' not in tool schema", false)]
    [InlineData("Parameter 'n' has type String but schema expects number", false)]
    public void IsFatalIssue_only_flags_definitely_broken_calls(string issue, bool expected)
        => Assert.Equal(expected, ToolArgumentRepair.IsFatalIssue(issue));
}
