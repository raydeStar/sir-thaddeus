using SirThaddeus.Harness.Cli;
using SirThaddeus.Harness.Execution;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Tests;

public sealed class StageTestValidatorTests
{
    [Fact]
    public async Task ValidateAsync_PreprocessTargetWithoutPreprocessCheck_Fails()
    {
        var validator = new StageTestValidator();
        var tests = new[]
        {
            new StageTestCase
            {
                Id = "query_only_followup",
                Name = "Query-only follow-up stage case",
                Input = "Pull up more info about that bakery.",
                Checks = new StageChecks
                {
                    Query = new QueryCheck
                    {
                        SearchQueryMustContain = ["Left Bank Pastry"]
                    }
                }
            }
        };

        var results = await validator.ValidateAsync(tests, HarnessStageTarget.Preprocess);

        var result = Assert.Single(results);
        Assert.False(result.Passed);
        Assert.Contains(result.Failures, failure => failure.Contains("does not define a preprocess check", StringComparison.OrdinalIgnoreCase));
    }
}