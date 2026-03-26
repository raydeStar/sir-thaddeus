using SirThaddeus.Agent;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Scoring;
using SirThaddeus.Harness.Tracing;

namespace SirThaddeus.Tests;

public class ScoringEngineTests
{
    [Fact]
    public void Score_DoesNotPenalizeOpaqueDocumentReadSummary_AsMissingIncorporation()
    {
        var scorer = new ScoringEngine();
        var test = new HarnessTestCase
        {
            Id = "personality_doc_read",
            Name = "personality_doc_read",
            UserMessage = "Explain the TCP three-way handshake.",
            AllowedTools = ["document_read"],
            Assertions = new HarnessAssertions
            {
                AllowedToolsOnly = true,
                RequireStructuredErrors = false,
                RequireNoHallucinatedCitations = true,
                ForbidInfrastructureErrors = true
            },
            Expectations = new HarnessExpectations
            {
                RequiredKeywords = ["SYN", "ACK"]
            },
            MinScore = 7
        };

        var response = new AgentResponse
        {
            Text = "TCP uses SYN, SYN-ACK, and ACK to confirm both sides can communicate before data starts flowing.",
            Success = true,
            ToolCallsMade =
            [
                new ToolCallRecord
                {
                    ToolName = "document_read",
                    Arguments = "{\"path\":\"docs/TCP-Handshake.md\"}",
                    Result = "[Document content: 100 chars, sha256=04b41be5fc8b]",
                    Success = true
                }
            ],
            LlmRoundTrips = 0
        };

        var steps = new List<TraceStep>
        {
            new()
            {
                StepIndex = 1,
                StepType = "tool_result",
                ToolName = "document_read",
                Result = "[Document content: 100 chars, sha256=04b41be5fc8b]"
            }
        };

        var score = scorer.Score(test, response, steps, judgeResult: null);

        Assert.Equal(0, score.ToolTokensAvailable);
        Assert.Equal(0, score.ToolTokensIncorporated);
        Assert.Equal(0, score.ToolIncorporationPenalty);
    }
}