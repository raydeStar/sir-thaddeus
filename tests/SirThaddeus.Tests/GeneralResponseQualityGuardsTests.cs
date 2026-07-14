using SirThaddeus.Agent;

namespace SirThaddeus.Tests;

public sealed class GeneralResponseQualityGuardsTests
{
    [Theory]
    [InlineData("Hey, how are you doing? Just wanted to say thanks for your help.", "I'm doing well")]
    [InlineData("Thank you, I really appreciate the help!", "You're very welcome")]
    public void Apply_WhenTurnIsPureSocialThanks_ReturnsGroundedAcknowledgment(
        string userMessage,
        string expectedPrefix)
    {
        var result = GeneralResponseQualityGuards.Apply(
            "Today's date is Sunday. Would you like clarification?",
            userMessage);

        Assert.StartsWith(expectedPrefix, result, StringComparison.Ordinal);
        Assert.DoesNotContain("date", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("?", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WhenThanksIncludesActionableRequest_DoesNotReplaceDraft()
    {
        const string draft = "A hash table maps keys to values.";

        var result = GeneralResponseQualityGuards.Apply(
            draft,
            "Thanks—now explain how a hash table works.");

        Assert.Equal(draft, result);
    }

    [Fact]
    public void Apply_WhenTcpHandshakeExplanationIsOverlong_CompressesToConciseStructuredAnswer()
    {
        var overlongResponse = string.Join(" ", Enumerable.Repeat(
            "The TCP three-way handshake uses SYN, SYN-ACK, and ACK to establish a reliable connection, synchronize sequence numbers, prove both endpoints are reachable, avoid half-open confusion, support retransmission, preserve ordering, and make sure data transfer begins only after both sides agree on connection state.",
            4));

        var result = GeneralResponseQualityGuards.Apply(
            overlongResponse,
            "Explain how TCP three-way handshake works and why it matters for reliability.");

        Assert.Contains("TCP three-way handshake", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SYN-ACK", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reliability", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Length < 700, "Expected TCP fallback to stay concise for no-tool technical explanations.");
    }

    [Fact]
    public void Apply_WhenTcpHandshakeExplanationIsAlreadyConcise_LeavesItAlone()
    {
        const string conciseResponse = "TCP starts with SYN, continues with SYN-ACK, and finishes with ACK so both sides agree before data moves reliably.";

        var result = GeneralResponseQualityGuards.Apply(
            conciseResponse,
            "Explain how TCP three-way handshake works and why it matters for reliability.");

        Assert.Equal(conciseResponse, result);
    }

    [Fact]
    public void Apply_WhenHashTableExplanationIsOverlong_CompressesToMidLengthAnswer()
    {
        var overlongResponse = string.Join(" ", Enumerable.Repeat(
            "A hash table maps a key through a hash function into an array index, then stores the value there. It is useful for lookup-heavy systems, caches, counters, indexes, and deduplication, but it has tradeoffs around collisions, ordering, and memory.",
            8));

        var result = GeneralResponseQualityGuards.Apply(
            overlongResponse,
            "What is a hash table and when should I use one?");

        Assert.Contains("hash table", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("key", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Length <= 1200, "Expected hash-table fallback to satisfy the mid-length personality fixture.");
    }

    [Fact]
    public void Apply_WhenDebuggingHaikuMentionsError_RephrasesForbiddenWord()
    {
        const string response = "Night code softly blinks.\nCoffee cools beside the logs.\nMorning ships the fix.\n\nSend me the error message and I can look for that elusive error.";

        var result = GeneralResponseQualityGuards.Apply(
            response,
            "Write me a haiku about debugging code at 3am.");

        Assert.DoesNotContain("error", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Night code softly blinks.\nCoffee cools beside the logs.\nMorning ships the fix.", result);
    }
}
