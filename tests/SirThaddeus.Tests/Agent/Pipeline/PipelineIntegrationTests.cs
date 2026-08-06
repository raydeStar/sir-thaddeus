using SirThaddeus.Agent;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline;

/// <summary>
/// Integration-style tests that compose the full CLI-equivalent pipeline
/// (the same 10 steps <c>BuildPipelineBackedOrchestrator</c> wires) with
/// fake collaborators (FakeLlm, FakeMcp, recorders). Catches regressions
/// where a step mutates context in a way that breaks the next step, or
/// where event emission is out of order. Fast — no real LLM, no real MCP.
/// </summary>
[Collection(RoutingLatencyEnvironmentCollection.Name)]
public class PipelineIntegrationTests
{
    [Fact]
    public async Task Utility_match_terminates_pipeline_before_llm_call()
    {
        // 350F to C is a deterministic high-confidence match. The
        // pipeline must terminate without touching the fake LLM.
        var llm = new CountingLlm(_ => throw new InvalidOperationException("utility match should bypass LLM"));
        var mcp = new StubMcp(Array.Empty<string>());
        var orch = BuildOrchestrator(llm, mcp);

        var response = await orch.ProcessAsync("350F to C");

        Assert.True(response.Success);
        Assert.Contains("°F", response.Text);
        Assert.Contains("°C", response.Text);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task Non_utility_turn_calls_llm_and_emits_lifecycle_events()
    {
        // Normal turn: LLM replies with plain text. Pipeline should:
        //   - call LLM once (no tool calls)
        //   - post-process the draft
        //   - terminate via composer with the (sanitized) reply
        // We don't invoke the sink directly in the orchestrator facade,
        // but the ToolLoopStep still emits nothing because there are no
        // tool calls. Footman fires (null router → skipped).
        var llm = new CountingLlm(_ => new LlmResponse
        {
            IsComplete = true,
            Content = "Hello, friend.",
            FinishReason = "stop",
        });
        var mcp = new StubMcp(Array.Empty<string>());
        var orch = BuildOrchestrator(llm, mcp);

        var response = await orch.ProcessAsync("hi there");

        Assert.True(response.Success);
        Assert.Equal("Hello, friend.", response.Text);
        Assert.Equal(1, llm.CallCount);
    }

    [Fact]
    public async Task Tool_loop_decision_telemetry_is_content_free_and_behavior_preserving()
    {
        const string finalText = "SECRET-FINAL-TEXT";
        var events = new List<string>();
        var prior = Environment.GetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE");

        try
        {
            Environment.SetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE", "1");
            var llm = new QueuedLlm(
                LlmReplyFactory.Tool("web_search", "{\"q\":\"private query\"}"),
                LlmReplyFactory.Final(finalText));
            var orch = BuildOrchestrator(
                llm,
                new StubMcp(["web_search"]),
                log: (name, detail) => events.Add($"{name} {detail}"));

            var response = await orch.ProcessAsync("private user prompt");

            Assert.Equal(finalText, response.Text);
            Assert.Single(response.ToolCallsMade);
            Assert.Contains(events, line =>
                line.Contains("TOOL_LOOP_DECISION", StringComparison.Ordinal) &&
                line.Contains("decision=tool_calls", StringComparison.Ordinal) &&
                line.Contains("effective_tool_calls=1", StringComparison.Ordinal));
            Assert.Contains(events, line =>
                line.Contains("TOOL_LOOP_DECISION", StringComparison.Ordinal) &&
                line.Contains("decision=final_text", StringComparison.Ordinal));
            Assert.DoesNotContain(events, line =>
                line.Contains(finalText, StringComparison.Ordinal) ||
                line.Contains("private query", StringComparison.Ordinal) ||
                line.Contains("private user prompt", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE", prior);
        }
    }

    [Fact]
    public async Task Tool_call_triggers_paired_start_and_completed_events_via_sink()
    {
        // When the model asks for a tool, the sink must receive a
        // start/complete pair bracketing the MCP call. This validates
        // the ToolLoopStep → sink wiring end-to-end.
        var llm = new QueuedLlm(
            LlmReplyFactory.Tool("web_search", "{\"q\":\"cats\"}"),
            LlmReplyFactory.Final("cats are furry"));
        var mcp = new StubMcp(new[] { "web_search" });
        var sink = new CapturingChatEventSink();
        var orch = BuildOrchestrator(llm, mcp, sink: sink);

        var response = await orch.ProcessAsync("tell me about cats");

        Assert.True(response.Success);
        Assert.Equal("cats are furry", response.Text);
        Assert.Single(response.ToolCallsMade);

        // Exactly one start/complete pair with matching activity ids.
        var starts = sink.SnapshotOfKind("tool.started");
        var completes = sink.SnapshotOfKind("tool.completed");
        var start = Assert.Single(starts);
        var completed = Assert.Single(completes);
        Assert.Equal(start.ActivityId, completed.ActivityId);
        Assert.Equal("web_search", start.Tool);
        Assert.True(completed.Ok);
    }

    [Fact]
    public async Task Logic_puzzle_prompts_inject_scaffold_into_system_message()
    {
        // The car-wash puzzle is a high-confidence logic match. The
        // LogicPuzzleScaffoldStep should append OrchestratorPrompts's
        // decomposition suffix to the system message. We verify by
        // capturing what the LLM actually receives.
        string? observedSystem = null;
        var llm = new CountingLlm(messages =>
        {
            observedSystem = messages.FirstOrDefault(m => m.Role == "system")?.Content;
            return new LlmResponse { IsComplete = true, Content = "Drive.", FinishReason = "stop" };
        });
        var mcp = new StubMcp(Array.Empty<string>());
        var orch = BuildOrchestrator(llm, mcp);

        await orch.ProcessAsync("The car is dirty and needs to be washed. 50m away. Should I walk or drive?");

        Assert.NotNull(observedSystem);
        Assert.Contains("LOGIC PUZZLE MODE", observedSystem);
    }

    [Fact]
    public async Task Memory_context_block_appears_in_system_message_when_provider_returns_pack()
    {
        // Verifies MemoryContextStep correctly appends the REMEMBERED
        // CONTEXT block to the system message before the LLM call.
        var provider = new StubMemoryProvider("user name: Mark\nhome: Olympia, WA");
        string? observedSystem = null;
        var llm = new CountingLlm(messages =>
        {
            observedSystem = messages.FirstOrDefault(m => m.Role == "system")?.Content;
            return new LlmResponse { IsComplete = true, Content = "Hi Mark.", FinishReason = "stop" };
        });
        var orch = BuildOrchestrator(llm, new StubMcp(Array.Empty<string>()), memoryProvider: provider);

        await orch.ProcessAsync("hi");

        Assert.NotNull(observedSystem);
        Assert.Contains("REMEMBERED CONTEXT", observedSystem);
        Assert.Contains("Olympia, WA", observedSystem);
    }

    [Fact]
    public async Task History_persists_across_multiple_turns()
    {
        // Turn 2's LLM call should see turn 1's user message + assistant
        // reply in the messages list — this is the primary reason for
        // the facade holding state externally.
        var receivedCalls = new List<IReadOnlyList<ChatMessage>>();
        var llm = new CountingLlm(messages =>
        {
            receivedCalls.Add(messages);
            return new LlmResponse { IsComplete = true, Content = "ok", FinishReason = "stop" };
        });
        var orch = BuildOrchestrator(llm, new StubMcp(Array.Empty<string>()));

        await orch.ProcessAsync("first turn");
        await orch.ProcessAsync("second turn");

        Assert.Equal(2, receivedCalls.Count);
        // Call 1: system + user("first turn")  → 2 messages
        Assert.Equal(2, receivedCalls[0].Count);
        // Call 2: system + user("first") + assistant("ok") + user("second") → 4 messages
        Assert.Equal(4, receivedCalls[1].Count);
        Assert.Equal("first turn", receivedCalls[1][1].Content);
        Assert.Equal("ok", receivedCalls[1][2].Content);
        Assert.Equal("second turn", receivedCalls[1][3].Content);
    }

    [Fact]
    public async Task AutoMemory_fires_user_extraction_and_both_chunk_writes_per_turn()
    {
        // Fire-and-forget contract: the step invokes the extractor
        // before returning. We verify both the structured extraction
        // call and the two chunk writes (user + assistant) landed.
        var extractor = new RecordingExtractor();
        var llm = new CountingLlm(_ => new LlmResponse { IsComplete = true, Content = "great!", FinishReason = "stop" });
        var orch = BuildOrchestrator(
            llm,
            new StubMcp(Array.Empty<string>()),
            extractor: extractor);

        await orch.ProcessAsync("remember my favorite color is blue");

        Assert.Single(extractor.Extractions);
        Assert.Equal("remember my favorite color is blue", extractor.Extractions[0].UserMessage);
        // Two chunks — user + assistant.
        Assert.Equal(2, extractor.Chunks.Count);
        Assert.Contains(extractor.Chunks, c => c.Role == "user");
        Assert.Contains(extractor.Chunks, c => c.Role == "assistant");
    }

    [Fact]
    public async Task ResetConversation_clears_history_and_next_turn_starts_fresh()
    {
        var receivedCalls = new List<IReadOnlyList<ChatMessage>>();
        var llm = new CountingLlm(messages =>
        {
            receivedCalls.Add(messages);
            return new LlmResponse { IsComplete = true, Content = "ok", FinishReason = "stop" };
        });
        var orch = BuildOrchestrator(llm, new StubMcp(Array.Empty<string>()));

        await orch.ProcessAsync("first");
        await orch.ProcessAsync("second");
        orch.ResetConversation();
        await orch.ProcessAsync("third");

        // The third call should again see just [system, user("third")].
        Assert.Equal(2, receivedCalls[2].Count);
        Assert.Equal("third", receivedCalls[2][1].Content);
    }

    // ── composition ──────────────────────────────────────────────────

    private static PipelineBackedAgentOrchestrator BuildOrchestrator(
        ILlmClient llm,
        IMcpToolClient mcp,
        IChatEventSink? sink = null,
        IMemoryContextProvider? memoryProvider = null,
        IAutoMemoryExtractor? extractor = null,
        Action<string, string>? log = null)
    {
        var effectiveSink = sink ?? NullChatEventSink.Instance;

        var toolLoop = new ToolLoopStep(
            llm, mcp, effectiveSink,
            permissionGate: new AlwaysGrantGate(),
            maxRoundTrips: 6,
            log: log);

        var sanitize = new Func<TurnContext, string, string>((_, draft) => draft);

        var pipeline = new ChatPipeline(new ITurnStep[]
        {
            new UtilityFastPathStep(),
            new FeatureExtractorStep(),
            new LogicPuzzleScaffoldStep(),
            new MemoryContextStep(memoryProvider),
            new FootmanRouterStep(footman: null, effectiveSink),
            toolLoop,
            new PostProcessStep(sanitize, "PostProcess:Identity"),
            new AutoMemoryExtractStep(extractor),
            new ResponseComposerStep(),
        });

        return new PipelineBackedAgentOrchestrator(pipeline, mcp, systemPrompt: "You are Sir Thaddeus.");
    }

    // ── fakes ────────────────────────────────────────────────────────

    private sealed class CountingLlm : ILlmClient
    {
        private readonly Func<IReadOnlyList<ChatMessage>, LlmResponse> _handler;
        public int CallCount { get; private set; }

        public CountingLlm(Func<IReadOnlyList<ChatMessage>, LlmResponse> handler) { _handler = handler; }

        public Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_handler(messages));
        }

        public Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools, int maxTokensOverride, CancellationToken cancellationToken = default)
            => ChatAsync(messages, tools, cancellationToken);

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("fake");
    }

