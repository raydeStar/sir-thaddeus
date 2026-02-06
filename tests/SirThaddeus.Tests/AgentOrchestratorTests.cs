using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

// ─────────────────────────────────────────────────────────────────────────
// Agent Orchestrator Tests
//
// Covers: LLM-based intent classification, search query + recency
// extraction, self-dialogue truncation, and the overall classify →
// tool → interpret flow.
//
// All tests use a FakeLlmClient to control model responses without
// any real LLM dependency, and TestAuditLogger to avoid file I/O.
// ─────────────────────────────────────────────────────────────────────────

#region ── Intent Classification ──────────────────────────────────────────

public class IntentClassificationTests
{

    [Theory]
    [InlineData("chat",   false)]  // Casual → no tool calls
    [InlineData("search", true)]   // WebLookup → web_search tool
    [InlineData("tool",   true)]   // Tooling → tool call loop
    public async Task ClassifiesIntent_BasedOnLlmResponse(string llmReply, bool expectsToolCall)
    {
        // The LLM returns the classification on the first call,
        // then a summary on subsequent calls.
        var callIndex = 0;
        var llm = new FakeLlmClient(messages =>
        {
            callIndex++;
            // First call = classification, second+ = actual response
            if (callIndex == 1)
                return llmReply;
            return "Here's a helpful summary of the results.";
        });

        var mcp = new FakeMcpClient(returnValue: "tool output here");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "You are a test assistant.");

        var result = await agent.ProcessAsync("what happened in the stock market today?");

        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));

        if (expectsToolCall && llmReply == "search")
        {
            Assert.Contains(result.ToolCallsMade, t => t.ToolName.Contains("web_search", StringComparison.OrdinalIgnoreCase)
                                                     || t.ToolName.Contains("WebSearch", StringComparison.OrdinalIgnoreCase));
        }
        else if (!expectsToolCall)
        {
            Assert.Empty(result.ToolCallsMade);
        }
    }

    [Theory]
    [InlineData("/search stock market")]
    [InlineData("/chat just say hello")]
    [InlineData("search: bitcoin price")]
    public async Task ExplicitOverrides_BypassLlmClassification(string userMessage)
    {
        var classifyCalled = false;
        var callIndex = 0;

        var llm = new FakeLlmClient(messages =>
        {
            callIndex++;
            // If classification LLM is called, it would be call #1
            // with a system prompt containing "Classify"
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sysMsg.Contains("Classify"))
                classifyCalled = true;

            return "response text";
        });

        var mcp = new FakeMcpClient(returnValue: "tool result");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "You are a test assistant.");

        var result = await agent.ProcessAsync(userMessage);

        Assert.True(result.Success);
        // Explicit overrides should NOT call the LLM for classification
        Assert.False(classifyCalled, "Explicit prefix should bypass LLM classification");
    }

    [Fact]
    public async Task ClassificationFailure_FallsBackToCasual()
    {
        // LLM throws on EVERY call — classification fails, falls back to casual.
        // The casual path also fails, but the error is caught at the top level.
        var callCount = 0;
        var llm = new FakeLlmClient(_ =>
        {
            callCount++;
            // Fail classification (call 1), succeed on casual response (call 2+)
            if (callCount == 1)
                throw new HttpRequestException("Connection refused");
            return "Hello! I'm doing well.";
        });

        var mcp = new FakeMcpClient(returnValue: "");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "You are a test assistant.");

        var result = await agent.ProcessAsync("hey there!");

        // Should succeed — classification failure falls back to casual
        Assert.True(result.Success);
        Assert.Empty(result.ToolCallsMade);
    }

    [Fact]
    public async Task GarbageLlmClassification_DefaultsToCasual()
    {
        var callIndex = 0;
        var llm = new FakeLlmClient(_ =>
        {
            callIndex++;
            if (callIndex == 1) return "banana pancake rainbow";  // Nonsense classification
            return "Casual response here.";
        });

        var mcp = new FakeMcpClient(returnValue: "");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "You are a test assistant.");

        var result = await agent.ProcessAsync("hey thadds!");

        Assert.True(result.Success);
        Assert.Empty(result.ToolCallsMade);  // Casual = no tools
    }
}

#endregion

#region ── Search Query + Recency Extraction ─────────────────────────────

public class SearchQueryExtractionTests
{

