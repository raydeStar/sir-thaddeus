using System.Text.Json;
using SirThaddeus.Agent.Orchestration;
using SirThaddeus.Agent.Validation;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Continuity;

public sealed class ToolArgumentValidatorTests
{
    // ── Helpers ──────────────────────────────────────────────────────

    private static ToolDefinition MakeTool(string name, object? parameters = null) => new()
    {
        Function = new FunctionDefinition
        {
            Name = name,
            Description = $"Test tool {name}",
            Parameters = parameters ?? new { type = "object" }
        }
    };

    private static ToolDefinition MakeToolWithSchema(string name, object properties, string[]? required = null) => new()
    {
        Function = new FunctionDefinition
        {
            Name = name,
            Description = $"Test tool {name}",
            Parameters = new
            {
                type = "object",
                properties,
                required = required ?? Array.Empty<string>()
            }
        }
    };

    private static ProposedToolCall Call(string name, string argsJson) =>
        new(name, argsJson, $"call_{name}");

    // ── Basic validation ─────────────────────────────────────────────

    [Fact]
    public void ValidArgs_PassesValidation()
    {
        var tool = MakeToolWithSchema("web_search",
            new { query = new { type = "string" } },
            ["query"]);

        var result = ToolArgumentValidator.Validate(
            Call("web_search", """{"query": "Portland bakeries"}"""),
            tool);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void InvalidJson_FailsValidation()
    {
        var tool = MakeTool("web_search");
        var result = ToolArgumentValidator.Validate(
            Call("web_search", "not json"),
            tool);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("Invalid JSON"));
    }

    [Fact]
    public void NonObjectArgs_FailsValidation()
    {
        var tool = MakeTool("web_search");
        var result = ToolArgumentValidator.Validate(
            Call("web_search", """["array"]"""),
            tool);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("JSON object"));
    }

    // ── Required parameters ──────────────────────────────────────────

    [Fact]
    public void MissingRequiredParam_FailsValidation()
    {
        var tool = MakeToolWithSchema("web_search",
            new { query = new { type = "string" } },
            ["query"]);

        var result = ToolArgumentValidator.Validate(
            Call("web_search", """{}"""),
            tool);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("'query'") && i.Contains("missing"));
    }

    [Fact]
    public void EmptyRequiredStringParam_FailsValidation()
    {
        var tool = MakeToolWithSchema("web_search",
            new { query = new { type = "string" } },
            ["query"]);

        var result = ToolArgumentValidator.Validate(
            Call("web_search", """{"query": ""}"""),
            tool);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("'query'") && i.Contains("empty"));
    }

    [Fact]
    public void NullRequiredParam_FailsValidation()
    {
        var tool = MakeToolWithSchema("web_search",
            new { query = new { type = "string" } },
            ["query"]);

        var result = ToolArgumentValidator.Validate(
            Call("web_search", """{"query": null}"""),
            tool);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void OptionalParamMissing_PassesValidation()
    {
        var tool = MakeToolWithSchema("web_search",
            new
            {
                query = new { type = "string" },
                limit = new { type = "integer" }
            },
            ["query"]);

        var result = ToolArgumentValidator.Validate(
            Call("web_search", """{"query": "test"}"""),
            tool);

        Assert.True(result.IsValid);
    }

    // ── Type checking ────────────────────────────────────────────────

    [Fact]
    public void WrongType_StringExpectedGotNumber_FailsValidation()
    {
        var tool = MakeToolWithSchema("web_search",
            new { query = new { type = "string" } },
            ["query"]);

        var result = ToolArgumentValidator.Validate(
            Call("web_search", """{"query": 42}"""),
            tool);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("type"));
    }

    [Fact]
    public void CorrectType_Number_Passes()
    {
        var tool = MakeToolWithSchema("test",
            new { count = new { type = "integer" } },
            ["count"]);

        var result = ToolArgumentValidator.Validate(
            Call("test", """{"count": 5}"""),
            tool);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void CorrectType_Boolean_Passes()
    {
        var tool = MakeToolWithSchema("test",
            new { verbose = new { type = "boolean" } },
            ["verbose"]);

        var result = ToolArgumentValidator.Validate(
            Call("test", """{"verbose": true}"""),
            tool);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void UnionTypeSchema_AcceptsMatchingType_WithoutThrowing()
    {
        var tool = MakeToolWithSchema("weather_geocode",
            new { location = new { type = new[] { "string", "null" } } },
            ["location"]);

        var result = ToolArgumentValidator.Validate(
            Call("weather_geocode", """{"location": "Olympia, WA"}"""),
            tool);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void UnionTypeSchema_ReportsMismatch_AgainstAllAllowedTypes()
    {
        var tool = MakeToolWithSchema("weather_geocode",
            new { location = new { type = new[] { "string", "null" } } },
            ["location"]);

        var result = ToolArgumentValidator.Validate(
            Call("weather_geocode", """{"location": ["Olympia, WA"]}"""),
            tool);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Contains("string or null", StringComparison.Ordinal));
    }

    // ── Unknown parameters ───────────────────────────────────────────

    [Fact]
    public void UnknownParam_ReportsIssue()
    {
        var tool = MakeToolWithSchema("web_search",
            new { query = new { type = "string" } },
            ["query"]);

        var result = ToolArgumentValidator.Validate(
            Call("web_search", """{"query": "test", "extra_field": "hello"}"""),
            tool);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("Unknown parameter") && i.Contains("extra_field"));
    }

    // ── No schema ────────────────────────────────────────────────────

    [Fact]
    public void NoSchema_AcceptsAnything()
    {
        var tool = MakeTool("test", null!);
        var result = ToolArgumentValidator.Validate(
            Call("test", """{"anything": "goes"}"""),
            tool);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void EmptyObjectSchema_AcceptsAnything()
    {
        var tool = MakeTool("test", new { type = "object" });
        var result = ToolArgumentValidator.Validate(
            Call("test", """{"anything": "goes"}"""),
            tool);

        Assert.True(result.IsValid);
    }

    // ── PlanValidationReport ─────────────────────────────────────────

    [Fact]
    public void PlanValidationReport_Valid_HasSanitizedCalls()
    {
        var calls = new[] { Call("web_search", """{"query": "test"}""") };
        var report = PlanValidationReport.Valid(calls);

        Assert.True(report.IsValid);
        Assert.Single(report.SanitizedCalls);
        Assert.Null(report.RejectReasonCode);
    }

    [Fact]
    public void PlanValidationReport_Rejected_HasReasonAndPrompt()
    {
        var report = PlanValidationReport.Rejected("budget_exceeded", "Too many tool calls");

        Assert.False(report.IsValid);
        Assert.Equal("budget_exceeded", report.RejectReasonCode);
        Assert.Equal("Too many tool calls", report.RepairPrompt);
    }

    [Fact]
    public void PlanValidationReport_ToLegacy_Converts()
    {
        var report = PlanValidationReport.Rejected("policy_mismatch", "Not allowed");
        var legacy = report.ToLegacy();

        Assert.False(legacy.IsValid);
        Assert.Equal("policy_mismatch", legacy.RejectReasonCode);
        Assert.Equal("Not allowed", legacy.RepairPrompt);
    }
}
