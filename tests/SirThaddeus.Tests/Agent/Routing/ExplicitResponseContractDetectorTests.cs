using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Tests.Agent.Routing;

public sealed class ExplicitResponseContractDetectorTests
{
    [Fact]
    public void Ignores_tool_signals_inside_completed_examples()
    {
        var prompt = "Put the final answer on its own line as `Final answer: <answer>`.\n\n" +
                     "Example: Research current pricing. Answer: B.\n\nQuestion: A or B?";

        Assert.True(ExplicitResponseContractDetector.IsNoToolDirectAnswer(prompt));
    }

    [Theory]
    [InlineData("Research current pricing and reply with only the total.")]
    [InlineData("Browse https://example.com and return only the title.")]
    [InlineData("Verify today's release and answer only yes or no.")]
    public void Keeps_real_tool_requests_out_of_direct_answer_lane(string prompt)
    {
        Assert.False(ExplicitResponseContractDetector.IsNoToolDirectAnswer(prompt));
    }

    [Fact]
    public void Ordinary_request_is_not_an_explicit_contract()
    {
        Assert.False(ExplicitResponseContractDetector.IsNoToolDirectAnswer("Explain DNS simply."));
    }
}
