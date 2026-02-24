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


    [Fact]
    public async Task TryRunAsync_ChatRoute_DoesNotRequireThinkTagsInSynthesisPrompt()
    {
        string finalSystemPrompt = string.Empty;
        string finalUserPrompt = string.Empty;

        var llm = new FakeLlmClient((messages, _) =>
        {
            var system = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? string.Empty;

            if (system.Contains("real-world goal", StringComparison.OrdinalIgnoreCase))
                return Respond("""{"primary_goal":"Get the car washed","alternative_goals":[],"confidence":0.9}""");
            if (system.Contains("Extract entities", StringComparison.OrdinalIgnoreCase))
                return Respond("""{"entities":[{"name":"car","kind":"required_object","required":true}],"options":[{"label":"walk","preconditions":[],"effects":[]},{"label":"drive","preconditions":[],"effects":[]}]}""");
            if (system.Contains("Build first-principles constraints", StringComparison.OrdinalIgnoreCase))
                return Respond("""{"constraints":["The car must physically reach the car wash"]}""");
            if (system.Contains("Break this into first principles", StringComparison.OrdinalIgnoreCase))
                return Respond("""{"need":"wash the car","pieces":"car, car wash location, available actions","assembly":"pick the action that gets the car to the wash"}""");

            finalSystemPrompt = system;
            finalUserPrompt = messages.FirstOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
            return Respond("Drive to the car wash.");
        });

        var coordinator = new GuardrailsCoordinator(new ReasoningGuardrailsPipeline(llm, new TestAuditLogger()));
        var result = await coordinator.TryRunAsync(
            new RouterOutput { Intent = Intents.ChatOnly, Confidence = 0.8 },
            "Should I walk or drive to the car wash that is 50 meters away?",
            mode: "always");

        Assert.NotNull(result);
        Assert.DoesNotContain("<think>", finalSystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<think>", finalUserPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Final answer:", finalSystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Need:", finalUserPrompt, StringComparison.Ordinal);
        Assert.Contains("Pieces:", finalUserPrompt, StringComparison.Ordinal);
        Assert.Contains("Assembly:", finalUserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryRunAsync_LookupRoute_WithLowConfidenceGuardrailsAnswer_ReturnsNullForWebFallback()
    {
        var callCount = 0;
        var llm = new FakeLlmClient((messages, _) =>
        {
            callCount++;
            var content = callCount switch
            {
                1 => """{"primary_goal":"Get the car washed","alternative_goals":[],"confidence":0.9}""",
                2 => """{"entities":[{"name":"car","kind":"required_object","required":true}],"options":[{"label":"walk","preconditions":[],"effects":[]},{"label":"drive","preconditions":[],"effects":[]}]}""",
                3 => """{"constraints":["The car must physically reach the car wash"]}""",
                4 => """{"need":"wash the car","pieces":"car, wash location","assembly":"choose option that gets car to wash"}""",
                _ => "It depends on your preferences and conditions."
            };
            return new LlmResponse { IsComplete = true, Content = content, FinishReason = "stop" };
        });

        var coordinator = new GuardrailsCoordinator(new ReasoningGuardrailsPipeline(llm, new TestAuditLogger()));
        var result = await coordinator.TryRunAsync(
            new RouterOutput { Intent = Intents.LookupFact, Confidence = 0.9, NeedsWeb = true, NeedsSearch = true },
            "The car wash is 50m away. Should I walk or drive?",
            mode: "always");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryRunAsync_CarWashChoice_IncludesDeterministicFeasibilityConstraintInFinalPrompt()
    {
        string finalUserPrompt = string.Empty;

        var llm = new FakeLlmClient((messages, _) =>
        {
            var system = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? string.Empty;

            if (system.Contains("real-world goal", StringComparison.OrdinalIgnoreCase))
                return Respond("""{"primary_goal":"Get the car washed","alternative_goals":[],"confidence":0.9}""");
            if (system.Contains("Extract entities", StringComparison.OrdinalIgnoreCase))
                return Respond("""{"entities":[{"name":"car","kind":"required_object","required":true}],"options":[{"label":"walk","preconditions":[],"effects":[]},{"label":"drive","preconditions":[],"effects":[]}]}""");
            if (system.Contains("Build first-principles constraints", StringComparison.OrdinalIgnoreCase))
                return Respond("""{"constraints":["Pick the easiest option"]}""");
            if (system.Contains("Break this into first principles", StringComparison.OrdinalIgnoreCase))
                return Respond("""{"need":"wash the car","pieces":"car, wash location, actions","assembly":"choose feasible action"}""");

            finalUserPrompt = messages.FirstOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
            return Respond("Drive.");
        });

        var coordinator = new GuardrailsCoordinator(new ReasoningGuardrailsPipeline(llm, new TestAuditLogger()));
        var result = await coordinator.TryRunAsync(
            new RouterOutput { Intent = Intents.ChatOnly, Confidence = 0.9 },
            "The car wash is 50m from my house. Do I walk or drive?",
            mode: "always");

        Assert.NotNull(result);
        Assert.Contains("Feasibility:", finalUserPrompt, StringComparison.Ordinal);
        Assert.Contains("car", finalUserPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("physically arrive", finalUserPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryRunAsync_CarWashChoice_CorrectsToDeterministicDriveAnswer()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var system = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? string.Empty;

            if (system.Contains("real-world goal", StringComparison.OrdinalIgnoreCase))
                return Respond("""{"primary_goal":"Get the car washed","alternative_goals":[],"confidence":0.9}""");
            if (system.Contains("Extract entities", StringComparison.OrdinalIgnoreCase))
                return Respond("""{"entities":[{"name":"car","kind":"required_object","required":true}],"options":[{"label":"walk","preconditions":[],"effects":[]},{"label":"drive","preconditions":[],"effects":[]}]}""");
            if (system.Contains("Build first-principles constraints", StringComparison.OrdinalIgnoreCase))
                return Respond("""{"constraints":["Pick the easiest option"]}""");
            if (system.Contains("Break this into first principles", StringComparison.OrdinalIgnoreCase))
                return Respond("""{"need":"wash the car","pieces":"car, wash location, actions","assembly":"choose feasible action"}""");

            return Respond("Walk.");
        });

        var coordinator = new GuardrailsCoordinator(new ReasoningGuardrailsPipeline(llm, new TestAuditLogger()));
        var result = await coordinator.TryRunAsync(
            new RouterOutput { Intent = Intents.ChatOnly, Confidence = 0.9 },
            "The car wash is 50m from my house. Do I walk or drive?",
            mode: "always");

        Assert.NotNull(result);
        Assert.Contains("Drive", result!.AnswerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("car", result!.AnswerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Decision:", result.RationaleLines.Last(), StringComparison.Ordinal);
    }

    private static GuardrailsCoordinator BuildCoordinator(bool returnStructuredJson = false)
    {
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolDefinition>?, LlmResponse> handler;

        if (returnStructuredJson)
        {
            handler = (messages, _) =>
            {
                var system = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? string.Empty;

                if (system.Contains("real-world goal", StringComparison.OrdinalIgnoreCase))
                    return Respond("""{"primary_goal":"Get the car washed","alternative_goals":[],"confidence":0.9}""");
                if (system.Contains("Extract entities", StringComparison.OrdinalIgnoreCase))
                    return Respond("""{"entities":[{"name":"car","kind":"required_object","required":true}],"options":[{"label":"walk","preconditions":[],"effects":[]},{"label":"drive","preconditions":[],"effects":[]}]}""");
                if (system.Contains("Build first-principles constraints", StringComparison.OrdinalIgnoreCase))
                    return Respond("""{"constraints":["The car must physically reach the car wash"]}""");
                if (system.Contains("Break this into first principles", StringComparison.OrdinalIgnoreCase))
                    return Respond("""{"need":"wash the car","pieces":"car, car wash location, available actions","assembly":"pick the action that gets the car to the wash"}""");

                return Respond("Drive to the car wash since the car needs to be there.");
            };
        }
        else
        {
            handler = (_, _) => Respond("chat");
        }

        var llm = new FakeLlmClient(handler);
        var audit = new TestAuditLogger();
        var pipeline = new ReasoningGuardrailsPipeline(llm, audit);
        return new GuardrailsCoordinator(pipeline);
    }

    private static LlmResponse Respond(string content) =>
        new LlmResponse { IsComplete = true, Content = content, FinishReason = "stop" };
}
