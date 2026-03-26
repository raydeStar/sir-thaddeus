using SirThaddeus.Harness.Execution;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Tests;

public sealed class StageContextUtilitiesTests
{
    [Fact]
    public void BuildQueryBuilderContext_PrefersExplicitFollowUpAnchor()
    {
        var context = new StageExecutionContext
        {
            AssistantContext = "Here's a bakery I found nearby in Olympia: Wagner's European Bakery and Cafe at 1013 Capitol Way S.",
            FollowUpAnchor = "Left Bank Pastry",
            UserCity = "Olympia, WA",
            HasRecentSearchResults = true
        };

        var result = StageContextUtilities.BuildQueryBuilderContext(context, "Seattle, WA");

        Assert.Equal("Olympia, WA", result.UserCity);
        Assert.Equal("Left Bank Pastry", result.FollowUpAnchor);
        Assert.Single(result.RecentMessages);
        Assert.Equal("assistant", result.RecentMessages[0].Role);
    }

    [Fact]
    public void ResolveFollowUpAnchor_UsesAssistantContextWhenExplicitAnchorMissing()
    {
        var context = new StageExecutionContext
        {
            AssistantContext = "Here's a bakery I found nearby in Olympia: Left Bank Pastry at 108 5th Ave SW, Olympia, WA."
        };

        var result = StageContextUtilities.ResolveFollowUpAnchor(context);

        Assert.Equal("Left Bank Pastry", result);
    }
}