    [Fact]
    public async Task ExtractsQuery_AndRecency_FromLlm()
    {
        var callIndex = 0;
        var llm = new FakeLlmClient(messages =>
        {
            callIndex++;
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            // Call 1 = classification → "search"
            if (sysMsg.Contains("Classify")) return "search";

            // Call 2 = query extraction → "stock market | day"
            if (sysMsg.Contains("QUERY") && sysMsg.Contains("RECENCY"))
                return "stock market | day";

            // Call 3+ = summary
            return "The stock market rallied today.";
        });

        var mcp = new FakeMcpClient(returnValue: "fake search results");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("hey what happened with the stock market today?");

        Assert.True(result.Success);

        // Verify the tool was called with recency
        var toolCall = result.ToolCallsMade.FirstOrDefault(
            t => t.ToolName.Contains("search", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(toolCall);
        Assert.Contains("\"recency\"", toolCall.Arguments);
        Assert.Contains("day", toolCall.Arguments);
    }

    [Fact]
    public async Task NoRecencyHint_DefaultsToAny()
    {
        var callIndex = 0;
        var llm = new FakeLlmClient(messages =>
        {
            callIndex++;
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify")) return "search";
            if (sysMsg.Contains("QUERY")) return "quantum computing";  // No pipe, no recency
            return "Summary of quantum computing.";
        });

        var mcp = new FakeMcpClient(returnValue: "search results");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("tell me about quantum computing");

        Assert.True(result.Success);

        var toolCall = result.ToolCallsMade.FirstOrDefault(
            t => t.ToolName.Contains("search", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(toolCall);
        // Default recency should be "any"
        Assert.Contains("\"recency\"", toolCall.Arguments);
        Assert.Contains("any", toolCall.Arguments);
    }

    [Theory]
    [InlineData("news today",             "day")]
    [InlineData("headlines this morning",  "day")]
    [InlineData("events this week",        "week")]
    [InlineData("updates past month",      "month")]
    [InlineData("latest research",         "any")]
    public async Task RecencyFallback_DetectsKeywords_WhenLlmSkipped(
        string shortQuery, string expectedRecency)
    {
        // Short + no question mark → LLM extraction is skipped, fallback used.
        var callIndex = 0;
        var llm = new FakeLlmClient(messages =>
        {
            callIndex++;
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

            if (sysMsg.Contains("Classify")) return "search";
            // Query extraction shouldn't be called for short clean inputs
            if (sysMsg.Contains("QUERY")) return $"{shortQuery} | any";
            return "Summary.";
        });

        var mcp = new FakeMcpClient(returnValue: "results");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        // Use /search to bypass classification, keep input short + clean
        var result = await agent.ProcessAsync($"/search {shortQuery}");

        Assert.True(result.Success);

        var toolCall = result.ToolCallsMade.FirstOrDefault(
            t => t.ToolName.Contains("search", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(toolCall);
        Assert.Contains($"\"{expectedRecency}\"", toolCall.Arguments);
    }
}

#endregion

#region ── Full Flow: Classify → Tool → Interpret ────────────────────────

public class AgentFlowTests
{

    [Fact]
    public async Task WebLookup_CallsToolThenSummarizes()
    {
        var llmCalls = new List<string>();
        var llm = new FakeLlmClient(messages =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            llmCalls.Add(sysMsg.Contains("Classify") ? "classify"
                       : sysMsg.Contains("QUERY")    ? "extract"
                       :                                "summarize");

            if (sysMsg.Contains("Classify")) return "search";
            if (sysMsg.Contains("QUERY")) return "latest news | day";
            return "Here's what's happening in the news today.";
        });

        var mcp = new FakeMcpClient(returnValue: "Source 1: Big headline...\nSource 2: Another story...");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("hey thadds! whats going on in the news today?");

        Assert.True(result.Success);

        // Verify the 3-step flow: classify → extract → summarize
        Assert.Equal("classify", llmCalls[0]);
        Assert.Equal("extract",  llmCalls[1]);
        Assert.Equal("summarize", llmCalls[2]);

        // Tool was called
        Assert.Single(result.ToolCallsMade);
        Assert.True(result.ToolCallsMade[0].Success);

        // Final response is the summary, not raw tool output
        Assert.Contains("news", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CasualChat_NoToolCalls()
    {
        var llm = new FakeLlmClient(messages =>
        {
            var sysMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sysMsg.Contains("Classify")) return "chat";
            return "Hey! Good to see you. How can I help?";
        });

        var mcp = new FakeMcpClient(returnValue: "should not be called");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("hey there, how are you?");

        Assert.True(result.Success);
        Assert.Empty(result.ToolCallsMade);
        Assert.Contains("Hey", result.Text);
    }

    [Fact]
    public async Task EmptyMessage_ReturnsError()
    {
        var llm = new FakeLlmClient(_ => "I'm here if you need me!");
        var mcp = new FakeMcpClient(returnValue: "");
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(llm, mcp, audit, "Test assistant.");

        var result = await agent.ProcessAsync("");

        // Empty messages are rejected early — no LLM or tool calls
        Assert.False(result.Success);
        Assert.Empty(result.ToolCallsMade);
        Assert.Contains("Empty", result.Text, StringComparison.OrdinalIgnoreCase);
    }
}

#endregion

#region ── Test Doubles ──────────────────────────────────────────────────

/// <summary>
/// Fake LLM client that returns canned responses based on the input
/// messages. The response function receives the full message list and
/// returns the assistant's content string.
/// </summary>
internal sealed class FakeLlmClient : ILlmClient
{
    private readonly Func<IReadOnlyList<ChatMessage>, string> _respond;

    public FakeLlmClient(Func<IReadOnlyList<ChatMessage>, string> respond)
        => _respond = respond;

    public FakeLlmClient(string fixedResponse)
        : this(_ => fixedResponse) { }

    public Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new LlmResponse
        {
            IsComplete   = true,
            Content      = _respond(messages),
            FinishReason = "stop"
        });
    }

    public Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokensOverride,
        CancellationToken cancellationToken = default)
    {
        // Delegate to the same handler — max_tokens isn't relevant for fakes
        return ChatAsync(messages, tools, cancellationToken);
    }

    public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>("fake-test-model");
}

/// <summary>
/// Fake MCP tool client that returns a fixed result for any tool call.
/// Tracks calls for assertion.
/// </summary>
internal sealed class FakeMcpClient : IMcpToolClient
{
    private readonly string _returnValue;
    public List<(string Tool, string Args)> Calls { get; } = [];

    public FakeMcpClient(string returnValue) => _returnValue = returnValue;

    public Task<string> CallToolAsync(
        string toolName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        Calls.Add((toolName, argumentsJson));
        return Task.FromResult(_returnValue);
    }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<McpToolInfo>>([]);
    }
}

#endregion
