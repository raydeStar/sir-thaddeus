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

    [Theory]
    [InlineData("Put the final answer on its own line as `Final answer: <answer>`.\n\nWhat is 2 + 2?")]
    [InlineData("Put the final answer on its own line as `Final answer: <answer>`.\r\n\r\nWhat is 2 + 2?")]
    [InlineData("PUT THE FINAL ANSWER ON ITS OWN LINE, formatted as Final answer: <answer>.\n\nName a primary color.")]
    public void Detects_explicit_labeled_final_answer_contract(string prompt)
    {
        Assert.True(ExplicitResponseContractDetector.RequiresLabeledFinalAnswerLine(prompt));
    }

    [Theory]
    [InlineData("Put the final answer on its own line.\n\nWhat is 2 + 2?")]
    [InlineData("Explain why the phrase `Final answer:` appears in grading rubrics.")]
    [InlineData("Summarize this.\n\nExample output: Final answer: concise")]
    public void Does_not_invent_a_labeled_line_contract(string prompt)
    {
        Assert.False(ExplicitResponseContractDetector.RequiresLabeledFinalAnswerLine(prompt));
    }

    [Theory]
    [InlineData("Final answer: green")]
    [InlineData("Brief reasoning.\n\nFinal Answer: 42")]
    public void Accepts_nonempty_labeled_final_answer_line(string response)
    {
        Assert.True(ExplicitResponseContractDetector.HasLabeledFinalAnswerLine(response));
    }

    [Theory]
    [InlineData("The final answer: green")]
    [InlineData("Final answer:")]
    [InlineData("Final answer: <answer>")]
    [InlineData("Final answer: <letter>")]
    [InlineData("Final answer: [choice]")]
    [InlineData("I would format it as `Final answer: green`.")]
    public void Rejects_missing_or_placeholder_labeled_final_answer_line(string response)
    {
        Assert.False(ExplicitResponseContractDetector.HasLabeledFinalAnswerLine(response));
    }

    [Theory]
    [InlineData("Put the final answer on its own line as `Final answer: <answer>`.\n\nChoose the correct letter choice from A, B, or C.")]
    [InlineData("Put the final answer on its own line as `Final answer: <answer>`.\n\nRespond using only the option letter.")]
    public void Detects_labeled_multiple_choice_letter_contract(string prompt)
    {
        Assert.True(ExplicitResponseContractDetector.RequiresLabeledMultipleChoiceLetter(prompt));
    }

    [Theory]
    [InlineData("Final answer: B", true)]
    [InlineData("Reasoning.\nFinal answer: j", true)]
    [InlineData("Final answer: K", true)]
    [InlineData("Final answer: the blue option", false)]
    [InlineData("Final answer: N/A", false)]
    [InlineData("Final answer: B because it is safer", false)]
    public void Validates_multiple_choice_letter_shape(string response, bool expected)
    {
        Assert.Equal(expected, ExplicitResponseContractDetector.HasLabeledMultipleChoiceLetter(response));
    }
}
