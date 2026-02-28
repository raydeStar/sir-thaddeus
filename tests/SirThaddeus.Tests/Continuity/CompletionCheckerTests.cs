using SirThaddeus.Agent;
using SirThaddeus.Agent.Validation.Completion;

namespace SirThaddeus.Tests.Continuity;

public sealed class CompletionCheckerTests
{
    private readonly CompletionChecker _checker = new();

    // ── Helpers ──────────────────────────────────────────────────────

    private static ToolCallRecord Ok(string toolName, string resultJson) => new()
    {
        ToolName = toolName,
        Arguments = "{}",
        Result = resultJson,
        Success = true
    };

    private static ToolCallRecord Err(string toolName, string error) => new()
    {
        ToolName = toolName,
        Arguments = "{}",
        Result = error,
        Success = false
    };

    // ── AlwaysSatisfied ──────────────────────────────────────────────

    [Fact]
    public void AlwaysSatisfied_WithNoResults_IsComplete()
    {
        var report = _checker.Check(CompletionContract.AlwaysSatisfied, []);
        Assert.True(report.IsComplete);
    }

    [Fact]
    public void AlwaysSatisfied_WithResults_IsComplete()
    {
        var report = _checker.Check(CompletionContract.AlwaysSatisfied, [Ok("web_search", "{}")]);
        Assert.True(report.IsComplete);
    }

    // ── Simple field checking ────────────────────────────────────────

