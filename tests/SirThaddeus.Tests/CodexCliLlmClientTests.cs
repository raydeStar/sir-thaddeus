using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public sealed class CodexCliLlmClientTests
{
    [Fact]
    public void Transport_event_filter_rejects_internal_tools_but_allows_model_messages()
    {
        var jsonl = string.Join('\n',
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\"}}",
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\"}}");

        var events = CodexCliLlmClient.FindForbiddenTransportEvents(jsonl);

        Assert.Single(events);
        Assert.Contains("command_execution", events[0]);
    }

    [Fact]
    public async Task ChatAsync_ParsesFinalEnvelope_WithoutGivingCodexToolAuthority()
    {
        CodexCliLlmClient.CodexCliInvocation? captured = null;
        var client = new CodexCliLlmClient(
            new LlmClientOptions { Provider = "codex-cli", Model = "gpt-5.6-luna" },
            (invocation, _) =>
            {
                captured = invocation;
                return Task.FromResult("{\"kind\":\"final\",\"content\":\"Hello from Thaddeus.\",\"tool_calls\":[]}");
            });

        var response = await client.ChatAsync([ChatMessage.User("hello")]);

        Assert.True(response.IsComplete);
        Assert.Equal("Hello from Thaddeus.", response.Content);
        Assert.NotNull(captured);
        Assert.Contains("Sir Thaddeus owns validation, permissions, execution", captured!.Prompt);
        Assert.Contains("workspace access", captured.Prompt);
        Assert.Contains("simulate, or claim to have executed any tool", captured.Prompt);
    }

    [Fact]
    public async Task ChatAsync_ParsesToolEnvelope_ForTheNormalToolLoop()
    {
        var client = new CodexCliLlmClient(
            new LlmClientOptions { Provider = "codex-cli", Model = "gpt-5.6-luna" },
            (_, _) => Task.FromResult("""
                {"kind":"tool_calls","content":"","tool_calls":[{"id":"call_weather","name":"weather_lookup","arguments":"{\"city\":\"Denver\"}"}]}
                """));

        var response = await client.ChatAsync(
            [ChatMessage.User("weather in Denver")],
            [new ToolDefinition
            {
                Function = new FunctionDefinition
                {
                    Name = "weather_lookup",
                    Description = "Looks up weather.",
                    Parameters = new { type = "object" }
                }
            }]);

        Assert.False(response.IsComplete);
        var call = Assert.Single(response.ToolCalls!);
        Assert.Equal("weather_lookup", call.Function.Name);
        Assert.Equal("{\"city\":\"Denver\"}", call.Function.Arguments);
    }

    [Fact]
    public async Task ChatAsync_RejectsAForcedToolMismatch()
    {
        var client = new CodexCliLlmClient(
            new LlmClientOptions { Provider = "codex-cli", Model = "gpt-5.6-luna" },
            (_, _) => Task.FromResult("""
                {"kind":"tool_calls","content":"","tool_calls":[{"id":"call_wrong","name":"weather_lookup","arguments":"{}"}]}
                """));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync(
            [ChatMessage.User("find a florist")],
            tools: null,
            forcedToolName: "places_lookup"));
    }
}
