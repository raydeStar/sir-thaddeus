using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class ReasoningGuardrailsModeTests
{
    [Fact]
    public async Task GuardrailsOff_DoesNotInterceptTrickPrompt()
    {
        var llm = MakeGuardrailsAwareLlm(normalReply: "Normal assistant fallback.");
        var mcp = new FakeMcpClient(returnValue: "unused");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            ReasoningGuardrailsMode = "off"
        };

        var result = await agent.ProcessAsync(
            "The parking garage gate is ahead. Should I drive out now or pay at the kiosk first?");

        Assert.True(result.Success);
        Assert.False(result.GuardrailsUsed);
        Assert.Equal("Normal assistant fallback.", result.Text);
        Assert.Empty(result.GuardrailsRationale);
    }

    [Fact]
    public async Task GuardrailsAuto_TriggersOnGoalConflictPrompt()
    {
        var llm = MakeGuardrailsAwareLlm(normalReply: "Normal assistant fallback.");
        var mcp = new FakeMcpClient(returnValue: "unused");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            ReasoningGuardrailsMode = "auto"
        };

        var result = await agent.ProcessAsync(
            "The parking garage gate is ahead. Should I drive out now or pay at the kiosk first?");

        Assert.True(result.Success);
        Assert.True(result.GuardrailsUsed);
        Assert.Contains("pay at the kiosk first", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.GuardrailsRationale.Count >= 3);
        Assert.StartsWith("Goal:", result.GuardrailsRationale[0], StringComparison.Ordinal);
        Assert.StartsWith("Constraint:", result.GuardrailsRationale[1], StringComparison.Ordinal);
        Assert.StartsWith("Decision:", result.GuardrailsRationale[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuardrailsAlways_MalformedStructuredOutput_FallsBackToNormalPath()
    {
        var llm = MakeGuardrailsAwareLlm(
            normalReply: "Normal assistant fallback.",
            malformedGuardrailsJson: true);
        var mcp = new FakeMcpClient(returnValue: "unused");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            ReasoningGuardrailsMode = "always"
        };

        var result = await agent.ProcessAsync("Hey there, how are you?");

        Assert.True(result.Success);
        Assert.False(result.GuardrailsUsed);
        Assert.Equal("Normal assistant fallback.", result.Text);
        Assert.Empty(result.GuardrailsRationale);
    }

    [Fact]
    public async Task GuardrailsRationale_DoesNotLeakChainOfThought()
    {
        var llm = MakeGuardrailsAwareLlm(normalReply: "Normal assistant fallback.");
        var mcp = new FakeMcpClient(returnValue: "unused");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            ReasoningGuardrailsMode = "always"
        };

        var result = await agent.ProcessAsync(
            "Hotel check-in needs ID at the desk. Should I wait in the room or bring ID downstairs?");

        Assert.True(result.Success);
        Assert.True(result.GuardrailsUsed);
        foreach (var line in result.GuardrailsRationale)
        {
            Assert.DoesNotContain("analysis", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("step-by-step", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("thought", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(
        "A man is looking at a photograph of someone. His friend asks who it is. The man replies, \"Brothers and sisters, I have none. But that man's father is my father's son.\" Who is in the photograph?",
        "his son")]
    [InlineData(
        "A woman is looking at a photograph of someone. Her friend asks who it is. She replies, \"I am an only child. But that woman's mother is my mother's daughter.\" Who is in the photograph?",
        "her daughter")]
    [InlineData(
        "A man is pointing to a photograph of someone. His friend asks who it is. He replies, \"I have no siblings. That man's son is my father's son.\" Who is in the photograph?",
        "his father")]
    public async Task FamilyPhotoPuzzle_AlwaysMode_ResolvesDeterministically(
        string prompt,
        string expectedFragment)
    {
        var llm = MakeGuardrailsAwareLlm(normalReply: "Normal assistant fallback.");
        var mcp = new FakeMcpClient(returnValue: "unused");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            ReasoningGuardrailsMode = "always"
        };

        var result = await agent.ProcessAsync(prompt);

        Assert.True(result.Success);
        Assert.True(result.GuardrailsUsed);
        Assert.Contains(expectedFragment, result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.GuardrailsRationale.Count >= 3);
    }

    [Fact]
    public async Task FamilyPhotoPuzzle_ConflictingClues_AsksForClarification()
    {
        var llm = MakeGuardrailsAwareLlm(normalReply: "Normal assistant fallback.");
        var mcp = new FakeMcpClient(returnValue: "unused");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            ReasoningGuardrailsMode = "always"
        };

        var result = await agent.ProcessAsync(
            "A woman is looking at a photograph of someone. She says, \"Brothers and sisters, I have none. But that man's father is my father's son.\" Who is in the photograph?");

        Assert.True(result.Success);
        Assert.True(result.GuardrailsUsed);
        Assert.Contains("cannot be resolved", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FamilyPhotoPuzzle_GuardrailsOff_StaysInChatPath()
    {
        var llm = MakeGuardrailsAwareLlm(normalReply: "Normal assistant fallback.");
        var mcp = new FakeMcpClient(returnValue: "unused");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            ReasoningGuardrailsMode = "off"
        };

        var result = await agent.ProcessAsync(
            "A man is looking at a photograph of someone. His friend asks who it is. The man replies, \"Brothers and sisters, I have none. But that man's father is my father's son.\" Who is in the photograph?");

        Assert.True(result.Success);
        Assert.False(result.GuardrailsUsed);
        Assert.Equal("Normal assistant fallback.", result.Text);
    }

    [Fact]
    public async Task FamilyPhotoPuzzle_MothersDaughterVariant_GuardrailsOff_StaysInChatPath()
    {
        var llm = MakeGuardrailsAwareLlm(normalReply: "Normal assistant fallback.");
        var mcp = new FakeMcpClient(returnValue: "unused");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            ReasoningGuardrailsMode = "off"
        };

        var result = await agent.ProcessAsync(
            "A woman is looking at a photograph of someone. She says, \"I am an only child. But that woman's mother is my mother's daughter.\" Who's in the photograph?");

        Assert.True(result.Success);
        Assert.False(result.GuardrailsUsed);
        Assert.Equal("Normal assistant fallback.", result.Text);
    }

    [Fact]
    public async Task CompletedTaskDurationTrick_AutoMode_ReturnsZeroTime()
    {
        var llm = MakeGuardrailsAwareLlm(normalReply: "Normal assistant fallback.");
        var mcp = new FakeMcpClient(returnValue: "unused");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            ReasoningGuardrailsMode = "auto"
        };

        var result = await agent.ProcessAsync(
            "If it takes 8 hours for 10 men to build a wall, how long does it take for 5 men to build the wall that is already built?");

        Assert.True(result.Success);
        Assert.True(result.GuardrailsUsed);
        Assert.Contains("zero time", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static FakeLlmClient MakeGuardrailsAwareLlm(
        string normalReply,
        bool malformedGuardrailsJson = false)
    {
        return new FakeLlmClient((messages, _) =>
        {
            var system = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (system.Contains("Classify whether this user question is a goal-conflict trick prompt", StringComparison.OrdinalIgnoreCase))
            {
                return Respond("""{"risk":"medium","why":"likely goal conflict","suggest_guardrails":true}""");
            }

            if (system.Contains("Infer the practical real-world goal", StringComparison.OrdinalIgnoreCase))
            {
                return malformedGuardrailsJson
                    ? Respond("not json")
                    : Respond("""{"primary_goal":"Complete the real-world prerequisite for the task.","alternative_goals":[],"confidence":0.92}""");
            }

            if (system.Contains("Extract entities and action options", StringComparison.OrdinalIgnoreCase))
            {
                return malformedGuardrailsJson
                    ? Respond("not json")
                    : Respond("""{"entities":[{"name":"id","kind":"required_object","required":true}],"options":[{"label":"wait in the room","preconditions":[],"effects":[]},{"label":"bring ID downstairs","preconditions":[],"effects":[]}]}""");
            }

            if (system.Contains("Build practical constraints", StringComparison.OrdinalIgnoreCase))
            {
                return malformedGuardrailsJson
                    ? Respond("not json")
                    : Respond("""{"constraints":["Respect explicit prerequisites and choose the option that physically completes the task."]}""");
            }

            if (system.Contains("Classify", StringComparison.OrdinalIgnoreCase))
                return Respond("chat");

            return Respond(normalReply);
        });
    }

    private static LlmResponse Respond(string content) => new()
    {
        IsComplete = true,
        Content = content,
        FinishReason = "stop"
    };
}

public class ReasoningGuardrailsBenchTests
{
    [Theory]
    [InlineData("My library hold expires tonight. Should I call the book home or go pick it up?", "go pick it up")]
    [InlineData("The parking garage gate is ahead. Should I drive out now or pay at the kiosk first?", "pay at the kiosk first")]
    [InlineData("My laptop repair is ready at the shop. Should I text 'fixed' or bring the device and collect it?", "bring the device and collect it")]
    [InlineData("Hotel check-in needs ID at the desk. Should I wait in the room or bring ID downstairs?", "bring id downstairs")]
    [InlineData("I need a key cut at the hardware store. Should I email the key or take the key there?", "take the key there")]
    [InlineData("I have dry-cleaning pickup before close. Should I call my jacket over or go collect it?", "go collect it")]
    public async Task TrickQuestionBench_AlwaysMode_SelectsGoalAlignedOption(
        string prompt,
        string expectedDecisionFragment)
    {
        var llm = MakeBenchLlm();
        var mcp = new FakeMcpClient(returnValue: "unused");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.")
        {
            ReasoningGuardrailsMode = "always"
        };

        var result = await agent.ProcessAsync(prompt);

        Assert.True(result.Success);
        Assert.True(result.GuardrailsUsed);
        Assert.Contains(expectedDecisionFragment, result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.GuardrailsRationale.Count >= 3);
    }

    private static FakeLlmClient MakeBenchLlm()
    {
        return new FakeLlmClient((messages, _) =>
        {
            var system = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (system.Contains("Infer the practical real-world goal", StringComparison.OrdinalIgnoreCase))
            {
                return Respond("""{"primary_goal":"Choose the action that actually completes the real-world task.","alternative_goals":[],"confidence":0.9}""");
            }

            if (system.Contains("Build practical constraints", StringComparison.OrdinalIgnoreCase))
            {
                return Respond("""{"constraints":["Pick the option that satisfies required real-world preconditions."]}""");
            }

            if (system.Contains("Classify", StringComparison.OrdinalIgnoreCase))
                return Respond("chat");

            return Respond("Normal assistant fallback.");
        });
    }

    private static LlmResponse Respond(string content) => new()
    {
        IsComplete = true,
        Content = content,
        FinishReason = "stop"
    };
}
