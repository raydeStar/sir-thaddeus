using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public sealed class ContinuityHarnessTests
{
    public static IEnumerable<object[]> ScriptCases()
    {
        foreach (var script in ContinuityScriptLoader.LoadAll())
            yield return [script];
    }

    [Theory]
    [MemberData(nameof(ScriptCases))]
    public async Task ContinuityScript_Passes(ContinuityScript script)
    {
        var llm = BuildLlm(script.Llm);
        var mcp = BuildMcp(script.Mcp);
        var audit = new TestAuditLogger();

        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");
        agent.MemoryEnabled = false;

        if (script.SeedState is not null)
        {
            agent.SeedDialogueState(script.SeedState);
            agent.ContextLocked = script.SeedState.ContextLocked;
        }

        for (var index = 0; index < script.Turns.Count; index++)
        {
            var turn = script.Turns[index];
            var result = await agent.ProcessAsync(turn.UserMessage);
            var expect = turn.Expect;

            if (expect.Success.HasValue)
                Assert.Equal(expect.Success.Value, result.Success);

            foreach (var required in expect.TextContains)
            {
                Assert.Contains(required, result.Text, StringComparison.OrdinalIgnoreCase);
            }

            foreach (var forbidden in expect.TextNotContains)
            {
                Assert.DoesNotContain(forbidden, result.Text, StringComparison.OrdinalIgnoreCase);
            }

            if (expect.SuppressSourceCardsUi.HasValue)
                Assert.Equal(expect.SuppressSourceCardsUi.Value, result.SuppressSourceCardsUi);

            if (expect.SuppressToolActivityUi.HasValue)
                Assert.Equal(expect.SuppressToolActivityUi.Value, result.SuppressToolActivityUi);

            if (expect.LlmRoundTrips.HasValue)
                Assert.Equal(expect.LlmRoundTrips.Value, result.LlmRoundTrips);

            foreach (var mustInclude in expect.ToolCalls.Include)
            {
                Assert.Contains(result.ToolCallsMade,
                    call => call.ToolName.Equals(mustInclude, StringComparison.OrdinalIgnoreCase));
            }

            if (expect.ToolCalls.IncludeAny.Count > 0)
            {
                Assert.Contains(result.ToolCallsMade,
                    call => expect.ToolCalls.IncludeAny.Any(required =>
                        call.ToolName.Equals(required, StringComparison.OrdinalIgnoreCase)));
            }

            foreach (var mustExclude in expect.ToolCalls.Exclude)
            {
                Assert.DoesNotContain(result.ToolCallsMade,
                    call => call.ToolName.Equals(mustExclude, StringComparison.OrdinalIgnoreCase) ||
                            call.ToolName.Contains(mustExclude, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var argNeedle in expect.ToolArgsContains)
            {
                Assert.Contains(result.ToolCallsMade,
                    call => call.Arguments.Contains(argNeedle, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var argNeedle in expect.ToolArgsNotContains)
            {
                Assert.DoesNotContain(result.ToolCallsMade,
                    call => call.Arguments.Contains(argNeedle, StringComparison.OrdinalIgnoreCase));
            }

            if (expect.Context is not null)
            {
                var snapshot = result.ContextSnapshot ?? agent.GetContextSnapshot();
                AssertContext(expect.Context, snapshot, script.Id, index);
            }
        }
    }

    private static void AssertContext(
        ContinuityExpectedContext expected,
        SirThaddeus.Agent.Dialogue.DialogueContextSnapshot actual,
        string scriptId,
        int turnIndex)
    {
        var turnLabel = $"script={scriptId}, turn={turnIndex + 1}";
        if (!string.IsNullOrWhiteSpace(expected.Topic))
            Assert.Contains(expected.Topic, actual.Topic ?? "", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(expected.Location))
            Assert.Contains(expected.Location, actual.Location ?? "", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(expected.TimeScope))
            Assert.Contains(expected.TimeScope, actual.TimeScope ?? "", StringComparison.OrdinalIgnoreCase);

        if (expected.ContextLocked.HasValue)
            Assert.True(expected.ContextLocked.Value == actual.ContextLocked, turnLabel);

        if (expected.GeocodeMismatch.HasValue)
            Assert.True(expected.GeocodeMismatch.Value == actual.GeocodeMismatch, turnLabel);

        if (expected.LocationInferred.HasValue)
            Assert.True(expected.LocationInferred.Value == actual.LocationInferred, turnLabel);
    }

    private static FakeLlmClient BuildLlm(ContinuityLlmConfig config)
    {
        var mode = (config.Mode ?? "fixed").Trim().ToLowerInvariant();
        return mode switch
        {
            "never" => new FakeLlmClient(config.FixedResponse ?? "LLM should not be called"),
            "fixed" => new FakeLlmClient(config.FixedResponse ?? "Fixed LLM response."),
            "pipeline" => new FakeLlmClient((messages, _) =>
            {
                var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
                if (sys.Contains("Classify", StringComparison.OrdinalIgnoreCase))
                {
                    return new LlmResponse
                    {
                        IsComplete = true,
                        Content = config.Classification,
                        FinishReason = "stop"
                    };
                }

                if (sys.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
                {
                    return new LlmResponse
                    {
                        IsComplete = true,
                        Content = config.EntityJson,
                        FinishReason = "stop"
                    };
                }

                if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
                {
                    var user = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
                    var followupHint =
                        user.Contains("anything else", StringComparison.OrdinalIgnoreCase) ||
                        user.Contains("more", StringComparison.OrdinalIgnoreCase) ||
                        user.Contains("else", StringComparison.OrdinalIgnoreCase);

                    var query = followupHint &&
                                !string.IsNullOrWhiteSpace(config.FollowupQueryJson)
                        ? config.FollowupQueryJson!
                        : config.QueryJson;

                    return new LlmResponse
                    {
                        IsComplete = true,
                        Content = query,
                        FinishReason = "stop"
                    };
                }

                return new LlmResponse
                {
                    IsComplete = true,
                    Content = config.SummaryText,
                    FinishReason = "stop"
                };
            }),
            _ => new FakeLlmClient(config.FixedResponse ?? "Fallback LLM response.")
        };
    }

    private static FakeMcpClient BuildMcp(ContinuityMcpConfig config)
    {
        var mode = (config.Mode ?? "fixed").Trim().ToLowerInvariant();
        if (mode != "pertool")
            return new FakeMcpClient(config.FixedValue);

        var sequenceCursor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return new FakeMcpClient((tool, args) =>
        {
            if (!config.PerTool.TryGetValue(tool, out var toolConfig))
                return config.FixedValue;

            if (toolConfig.Sequence.Count > 0)
            {
                sequenceCursor.TryGetValue(tool, out var cursor);
                var index = Math.Min(cursor, toolConfig.Sequence.Count - 1);
                sequenceCursor[tool] = cursor + 1;
                return toolConfig.Sequence[index];
            }

            if (toolConfig.Contains.Count > 0)
            {
                foreach (var pair in toolConfig.Contains)
                {
                    if (args.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                        return pair.Value;
                }
            }

            if (!string.IsNullOrWhiteSpace(toolConfig.DefaultResponse))
                return toolConfig.DefaultResponse!;

            return config.FixedValue;
        });
    }
}
