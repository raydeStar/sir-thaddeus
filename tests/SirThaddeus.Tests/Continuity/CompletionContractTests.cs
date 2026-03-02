using SirThaddeus.Agent;
using SirThaddeus.Agent.Validation.Completion;

namespace SirThaddeus.Tests.Continuity;

public sealed class CompletionContractTests
{
    // ── FieldRequirement ─────────────────────────────────────────────

    [Fact]
    public void FieldRequirement_DefaultsToRequired()
    {
        var field = new FieldRequirement { FieldName = "name" };
        Assert.Equal(FieldNecessity.Required, field.Necessity);
    }

    [Fact]
    public void FieldRequirement_AliasesDefaultEmpty()
    {
        var field = new FieldRequirement { FieldName = "phone" };
        Assert.Empty(field.Aliases);
    }

    [Fact]
    public void FieldRequirement_DescriptionDefaultEmpty()
    {
        var field = new FieldRequirement { FieldName = "phone" };
        Assert.Equal(string.Empty, field.Description);
    }

    // ── EvidenceRequirement ──────────────────────────────────────────

    [Fact]
    public void EvidenceRequirement_None_NoRequirements()
    {
        var ev = EvidenceRequirement.None;
        Assert.Equal(0, ev.MinSourceUrls);
        Assert.False(ev.RequiresNamedSource);
    }

    [Fact]
    public void EvidenceRequirement_AtLeastOneUrl()
    {
        var ev = EvidenceRequirement.AtLeastOneUrl;
        Assert.Equal(1, ev.MinSourceUrls);
        Assert.False(ev.RequiresNamedSource);
    }

    [Fact]
    public void EvidenceRequirement_NamedWithUrl()
    {
        var ev = EvidenceRequirement.NamedWithUrl;
        Assert.Equal(1, ev.MinSourceUrls);
        Assert.True(ev.RequiresNamedSource);
    }

    [Fact]
    public void EvidenceRequirement_RejectErrorOnlyResults_DefaultsTrue()
    {
        var ev = new EvidenceRequirement();
        Assert.True(ev.RejectErrorOnlyResults);
    }

    // ── CompletionContract ───────────────────────────────────────────

    [Fact]
    public void CompletionContract_AlwaysSatisfied_HasNoRequirements()
    {
        var c = CompletionContract.AlwaysSatisfied;
        Assert.Equal("*", c.Intent);
        Assert.Empty(c.Fields);
        Assert.Equal(0, c.MinItems);
        Assert.Equal(0, c.Evidence.MinSourceUrls);
    }

    [Fact]
    public void CompletionContract_DefaultsArePermissive()
    {
        var c = new CompletionContract { Intent = "test" };
        Assert.Empty(c.Fields);
        Assert.Equal(0, c.MinItems);
        Assert.Same(EvidenceRequirement.None, c.Evidence);
    }

    // ── CompletionReport ─────────────────────────────────────────────

