using SirThaddeus.Agent.Validation.Completion;

namespace SirThaddeus.Tests.Continuity;

public sealed class RepairPlannerTests
{
    private readonly RepairPlanner _planner = new();

    // ── No repair needed ─────────────────────────────────────────────

    [Fact]
    public void AlreadyComplete_ReturnsNull()
    {
        var report = CompletionReport.Satisfied(CompletionContract.AlwaysSatisfied);
        Assert.Null(_planner.Plan(report, 1, 2));
    }

    [Fact]
    public void BudgetExhausted_ReturnsNull()
    {
        var report = new CompletionReport
        {
            IsComplete = false,
            Contract = new CompletionContract { Intent = "test" },
            MissingFields = ["name"],
            Issues = ["missing field"]
        };

        Assert.Null(_planner.Plan(report, 3, 2));
    }

    [Fact]
    public void NullReport_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _planner.Plan(null!, 1, 2));
    }

    // ── Repair directives ────────────────────────────────────────────

    [Fact]
    public void MissingFields_ProducesDirective()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            Fields =
            [
                new FieldRequirement { FieldName = "name", Description = "business name" },
                new FieldRequirement { FieldName = "phone", Description = "phone number" }
            ]
        };

        var report = new CompletionReport
        {
            IsComplete = false,
            Contract = contract,
            MissingFields = ["name", "phone"]
        };

        var directive = _planner.Plan(report, 1, 2);

        Assert.NotNull(directive);
        Assert.Contains("name", directive.RepairPrompt);
        Assert.Contains("phone", directive.RepairPrompt);
        Assert.Contains("business name", directive.RepairPrompt);
        Assert.Contains("phone number", directive.RepairPrompt);
        Assert.Contains("[REPAIR 1/2]", directive.RepairPrompt);
        Assert.Equal(1, directive.AttemptNumber);
        Assert.Equal(2, directive.MaxAttempts);
    }

    [Fact]
    public void Issues_IncludedInPrompt()
    {
        var report = new CompletionReport
        {
            IsComplete = false,
            Contract = new CompletionContract { Intent = "test" },
            MissingFields = [],
            Issues = ["Expected at least 1 source URL(s), found 0"]
        };

        var directive = _planner.Plan(report, 1, 2);

        Assert.NotNull(directive);
        Assert.Contains("source URL", directive.RepairPrompt);
    }

    [Fact]
    public void MinItems_ShortfallIncludedInPrompt()
    {
        var contract = new CompletionContract
        {
            Intent = "test",
            MinItems = 3
        };

        var report = new CompletionReport
        {
            IsComplete = false,
            Contract = contract,
            MissingFields = [],
            Issues = ["Expected at least 3 item(s), found 1"],
            ItemCount = 1
        };

        var directive = _planner.Plan(report, 1, 2);

        Assert.NotNull(directive);
        Assert.Contains("3", directive.RepairPrompt);
        Assert.Contains("1", directive.RepairPrompt);
    }

    [Fact]
    public void DirectiveContainsAntiHallucinationInstructions()
    {
        var report = new CompletionReport
        {
            IsComplete = false,
            Contract = new CompletionContract { Intent = "test" },
            MissingFields = ["name"],
            Issues = []
        };

        var directive = _planner.Plan(report, 1, 2);

        Assert.NotNull(directive);
        Assert.Contains("Do NOT fabricate", directive.RepairPrompt);
        Assert.Contains("Do NOT repeat", directive.RepairPrompt);
    }

    [Fact]
    public void SecondAttempt_ShowsCorrectCounter()
    {
        var report = new CompletionReport
        {
            IsComplete = false,
            Contract = new CompletionContract { Intent = "test" },
            MissingFields = ["address"],
            Issues = []
        };

        var directive = _planner.Plan(report, 2, 3);

        Assert.NotNull(directive);
        Assert.Contains("[REPAIR 2/3]", directive.RepairPrompt);
        Assert.Equal(2, directive.AttemptNumber);
        Assert.Equal(3, directive.MaxAttempts);
    }

    [Fact]
    public void LastAttempt_StillAllowed()
    {
        var report = new CompletionReport
        {
            IsComplete = false,
            Contract = new CompletionContract { Intent = "test" },
            MissingFields = ["address"],
            Issues = []
        };

        // Attempt 2 of max 2 should still be allowed
        var directive = _planner.Plan(report, 2, 2);
        Assert.NotNull(directive);
    }

    [Fact]
    public void DirectiveMissingFields_MatchReportMissingFields()
    {
        var report = new CompletionReport
        {
            IsComplete = false,
            Contract = new CompletionContract { Intent = "test" },
            MissingFields = ["name", "phone"],
            Issues = []
        };

        var directive = _planner.Plan(report, 1, 2);

        Assert.NotNull(directive);
        Assert.Equal(2, directive.MissingFields.Count);
        Assert.Contains("name", directive.MissingFields);
        Assert.Contains("phone", directive.MissingFields);
    }
}
