using SirThaddeus.Agent;

namespace SirThaddeus.Tests.Agent;

public class HedgeSignalDetectorTests
{
    // ── Positive cases: hedge in factual context = verify ───────────────

    [Theory]
    [InlineData(
        "When did Starfield come out?",
        "I believe Starfield came out in 2023, though I'm not 100% sure.")]
    [InlineData(
        "Who is the CEO of OpenAI?",
        "As of my training cutoff, the CEO was Sam Altman, but this may have changed since.")]
    [InlineData(
        "Does the iPhone 16 Pro Max exist?",
        "If I recall correctly, Apple announced the iPhone 16 line in late 2024.")]
    [InlineData(
        "What is the latest Python version?",
        "As far as I know, Python 3.11 is the latest stable release.")]
    [InlineData(
        "How much does a Tesla Model Y cost?",
        "I think it's around $50,000, based on my information.")]
    [InlineData(
        "What's the current price of Bitcoin?",
        "I don't have access to real-time data, so I can't give you an exact current price.")]
    // "My internal archives" is how the Thaddeus voice rephrases
    // "training cutoff" — caught verbatim from the harness dragon case.
    [InlineData(
        "Can you tell me if the new live-action How to Train Your Dragon is word for word like the original movies?",
        "That is a question that requires more current information than my internal archives contain.")]
    // Deferral shape: model noticed it should search but asked the user
    // for permission. Treat as hedge so we search proactively instead.
    [InlineData(
        "Can you tell me if the new live-action How to Train Your Dragon is word for word like the original movies?",
        "Cinematic adaptations vary. Shall I perform a web search to find out how close the adaptation is?")]
    [InlineData(
        "What is the latest Python version?",
        "Would you like me to search for the latest stable release?")]
    // Recommendation shape: "Can you recommend a good X" — the model
    // sometimes answers with a deferral ("shall I proceed..."). Harness
    // caught this in the ashwagandha case. "Can you recommend" needed
    // to be a recognized factual shape AND "shall I proceed" needed a
    // deferral marker.
    [InlineData(
        "Can you recommend a good Ashwagandha on Amazon.com?",
        "To provide a useful recommendation, I will need to perform a search of the web for current listings. Shall I proceed with searching Amazon.com?")]
    [InlineData(
        "Can you recommend a good deli nearby?",
        "I shall use the web_search tool. Let me search for local reviews.")]
    [InlineData(
        "Find me a good Italian place downtown",
        "I will search for Italian restaurants in the area.")]
    public void Should_verify_when_hedge_in_factual_answer(string userPrompt, string draft)
    {
        Assert.True(HedgeSignalDetector.ShouldVerify(draft, userPrompt));
    }

    // ── Negative: hedge in creative/opinion context = leave alone ───────
    // This is the user's core concern: "I don't want it to run off on a
    // random search". Legitimate hedge language in opinion or creative
    // prompts must NOT trigger verification.

    [Theory]
    [InlineData(
        "Write me a poem about the ocean.",
        "I think this captures the feeling you were after, though you may want to tweak the second stanza.")]
    [InlineData(
        "What do you think of my code?",
        "I believe the structure is clean, though as far as I know, there's a subtle bug in the loop condition.")]
    [InlineData(
        "Can you help me refactor this?",
        "If I recall correctly, the idiomatic approach here is to use a visitor pattern.")]
    [InlineData(
        "How are you doing?",
        "I'm doing well, thanks for asking! I believe it's going to be a productive day.")]
    public void Does_not_verify_when_hedge_in_non_factual_response(string userPrompt, string draft)
    {
        Assert.False(HedgeSignalDetector.ShouldVerify(draft, userPrompt));
    }

    // ── Negative: confident factual answer = leave alone ────────────────

    [Theory]
    [InlineData(
        "When did Starfield come out?",
        "Starfield was released on September 6, 2023 by Bethesda Game Studios.")]
    [InlineData(
        "What is 2+2?",
        "The answer is 4.")]
    public void Does_not_verify_when_no_hedge_marker(string userPrompt, string draft)
    {
        Assert.False(HedgeSignalDetector.ShouldVerify(draft, userPrompt));
    }

    // ── Guards ──────────────────────────────────────────────────────────

    [Fact]
    public void Empty_inputs_return_false()
    {
        Assert.False(HedgeSignalDetector.ShouldVerify(null, "what is the latest Python version"));
        Assert.False(HedgeSignalDetector.ShouldVerify("I believe ...", null));
        Assert.False(HedgeSignalDetector.ShouldVerify("", "what is X"));
        Assert.False(HedgeSignalDetector.ShouldVerify("I believe", "   "));
    }
}
