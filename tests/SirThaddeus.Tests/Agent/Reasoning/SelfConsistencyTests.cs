using SirThaddeus.Agent.Reasoning;

namespace SirThaddeus.Tests.Agent.Reasoning;

public class SelfConsistencyTests
{
    [Theory]
    [InlineData("The steps give 37. Final answer: 37", "37")]
    [InlineData("...so the total is 37.", "37")]
    [InlineData("Final answer: 0.2222222222", "0.2222222222")]
    [InlineData("That is 1,024 in total. Final answer: 1,024", "1024")]
    [InlineData("Final answer: 792", "792")]
    public void ExtractNumeric_prefers_final_answer_then_last_number(string text, string expected)
        => Assert.Equal(expected, SelfConsistency.ExtractNumeric(text));

    [Theory]
    [InlineData("Reasoning about it. Final answer: B", "B")]
    [InlineData("The correct option is (C).", "C")]
    [InlineData("Final answer: d", "D")]
    public void ExtractChoice_pulls_letter(string text, string expected)
        => Assert.Equal(expected, SelfConsistency.ExtractChoice(text));

    [Fact]
    public void Vote_takes_majority_over_noisy_samples()
    {
        var samples = new[]
        {
            "Final answer: 37",
            "Final answer: 32",
            "Final answer: 37",
            "Final answer: 19",
            "Final answer: 37",
        };

        var result = SelfConsistency.Vote(samples, SelfConsistency.ExtractNumeric);

        Assert.Equal("37", result.Answer);
        Assert.Equal(3, result.Votes);
        Assert.Equal(5, result.Samples);
    }

    [Fact]
    public void Vote_returns_null_when_no_sample_parses()
    {
        var result = SelfConsistency.Vote(["I'm not sure", "hard to say"], SelfConsistency.ExtractNumeric);
        Assert.Null(result.Answer);
    }

    [Fact]
    public void Vote_single_sample_degrades_to_that_answer()
    {
        var result = SelfConsistency.Vote(["Final answer: 42"], SelfConsistency.ExtractNumeric);
        Assert.Equal("42", result.Answer);
        Assert.Equal(1, result.Votes);
    }
}
