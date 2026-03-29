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

    [Fact]
    public void Score_HardFails_DeepDiveZeroResultFallbackNonAnswer()
    {
        var scorer = new ScoringEngine();
        var test = new HarnessTestCase
        {
            Id = "web_deep_dive_place_briefing",
            Name = "web_deep_dive_place_briefing",
            UserMessage = "Deep dive Seattle Flowers with hours + reviews and what to expect.",
            AllowedTools = ["memory_retrieve", "places_lookup", "web_search", "browser_navigate"],
            Assertions = new HarnessAssertions
            {
                AllowedToolsOnly = true,
                RequireStructuredErrors = false,
                RequireNoHallucinatedCitations = true,
                ForbidInfrastructureErrors = true
            },
            Expectations = new HarnessExpectations(),
            MinScore = 7
        };

        var response = new AgentResponse
        {
            Text = "**Seattle Flowers**\nVerification recommended\nHours were not found in available sources.\nThe fallback search came back with 0 results for this query.\nCurrent open status is unknown from the available sources. Check the listed source before visiting.\nSources checked: deep-dive.\nBriefing summary: hours and review details are based on currently available web sources.",
            Success = true,
            ToolCallsMade =
            [
                new ToolCallRecord
                {
                    ToolName = "places_lookup",
                    Arguments = "{}",
                    Result = "[Places lookup error: Google Places API key is not configured.]",
                    Success = false
                },
                new ToolCallRecord
                {
                    ToolName = "web_search",
                    Arguments = "{}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ],
            LlmRoundTrips = 0
        };

        var score = scorer.Score(test, response, [], judgeResult: null);

        Assert.False(score.HardPass);
        Assert.Contains(score.HardFailures, failure =>
            failure.Contains("web-grounding fallback non-answer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Score_HardFails_DirectoryStyleLocalBusinessFallbackNonAnswer()
    {
        var scorer = new ScoringEngine();
        var test = new HarnessTestCase
        {
            Id = "web_local_business_florist",
            Name = "web_local_business_florist",
            UserMessage = "Find me a good florist in Hillsboro, OR.",
            AllowedTools = ["memory_retrieve", "places_lookup", "web_search", "browser_navigate"],
            Assertions = new HarnessAssertions
            {
                AllowedToolsOnly = true,
                RequireStructuredErrors = false,
                RequireNoHallucinatedCitations = true,
                ForbidInfrastructureErrors = true
            },
            Expectations = new HarnessExpectations(),
            MinScore = 7
        };

        var response = new AgentResponse
        {
            Text = "Here are the live florists results I found in Hillsboro, OR: - **Related Group and Dezer Development Top Off Rosewood Residences Hillsboro Beach** — 2026-02-09 08:00 UTC · source: profilemiamire.com These came back as directory-style local results rather than single verified storefront pages. If you want, give me a neighborhood or major street and I can narrow the deli search further. -- Sir Thaddeus",
            Success = true,
            ToolCallsMade =
            [
                new ToolCallRecord
                {
                    ToolName = "web_search",
                    Arguments = "{}",
                    Result = "[search: 3 result(s) returned]",
                    Success = true
                }
            ],
            LlmRoundTrips = 0
        };

        var score = scorer.Score(test, response, [], judgeResult: null);

        Assert.False(score.HardPass);
        Assert.Contains(score.HardFailures, failure =>
            failure.Contains("local-business fallback non-answer", StringComparison.OrdinalIgnoreCase));
    }
}