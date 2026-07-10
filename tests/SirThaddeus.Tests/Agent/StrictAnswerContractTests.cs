using SirThaddeus.Agent;
using SirThaddeus.Agent.PostProcessing;

namespace SirThaddeus.Tests.Agent;

public sealed class StrictAnswerContractTests
{
    [Theory]
    [InlineData("Reply with only the integer.")]
    [InlineData("Answer with just one number, please.")]
    [InlineData("Return just the numeric value.")]
    [InlineData("Output only the final sum.")]
    [InlineData("Decimal only.")]
    public void RequestsBareNumeric_accepts_natural_output_contracts(string prompt)
    {
        Assert.True(StrictAnswerContract.RequestsBareNumeric(prompt));
    }

    [Theory]
    [InlineData("Only calculate a number if useful, then explain your method.")]
    [InlineData("Give me the result and show the steps.")]
    [InlineData("Explain what numeric value means in this context.")]
    [InlineData("")]
    public void RequestsBareNumeric_rejects_explanatory_or_ambiguous_requests(string prompt)
    {
        Assert.False(StrictAnswerContract.RequestsBareNumeric(prompt));
    }

    [Theory]
    [InlineData("73")]
    [InlineData("-0.125")]
    [InlineData("+4")]
    [InlineData(".5")]
    [InlineData("-1.25e3")]
    public void IsBareNumeric_accepts_common_machine_numeric_forms(string value)
    {
        Assert.True(StrictAnswerContract.IsBareNumeric(value));
    }

    [Theory]
    [InlineData("1,000")]
    [InlineData("answer: 73")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void IsBareNumeric_rejects_ambiguous_or_decorated_values(string value)
    {
        Assert.False(StrictAnswerContract.IsBareNumeric(value));
    }

    [Fact]
    public void PostProcessor_uses_shared_contract_for_unseen_paraphrase()
    {
        var normalized = DeterministicChatPostProcessor.TryNormalizeStrictAnswerOnlyReply(
            "Please output just one number.",
            "After checking the inputs, the answer is 73.");

        Assert.Equal("73", normalized);
    }
}