    [Fact]
    public void CompletionReport_Satisfied_IsComplete()
    {
        var report = CompletionReport.Satisfied(CompletionContract.AlwaysSatisfied);
        Assert.True(report.IsComplete);
        Assert.Empty(report.MissingFields);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void CompletionReport_AlwaysSatisfied_Singleton()
    {
        var report = CompletionReport.AlwaysSatisfied;
        Assert.True(report.IsComplete);
        Assert.Same(CompletionContract.AlwaysSatisfied, report.Contract);
    }

    [Fact]
    public void CompletionReport_Incomplete_HasMissingFields()
    {
        var contract = new CompletionContract { Intent = "test" };
        var report = new CompletionReport
        {
            IsComplete = false,
            Contract = contract,
            MissingFields = ["phone", "address"],
            Issues = ["2 required fields missing"]
        };

        Assert.False(report.IsComplete);
        Assert.Equal(2, report.MissingFields.Count);
        Assert.Contains("phone", report.MissingFields);
    }

    [Fact]
    public void CompletionReport_MissingOptionalFields_StillComplete()
    {
        var contract = new CompletionContract { Intent = "test" };
        var report = new CompletionReport
        {
            IsComplete = true,
            Contract = contract,
            MissingOptionalFields = ["hours"]
        };

        Assert.True(report.IsComplete);
        Assert.Single(report.MissingOptionalFields);
    }

    // ── CompletionContractRegistry ───────────────────────────────────

    [Fact]
    public void Registry_LookupFact_HasExpectedFields()
    {
        var c = CompletionContractRegistry.For(Intents.LookupFact);
        Assert.Equal(Intents.LookupFact, c.Intent);
        Assert.True(c.Fields.Count >= 2);
        Assert.Contains(c.Fields, f => f.FieldName == "name");
        Assert.Contains(c.Fields, f => f.FieldName == "address");
        Assert.Equal(1, c.MinItems);
        Assert.Equal(1, c.Evidence.MinSourceUrls);
    }

    [Fact]
    public void Registry_LookupNews_RequiresEvidence()
    {
        var c = CompletionContractRegistry.For(Intents.LookupNews);
        Assert.Equal(1, c.Evidence.MinSourceUrls);
        Assert.Contains(c.Fields, f => f.FieldName == "answer");
    }

    [Fact]
    public void Registry_LookupDeepDive_RequiresNamedSource()
    {
        var c = CompletionContractRegistry.For(Intents.LookupDeepDive);
        Assert.True(c.Evidence.RequiresNamedSource);
        Assert.Equal(1, c.Evidence.MinSourceUrls);
    }

    [Fact]
    public void Registry_ChatOnly_ReturnsAlwaysSatisfied()
    {
        var c = CompletionContractRegistry.For(Intents.ChatOnly);
        Assert.Same(CompletionContract.AlwaysSatisfied, c);
    }

    [Fact]
    public void Registry_UnknownIntent_ReturnsAlwaysSatisfied()
    {
        var c = CompletionContractRegistry.For("totally_unknown_intent");
        Assert.Same(CompletionContract.AlwaysSatisfied, c);
    }

    [Fact]
    public void Registry_CaseInsensitive()
    {
        var lower = CompletionContractRegistry.For("lookup_fact");
        var upper = CompletionContractRegistry.For("LOOKUP_FACT");
        Assert.Same(lower, upper);
    }

    [Fact]
    public void Registry_AllContracts_HaveNonEmptyIntent()
    {
        foreach (var (key, contract) in CompletionContractRegistry.All)
        {
            Assert.False(string.IsNullOrEmpty(contract.Intent), $"Contract for '{key}' has empty intent");
            Assert.False(string.IsNullOrEmpty(contract.Label), $"Contract for '{key}' has empty label");
        }
    }

    [Fact]
    public void Registry_ScreenObserve_NoEvidenceRequired()
    {
        var c = CompletionContractRegistry.For(Intents.ScreenObserve);
        Assert.Equal(0, c.Evidence.MinSourceUrls);
        Assert.False(c.Evidence.RequiresNamedSource);
    }

    [Fact]
    public void Registry_MemoryWrite_NoEvidenceRequired()
    {
        var c = CompletionContractRegistry.For(Intents.MemoryWrite);
        Assert.Equal(0, c.Evidence.MinSourceUrls);
    }

    [Fact]
    public void Registry_BrowseOnce_NoEvidenceRequired()
    {
        var c = CompletionContractRegistry.For(Intents.BrowseOnce);
        Assert.Equal(0, c.Evidence.MinSourceUrls);
    }

    // ── FieldRequirement aliases ─────────────────────────────────────

    [Fact]
    public void Registry_LookupFact_AddressHasAliases()
    {
        var c = CompletionContractRegistry.For(Intents.LookupFact);
        var addressField = c.Fields.FirstOrDefault(f => f.FieldName == "address");
        Assert.NotNull(addressField);
        Assert.Contains("formatted_address", addressField.Aliases);
    }

    [Fact]
    public void Registry_LookupFact_PhoneIsOptional()
    {
        var c = CompletionContractRegistry.For(Intents.LookupFact);
        var phoneField = c.Fields.FirstOrDefault(f => f.FieldName == "phone");
        Assert.NotNull(phoneField);
        Assert.Equal(FieldNecessity.Optional, phoneField.Necessity);
    }
}
