using SirThaddeus.Agent;
using SirThaddeus.Agent.Policy;

namespace SirThaddeus.Tests.Continuity;

public sealed class BudgetPolicyTests
{
    [Fact]
    public void Default_MatchesCurrentHardcodedValues()
    {
        var p = BudgetPolicy.Default;
        Assert.Equal(15, p.MaxToolCalls);
        Assert.Equal(10, p.MaxLlmRoundTrips);
        Assert.Equal(5, p.MaxToolCallsPerResponse);
        Assert.Equal(2, p.MaxRepairs);
    }

    [Fact]
    public void NoTools_ZeroBudgets()
    {
        var p = BudgetPolicy.NoTools;
        Assert.Equal(0, p.MaxToolCalls);
        Assert.Equal(0, p.MaxToolCallsPerResponse);
        Assert.Equal(0, p.MaxRepairs);
        Assert.Equal(1, p.MaxLlmRoundTrips);
    }

    [Fact]
    public void Research_HigherBudgets()
    {
        var p = BudgetPolicy.Research;
        Assert.True(p.MaxToolCalls > BudgetPolicy.Default.MaxToolCalls);
        Assert.True(p.MaxLlmRoundTrips > BudgetPolicy.Default.MaxLlmRoundTrips);
        Assert.True(p.MaxRepairs > BudgetPolicy.Default.MaxRepairs);
    }

    [Fact]
    public void Registry_ChatOnly_ReturnsNoTools()
    {
        Assert.Same(BudgetPolicy.NoTools, BudgetPolicyRegistry.For(Intents.ChatOnly));
    }

    [Fact]
    public void Registry_UtilityDeterministic_ReturnsNoTools()
    {
        Assert.Same(BudgetPolicy.NoTools, BudgetPolicyRegistry.For(Intents.UtilityDeterministic));
    }

    [Fact]
    public void Registry_LookupDeepDive_ReturnsResearch()
    {
        Assert.Same(BudgetPolicy.Research, BudgetPolicyRegistry.For(Intents.LookupDeepDive));
    }

    [Fact]
    public void Registry_LookupFact_ReturnsDefault()
    {
        Assert.Same(BudgetPolicy.Default, BudgetPolicyRegistry.For(Intents.LookupFact));
    }

    [Fact]
    public void Registry_UnknownIntent_ReturnsDefault()
    {
        Assert.Same(BudgetPolicy.Default, BudgetPolicyRegistry.For("unknown_intent_xyz"));
    }

    [Fact]
    public void Registry_CaseInsensitive()
    {
        var lower = BudgetPolicyRegistry.For("chat_only");
        var upper = BudgetPolicyRegistry.For("CHAT_ONLY");
        Assert.Same(lower, upper);
    }

    [Fact]
    public void Default_NoTimeout()
    {
        Assert.Null(BudgetPolicy.Default.TurnTimeout);
        Assert.Null(BudgetPolicy.Default.MaxResponseTokens);
    }

    [Fact]
    public void CustomBudget_CanOverrideAll()
    {
        var custom = new BudgetPolicy
        {
            MaxToolCalls = 3,
            MaxLlmRoundTrips = 2,
            MaxToolCallsPerResponse = 1,
            MaxRepairs = 0,
            MaxResponseTokens = 512,
            TurnTimeout = TimeSpan.FromSeconds(30)
        };

        Assert.Equal(3, custom.MaxToolCalls);
        Assert.Equal(2, custom.MaxLlmRoundTrips);
        Assert.Equal(1, custom.MaxToolCallsPerResponse);
        Assert.Equal(0, custom.MaxRepairs);
        Assert.Equal(512, custom.MaxResponseTokens);
        Assert.Equal(TimeSpan.FromSeconds(30), custom.TurnTimeout);
    }
}
