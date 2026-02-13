using SirThaddeus.Agent;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class GuardrailsCoordinatorTests
{
    [Fact]
    public void TryRunDeterministicSpecialCase_OffMode_ReturnsNull()
    {
        var coordinator = BuildCoordinator();
        var result = coordinator.TryRunDeterministicSpecialCase(
            "If it takes 8 hours for 10 men to build a wall, how long does it take for 5 men to build the wall that is already built?",
            mode: "off");

        Assert.Null(result);
    }

    [Fact]
    public void TryRunDeterministicSpecialCase_EnabledMode_ReturnsDecision()
    {
        var coordinator = BuildCoordinator();
        var result = coordinator.TryRunDeterministicSpecialCase(
            "If it takes 8 hours for 10 men to build a wall, how long does it take for 5 men to build the wall that is already built?",
            mode: "auto");

        Assert.NotNull(result);
        Assert.Contains("zero time", result!.AnswerText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.LlmRoundTrips);
    }

    [Fact]
    public async Task TryRunAsync_BlocksRoutesThatNeedTools()
    {
        var coordinator = BuildCoordinator();
        var result = await coordinator.TryRunAsync(
            new RouterOutput
            {
                Intent = Intents.ScreenObserve,
                NeedsScreenRead = true,
                Confidence = 0.9
            },
            "Should I drive out now or pay at the kiosk first?",
            mode: "always");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryRunAsync_ChatRoute_CanReturnGuardrailsResult()
    {
        var coordinator = BuildCoordinator();
        var result = await coordinator.TryRunAsync(
            new RouterOutput
            {
                Intent = Intents.ChatOnly,
                Confidence = 0.8
            },
            "If it takes 8 hours for 10 men to build a wall, how long does it take for 5 men to build the wall that is already built?",
            mode: "always");

        Assert.NotNull(result);
        Assert.Contains("zero time", result!.AnswerText, StringComparison.OrdinalIgnoreCase);
    }

    private static GuardrailsCoordinator BuildCoordinator()
    {
        var llm = new FakeLlmClient((messages, _) => new LlmResponse
        {
            IsComplete = true,
            Content = "chat",
            FinishReason = "stop"
        });
        var audit = new TestAuditLogger();
        var pipeline = new ReasoningGuardrailsPipeline(llm, audit);
        return new GuardrailsCoordinator(pipeline);
    }
}

