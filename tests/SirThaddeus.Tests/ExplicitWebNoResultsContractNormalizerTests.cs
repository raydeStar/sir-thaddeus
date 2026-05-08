using SirThaddeus.Agent;

namespace SirThaddeus.Tests;

public sealed class ExplicitWebNoResultsContractNormalizerTests
{
    [Fact]
    public void TryBuildResponse_WhenExplicitTimeoutPromptAndNoResults_ReturnsTimeoutMessage()
    {
        var response = ExplicitWebNoResultsContractNormalizer.TryBuildResponse(
            "Use web_search for AI policy news and handle timeout gracefully.",
            [new ToolCallRecord
            {
                ToolName = "web_search",
                Arguments = "{}",
                Result = "[search: 0 result(s) returned]",
                Success = true
            }]);

        Assert.Equal(ExplicitWebNoResultsContractNormalizer.TimeoutMessage, response);
    }

    [Fact]
    public void TryBuildResponse_WhenExplicitCSharpChangesPromptAndNoResults_ReturnsUnavailableMessage()
    {
        var response = ExplicitWebNoResultsContractNormalizer.TryBuildResponse(
            "Use web_search to answer what changed in C# 13 and keep it practical.",
            [new ToolCallRecord
            {
                ToolName = "web_search",
                Arguments = "{}",
                Result = "[search: 0 result(s) returned]",
                Success = true
            }]);

        // Live search returned no results, so the agent must not invent a feature list.
        // It defers to the unavailable message instead.
        Assert.Equal(ExplicitWebNoResultsContractNormalizer.UnavailableMessage, response);
    }

    [Fact]
    public void TryBuildResponse_WhenExplicitRustReleaseNotesPromptAndNoResults_ReturnsUnavailableMessage()
    {
        var response = ExplicitWebNoResultsContractNormalizer.TryBuildResponse(
            "Use web_search to find the latest Rust language release notes.",
            [new ToolCallRecord
            {
                ToolName = "web_search",
                Arguments = "{}",
                Result = "[search: 0 result(s) returned]",
                Success = true
            }]);

        Assert.Equal(ExplicitWebNoResultsContractNormalizer.UnavailableMessage, response);
    }

    [Fact]
    public void TryBuildResponse_WhenLatestStablePythonHasNoResults_ReturnsUnavailableMessage()
    {
        var response = ExplicitWebNoResultsContractNormalizer.TryBuildResponse(
            "What is the latest stable version of Python?",
            [new ToolCallRecord
            {
                ToolName = "web_search",
                Arguments = "{}",
                Result = "[search: 0 result(s) returned]",
                Success = true
            }]);

        // Live search returned no results, so the agent must not invent a subject-specific
        // "check python.org" response — that's still a hard-coded answer template. It defers
        // to the generic unavailable message instead.
        Assert.Equal(ExplicitWebNoResultsContractNormalizer.UnavailableMessage, response);
    }

    [Fact]
    public void TryBuildResponse_WhenPromptIsNotExplicitToolInvocation_ReturnsNull()
    {
        var response = ExplicitWebNoResultsContractNormalizer.TryBuildResponse(
            "What changed in C# 13?",
            [new ToolCallRecord
            {
                ToolName = "web_search",
                Arguments = "{}",
                Result = "[search: 0 result(s) returned]",
                Success = true
            }]);

        Assert.Null(response);
    }

    [Fact]
    public void ShouldPreserveResponse_WhenResponseMatchesDeterministicTimeoutContract_ReturnsTrue()
    {
        var toolCalls = new List<ToolCallRecord>
        {
            new()
            {
                ToolName = "web_search",
                Arguments = "{}",
                Result = "[search: 0 result(s) returned]",
                Success = true
            }
        };

        var shouldPreserve = ExplicitWebNoResultsContractNormalizer.ShouldPreserveResponse(
            "Use web_search for AI policy news and handle timeout gracefully.",
            ExplicitWebNoResultsContractNormalizer.TimeoutMessage,
            toolCalls);

        Assert.True(shouldPreserve);
    }
}