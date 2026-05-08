using SirThaddeus.Agent;

namespace SirThaddeus.Tests;

public sealed class GeneralResponseQualityGuardsTests
{
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
}