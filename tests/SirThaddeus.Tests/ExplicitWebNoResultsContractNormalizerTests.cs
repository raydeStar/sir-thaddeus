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
    public void TryBuildResponse_WhenExplicitCSharpChangesPromptAndNoResults_ReturnsStableFallback()
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

        Assert.NotNull(response);
        Assert.Contains("C# 13", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("params collections", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("System.Threading.Lock", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unavailable", response, StringComparison.OrdinalIgnoreCase);
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
    public void TryBuildResponse_WhenLatestStablePythonHasNoResults_PreservesSubject()
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

        Assert.NotNull(response);
        Assert.Contains("Python", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("latest stable version", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("python.org", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Please retry", response, StringComparison.OrdinalIgnoreCase);
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