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
    public void TryRunDeterministicSpecialCase_EnabledMode_ReturnsNull_WithoutHardcodedSolvers()
    {
        var coordinator = BuildCoordinator();
        var result = coordinator.TryRunDeterministicSpecialCase(
            "If it takes 8 hours for 10 men to build a wall, how long does it take for 5 men to build the wall that is already built?",
            mode: "auto");

        Assert.Null(result);
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
    public async Task TryRunAsync_ChatRoute_WithStructuredLlm_ReturnsGuardrailsResult()
    {
        var coordinator = BuildCoordinator(returnStructuredJson: true);
        var result = await coordinator.TryRunAsync(
            new RouterOutput
            {
                Intent = Intents.ChatOnly,
                Confidence = 0.8
            },
            "Should I walk or drive to the car wash that is 500 meters away?",
            mode: "always");

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.AnswerText));
    }

    private static GuardrailsCoordinator BuildCoordinator(bool returnStructuredJson = false)
    {
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolDefinition>?, LlmResponse> handler;

        if (returnStructuredJson)
        {
            var callCount = 0;
            handler = (messages, _) =>
            {
                callCount++;
                var content = callCount switch
                {
                    1 => """{"primary_goal":"Get the car washed","alternative_goals":[],"confidence":0.9}""",
                    2 => """{"entities":[{"name":"car","kind":"required_object","required":true}],"options":[{"label":"walk","preconditions":[],"effects":[]},{"label":"drive","preconditions":[],"effects":[]}]}""",
                    3 => """{"constraints":["The car must physically reach the car wash"]}""",
                    _ => "Drive to the car wash since the car needs to be there."
                };
                return new LlmResponse { IsComplete = true, Content = content, FinishReason = "stop" };
            };
        }
        else
        {
            handler = (_, _) => new LlmResponse { IsComplete = true, Content = "chat", FinishReason = "stop" };
        }

        var llm = new FakeLlmClient(handler);
        var audit = new TestAuditLogger();
        var pipeline = new ReasoningGuardrailsPipeline(llm, audit);
        return new GuardrailsCoordinator(pipeline);
    }
}
