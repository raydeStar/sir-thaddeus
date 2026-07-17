using SirThaddeus.Agent;
using SirThaddeus.Agent.Validation;

namespace SirThaddeus.Tests.Agent.Validation;

public sealed class EvidenceBackedAnswerOnlyProjectionTests
{
    [Fact]
    public void Projects_one_verbatim_span_from_successful_file_evidence()
    {
        var result = EvidenceBackedAnswerOnlyProjection.Project(
            "Use file_read. Reply with only the codename value.",
            "The codename is **CIRRUS-284**.",
            [Successful("{\"ok\":true,\"textContent\":\"Codename: CIRRUS-284\"}")]);

        Assert.True(result.Applied);
        Assert.Equal("CIRRUS-284", result.Text);
    }

    [Fact]
    public void Collapses_a_shared_sentence_to_its_nested_answer_span()
    {
        var result = EvidenceBackedAnswerOnlyProjection.Project(
            "Use wiki_search. Answer with only the code.",
            "The Meridian hatch code is KESTREL-541.",
            [Successful("{\"results\":[{\"excerpt\":\"The Meridian hatch code is KESTREL-541.\"}]}")]);

        Assert.True(result.Applied);
        Assert.Equal("KESTREL-541", result.Text);
    }

    [Fact]
    public void Ignores_irrelevant_successful_evidence()
    {
        var result = EvidenceBackedAnswerOnlyProjection.Project(
            "Use the files. Return only the primary check value.",
            "Primary check: LUMEN-263.",
            [
                Successful("{\"textContent\":\"LUMEN-263\"}"),
                Successful("{\"textContent\":\"Routine notes: complete\"}")
            ]);

        Assert.True(result.Applied);
        Assert.Equal("LUMEN-263", result.Text);
    }

    [Theory]
    [InlineData("Explain why the code is correct.", "not_answer_only")]
    [InlineData("Reply with only the code and explain why.", "explanation_requested")]
    [InlineData("Return both relay values only.", "plural_contract")]
    public void Rejects_non_scalar_response_contracts(string prompt, string reason)
    {
        var result = EvidenceBackedAnswerOnlyProjection.Project(
            prompt,
            "The code is ORBIT-117.",
            [Successful("{\"textContent\":\"Code: ORBIT-117\"}")]);

        Assert.False(result.Applied);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void Rejects_failed_or_missing_tool_evidence()
    {
        var failed = EvidenceBackedAnswerOnlyProjection.Project(
            "Reply with only the code.",
            "The code is ORBIT-117.",
            [new ToolCallRecord { ToolName = "file_read", Arguments = "{}", Result = "Error: missing", Success = false }]);
        var missing = EvidenceBackedAnswerOnlyProjection.Project(
            "Reply with only the code.",
            "The code is ORBIT-117.",
            []);

        Assert.False(failed.Applied);
        Assert.False(missing.Applied);
        Assert.Equal("no_successful_tool_evidence", failed.Reason);
        Assert.Equal("no_successful_tool_evidence", missing.Reason);
    }

    [Fact]
    public void Rejects_distinct_shared_values()
    {
        var result = EvidenceBackedAnswerOnlyProjection.Project(
            "Reply with only the current relay value.",
            "Current relay: ORBIT-117\nBackup relay: ORBIT-882",
            [Successful("{\"textContent\":\"Current relay: ORBIT-117\\nBackup relay: ORBIT-882\"}")]);

        Assert.False(result.Applied);
        Assert.Equal("ambiguous_shared_spans", result.Reason);
    }

    [Fact]
    public void Leaves_an_already_exact_answer_unchanged()
    {
        var result = EvidenceBackedAnswerOnlyProjection.Project(
            "Give only the location.",
            "Harbor Seven",
            [Successful("{\"excerpt\":\"The staging bay is Harbor Seven.\"}")]);

        Assert.False(result.Applied);
        Assert.Equal("already_exact", result.Reason);
        Assert.Equal("Harbor Seven", result.Text);
    }

    private static ToolCallRecord Successful(string result) => new()
    {
        ToolName = "test_tool",
        Arguments = "{}",
        Result = result,
        Success = true
    };
}