    private sealed class QueuedLlm : ILlmClient
    {
        private readonly Queue<LlmResponse> _replies;

        public QueuedLlm(params LlmResponse[] replies) { _replies = new(replies); }

        public Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_replies.Count > 0
                ? _replies.Dequeue()
                : new LlmResponse { IsComplete = true, Content = "(queue empty)", FinishReason = "stop" });
        }

        public Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools, int maxTokensOverride, CancellationToken cancellationToken = default)
            => ChatAsync(messages, tools, cancellationToken);

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("fake");
    }

    private static class LlmReplyFactory
    {
        public static LlmResponse Final(string text) => new() { IsComplete = true, Content = text, FinishReason = "stop" };
        public static LlmResponse Tool(string name, string args) => new()
        {
            IsComplete = false,
            FinishReason = "tool_calls",
            ToolCalls = new[]
            {
                new ToolCallRequest
                {
                    Id = "call_" + Guid.NewGuid().ToString("N")[..8],
                    Function = new FunctionCallDetails { Name = name, Arguments = args },
                },
            },
        };
    }

    private sealed class StubMcp : IMcpToolClient
    {
        private readonly IReadOnlyList<string> _toolNames;
        public StubMcp(IReadOnlyList<string> toolNames) { _toolNames = toolNames; }

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            var infos = _toolNames
                .Select(n => new McpToolInfo { Name = n, Description = n, InputSchema = new { } })
                .ToArray();
            return Task.FromResult<IReadOnlyList<McpToolInfo>>(infos);
        }

        public Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            => Task.FromResult("{\"ok\":true}");
    }

    private sealed class StubMemoryProvider : IMemoryContextProvider
    {
        private readonly string _packText;
        public StubMemoryProvider(string packText) { _packText = packText; }

        public Task<MemoryContextResult> GetContextAsync(MemoryContextRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryContextResult { PackText = _packText });
    }

    private sealed record ExtractionCall(string UserMessage, string? ProfileId, string TurnId);
    private sealed record ChunkCall(string Text, string? ConversationId, string TurnId, string Role);

    private sealed class RecordingExtractor : IAutoMemoryExtractor
    {
        public List<ExtractionCall> Extractions { get; } = new();
        public List<ChunkCall> Chunks { get; } = new();

        public void FireAndForgetExtraction(string userMessage, string? activeProfileId, string turnId)
            => Extractions.Add(new ExtractionCall(userMessage, activeProfileId, turnId));

        public void FireAndForgetConversationChunk(string text, string? conversationId, string turnId, string role)
            => Chunks.Add(new ChunkCall(text, conversationId, turnId, role));
    }
}