    [Fact]
    public void RequiredField_Present_IsComplete()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [new FieldRequirement { FieldName = "name", Necessity = FieldNecessity.Required }]
        };

        var results = new[] { Ok("places_lookup", """{"name": "Joe's Bakery"}""") };
        var report = _checker.Check(contract, results);

        Assert.True(report.IsComplete);
        Assert.Empty(report.MissingFields);
    }

    [Fact]
    public void RequiredField_Missing_IsIncomplete()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [new FieldRequirement { FieldName = "name", Necessity = FieldNecessity.Required }]
        };

        var results = new[] { Ok("places_lookup", """{"address": "123 Main St"}""") };
        var report = _checker.Check(contract, results);

        Assert.False(report.IsComplete);
        Assert.Contains("name", report.MissingFields);
    }

    [Fact]
    public void RequiredField_EmptyString_IsMissing()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [new FieldRequirement { FieldName = "name", Necessity = FieldNecessity.Required }]
        };

        var results = new[] { Ok("places_lookup", """{"name": ""}""") };
        var report = _checker.Check(contract, results);

        Assert.False(report.IsComplete);
        Assert.Contains("name", report.MissingFields);
    }

    [Fact]
    public void OptionalField_Missing_StillComplete()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields =
            [
                new FieldRequirement { FieldName = "name", Necessity = FieldNecessity.Required },
                new FieldRequirement { FieldName = "phone", Necessity = FieldNecessity.Optional }
            ]
        };

        var results = new[] { Ok("places_lookup", """{"name": "Joe's Bakery"}""") };
        var report = _checker.Check(contract, results);

        Assert.True(report.IsComplete);
        Assert.Empty(report.MissingFields);
        Assert.Contains("phone", report.MissingOptionalFields);
    }

    // ── Alias matching ───────────────────────────────────────────────

    [Fact]
    public void Field_MatchedByAlias_IsFound()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [new FieldRequirement
            {
                FieldName = "address",
                Necessity = FieldNecessity.Required,
                Aliases = ["formatted_address", "location"]
            }]
        };

        var results = new[] { Ok("places_lookup", """{"formatted_address": "123 Main St"}""") };
        var report = _checker.Check(contract, results);

        Assert.True(report.IsComplete);
    }

    // ── Evidence requirements ────────────────────────────────────────

    [Fact]
    public void Evidence_UrlRequired_FoundInJson_IsComplete()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [new FieldRequirement { FieldName = "answer" }],
            Evidence = EvidenceRequirement.AtLeastOneUrl
        };

        var results = new[] { Ok("web_search", """{"answer": "42", "url": "https://example.com"}""") };
        var report = _checker.Check(contract, results);

        Assert.True(report.IsComplete);
    }

    [Fact]
    public void Evidence_UrlRequired_NotFound_IsIncomplete()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [new FieldRequirement { FieldName = "answer" }],
            Evidence = EvidenceRequirement.AtLeastOneUrl
        };

        var results = new[] { Ok("web_search", """{"answer": "42"}""") };
        var report = _checker.Check(contract, results);

        Assert.False(report.IsComplete);
        Assert.Contains(report.Issues, i => i.Contains("source URL"));
    }

    [Fact]
    public void Evidence_UrlFoundInAssistantText_IsComplete()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [],
            Evidence = EvidenceRequirement.AtLeastOneUrl
        };

        var report = _checker.Check(contract, [], assistantText: "See https://example.com for details.");

        Assert.True(report.IsComplete);
    }

    [Fact]
    public void Evidence_NamedSource_FoundInAssistant_IsComplete()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [],
            Evidence = EvidenceRequirement.NamedWithUrl
        };

        var report = _checker.Check(
            contract,
            [Ok("web_search", """{"url": "https://nyt.com/article"}""")],
            assistantText: "According to the New York Times, the event was held on Friday.");

        Assert.True(report.IsComplete);
    }

    // ── MinItems ─────────────────────────────────────────────────────

    [Fact]
    public void MinItems_MetByArray_IsComplete()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [],
            MinItems = 2
        };

        var results = new[] { Ok("places_lookup", """[{"name":"A"},{"name":"B"},{"name":"C"}]""") };
        var report = _checker.Check(contract, results);

        Assert.True(report.IsComplete);
        Assert.Equal(3, report.ItemCount);
    }

    [Fact]
    public void MinItems_NotMet_IsIncomplete()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [],
            MinItems = 3
        };

        var results = new[] { Ok("places_lookup", """[{"name":"A"}]""") };
        var report = _checker.Check(contract, results);

        Assert.False(report.IsComplete);
        Assert.Contains(report.Issues, i => i.Contains("item"));
    }

    [Fact]
    public void MinItems_NestedResultsArray_CountsCorrectly()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [],
            MinItems = 2
        };

        var json = """{"results": [{"name":"A"},{"name":"B"}]}""";
        var results = new[] { Ok("web_search", json) };
        var report = _checker.Check(contract, results);

        Assert.True(report.IsComplete);
        Assert.Equal(2, report.ItemCount);
    }

    // ── Error handling ───────────────────────────────────────────────

    [Fact]
    public void ErrorResults_AreIgnored()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [new FieldRequirement { FieldName = "name" }]
        };

        var results = new[]
        {
            Err("places_lookup", "Tool error: timeout"),
            Ok("places_lookup", """{"name": "Joe's"}""")
        };

        var report = _checker.Check(contract, results);
        Assert.True(report.IsComplete);
    }

    [Fact]
    public void AllErrorResults_ReportsIssue()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [new FieldRequirement { FieldName = "name" }],
            Evidence = new EvidenceRequirement { RejectErrorOnlyResults = true }
        };

        var results = new[] { Err("places_lookup", "Tool error: timeout") };
        var report = _checker.Check(contract, results);

        Assert.False(report.IsComplete);
        Assert.Contains(report.Issues, i => i.Contains("error"));
    }

    [Fact]
    public void StructuredErrorJson_IsIgnored()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [new FieldRequirement { FieldName = "name" }]
        };

        var results = new[] { Ok("places_lookup", """{"error": "not_found", "name": "ghost"}""") };
        var report = _checker.Check(contract, results);

        Assert.False(report.IsComplete);
    }

    // ── Assistant text field extraction ───────────────────────────────

    [Fact]
    public void AssistantText_SubstantiveAnswer_SatisfiesAnswerField()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [new FieldRequirement { FieldName = "answer" }]
        };

        var report = _checker.Check(contract, [], assistantText: "The capital of France is Paris, which has been the capital since the 10th century.");
        Assert.True(report.IsComplete);
    }

    [Fact]
    public void AssistantText_TooShort_DoesNotSatisfyAnswer()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [new FieldRequirement { FieldName = "answer" }]
        };

        var report = _checker.Check(contract, [], assistantText: "Yes.");
        Assert.False(report.IsComplete);
    }

    // ── Real-world contract tests ────────────────────────────────────

    [Fact]
    public void LookupFact_FullBusinessResult_IsComplete()
    {
        var contract = CompletionContractRegistry.For(Intents.LookupFact);
        var json = """
        {
            "name": "Joe's Bakery",
            "address": "123 Main St, Portland, OR",
            "phone": "503-555-1234",
            "website": "https://joesbakery.com",
            "url": "https://maps.google.com/joes"
        }
        """;

        var report = _checker.Check(contract, [Ok("places_lookup", json)]);
        Assert.True(report.IsComplete);
    }

    [Fact]
    public void LookupFact_MinimalBusinessResult_StillComplete()
    {
        var contract = CompletionContractRegistry.For(Intents.LookupFact);
        // name + address are required; phone/website are optional
        var json = """
        {
            "name": "Joe's Bakery",
            "address": "123 Main St",
            "url": "https://maps.google.com/joes"
        }
        """;

        var report = _checker.Check(contract, [Ok("places_lookup", json)]);
        Assert.True(report.IsComplete);
        Assert.Contains("phone", report.MissingOptionalFields);
        // "website" is satisfied by the "url" alias in the JSON
    }

    [Fact]
    public void LookupFact_MissingName_IsIncomplete()
    {
        var contract = CompletionContractRegistry.For(Intents.LookupFact);
        var json = """{"address": "123 Main St", "url": "https://example.com"}""";

        var report = _checker.Check(contract, [Ok("places_lookup", json)]);
        Assert.False(report.IsComplete);
        Assert.Contains("name", report.MissingFields);
    }

    [Fact]
    public void LookupNews_WithAnswerAndUrl_IsComplete()
    {
        var contract = CompletionContractRegistry.For(Intents.LookupNews);
        var json = """{"answer": "Major earthquake hits region", "source_url": "https://reuters.com/eq"}""";

        var report = _checker.Check(contract, [Ok("web_search", json)]);
        Assert.True(report.IsComplete);
    }

    // ── Null/empty edge cases ────────────────────────────────────────

    [Fact]
    public void EmptyToolResults_WithNoRequirements_IsComplete()
    {
        var contract = new CompletionContract { Intent = "test" };
        var report = _checker.Check(contract, []);
        Assert.True(report.IsComplete);
    }

    [Fact]
    public void NullContract_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _checker.Check(null!, []));
    }

    [Fact]
    public void NullToolResults_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _checker.Check(CompletionContract.AlwaysSatisfied, null!));
    }

    [Fact]
    public void InvalidJson_InToolResult_DoesNotThrow()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields = [new FieldRequirement { FieldName = "name" }]
        };

        var results = new[] { Ok("places_lookup", "this is not json at all") };
        var report = _checker.Check(contract, results);
        Assert.False(report.IsComplete);
    }

    // ── Multiple tool results merged ─────────────────────────────────

    [Fact]
    public void MultipleResults_FieldsAcrossResults_AreAggregated()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields =
            [
                new FieldRequirement { FieldName = "name" },
                new FieldRequirement { FieldName = "address" }
            ]
        };

        var results = new[]
        {
            Ok("tool1", """{"name": "Joe's"}"""),
            Ok("tool2", """{"address": "123 Main St"}""")
        };

        var report = _checker.Check(contract, results);
        Assert.True(report.IsComplete);
    }
}
