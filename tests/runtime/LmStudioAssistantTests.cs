using Microsoft.Extensions.Logging.Abstractions;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Routing;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Events;
using Thaddeus.Runtime.Settings;
using Thaddeus.Runtime.Tools;
using Thaddeus.SharedTypes;
using Xunit;
using LlmChatMessage = SirThaddeus.LlmClient.ChatMessage;
using ChatMessage = Thaddeus.SharedTypes.ChatMessage;

namespace Thaddeus.Runtime.Tests;

public class LmStudioAssistantTests : IDisposable
{
    private readonly string _root;

    public LmStudioAssistantTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "thaddeus-lm-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        public string Reply { get; init; } = "ok";
        public Exception? Throw { get; init; }
        public List<IReadOnlyList<LlmChatMessage>> Calls { get; } = new();
        public List<IReadOnlyList<ToolDefinition>> ToolCalls { get; } = new();

        public Task<LlmResponse> ChatAsync(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<ToolDefinition>? tools = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(messages);
            ToolCalls.Add(tools ?? Array.Empty<ToolDefinition>());
            if (Throw is not null) throw Throw;
            return Task.FromResult(new LlmResponse { IsComplete = true, Content = Reply });
        }

        public Task<LlmResponse> ChatAsync(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<ToolDefinition>? tools, int maxTokensOverride, CancellationToken cancellationToken = default)
            => ChatAsync(messages, tools, cancellationToken);

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("fake");
    }

    private sealed class FakeMcpClient : IMcpToolClient
    {
        public IReadOnlyList<McpToolInfo> Tools { get; init; } = Array.Empty<McpToolInfo>();

        public Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Tools);
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        private SettingsDocument _doc = SettingsDocument.Defaults();
        public Task<SettingsDocument> GetAsync(CancellationToken ct) => Task.FromResult(_doc);
        public Task<SettingsDocument> ReplaceAsync(SettingsDocument document, CancellationToken ct)
        {
            _doc = document;
            Changed?.Invoke(document);
            return Task.FromResult(document);
        }
        public event Action<SettingsDocument>? Changed;
    }

    private (JsonFileThreadStore store, LmStudioAssistant assistant, List<RuntimeEvent<object?>> captured, FakeLlmClient fake)
        NewSut(
            string reply = "hello world from model",
            Exception? throwOnCall = null,
            IReadOnlyList<McpToolInfo>? tools = null,
            bool offlineMode = false)
    {
        var store = new JsonFileThreadStore(_root, NullLogger<JsonFileThreadStore>.Instance);
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var publisher = new ChatTurnPublisher(bus);
        var fake = new FakeLlmClient { Reply = reply, Throw = throwOnCall };
        var mcp = new FakeMcpClient { Tools = tools ?? Array.Empty<McpToolInfo>() };
        var gate = new ToolPermissionGate(new FakeSettingsStore(), bus, NullLogger<ToolPermissionGate>.Instance);
        var audit = new TestAuditLogger();
        var assistant = new LmStudioAssistant(fake, mcp, gate, store, publisher, audit, NullLogger<LmStudioAssistant>.Instance)
        {
            DeltaDelay = TimeSpan.Zero,
            OfflineMode = offlineMode,
        };
        var captured = new List<RuntimeEvent<object?>>();
        bus.Subscribe((evt, _) => { captured.Add(evt); return Task.CompletedTask; });
        return (store, assistant, captured, fake);
    }

    [Fact]
    public async Task RespondAsync_streams_model_reply_and_persists()
    {
        var (store, assistant, captured, fake) = NewSut(reply: "hello world from model");
        var thread = await store.CreateAsync("t", CancellationToken.None);
        await store.AppendMessageAsync(thread.Id,
            new ChatMessage("u1", ChatRole.User, "hi", DateTimeOffset.UtcNow), CancellationToken.None);

        var msg = await assistant.RespondAsync(thread.Id, "hi", CancellationToken.None);

        Assert.Equal("hello world from model", msg.Text);
        Assert.Equal(ChatTurnEvents.Start, captured[0].Type);
        Assert.Equal(ChatTurnEvents.Complete, captured[^1].Type);
        var assembled = string.Concat(captured
            .Where(e => e.Type == ChatTurnEvents.Delta)
            .Select(e => ((ChatTurnDelta)e.Payload!).Text));
        Assert.Equal(msg.Text, assembled);

        // history is sent: system + the user turn we appended
        var sent = fake.Calls.Single();
        Assert.Equal("system", sent[0].Role);
        Assert.Contains(sent, m => m.Role == "user" && m.Content == "hi");

        var refreshed = await store.GetAsync(thread.Id, CancellationToken.None);
        Assert.Equal(2, refreshed!.Messages.Count);
        Assert.Equal(ChatRole.Assistant, refreshed.Messages[1].Role);
    }

    [Fact]
    public async Task RespondAsync_returns_error_text_when_llm_throws()
    {
        var (store, assistant, captured, _) = NewSut(throwOnCall: new InvalidOperationException("boom"));
        var thread = await store.CreateAsync("t", CancellationToken.None);

        var msg = await assistant.RespondAsync(thread.Id, "hi", CancellationToken.None);

        Assert.Contains("LLM error", msg.Text);
        Assert.Contains("boom", msg.Text);
        Assert.Equal(ChatTurnEvents.Complete, captured[^1].Type);
    }

    [Fact]
    public async Task RespondAsync_propagates_transport_exceptions_for_router_fallback()
    {
        var (store, assistant, _, _) = NewSut(throwOnCall: new HttpRequestException("offline"));
        var thread = await store.CreateAsync("t", CancellationToken.None);

        // Transport failures must bubble so AssistantRouter can fall back to
        // the stub for the same turn.
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            assistant.RespondAsync(thread.Id, "hi", CancellationToken.None));
    }

    [Fact]
    public async Task RespondAsync_filters_web_tools_when_offline_mode_is_on()
    {
        var tools = new[]
        {
            new McpToolInfo
            {
                Name = "web_search",
                Description = "Search the web",
                InputSchema = new { type = "object" },
            },
            new McpToolInfo
            {
                Name = "memory_retrieve",
                Description = "Search local memory",
                InputSchema = new { type = "object" },
            },
        };
        var (store, assistant, _, fake) = NewSut(tools: tools, offlineMode: true);
        var thread = await store.CreateAsync("t", CancellationToken.None);

        await assistant.RespondAsync(thread.Id, "what is current?", CancellationToken.None);

        var sentTools = fake.ToolCalls.Single();
        Assert.DoesNotContain(sentTools, t => t.Function?.Name == "web_search");
        Assert.Contains(sentTools, t => t.Function?.Name == "memory_retrieve");
        Assert.Contains("Offline mode is ON", fake.Calls.Single()[0].Content);
    }

    [Fact]
    public void BuildTurnPipeline_uses_audited_mcp_as_the_only_permission_boundary()
    {
        var (_, assistant, _, _) = NewSut();

        var pipeline = assistant.BuildTurnPipeline(
            new FakeMcpClient(),
            NullChatEventSink.Instance);

        var stepNames = pipeline.Steps.Select(s => s.Name).ToList();
        var toolLoopIndex = stepNames.IndexOf("ToolLoop");

        Assert.True(toolLoopIndex >= 0, "ToolLoop step must be present in the chat pipeline.");
        Assert.Equal(1, stepNames.Count(name => name == "ToolLoop"));

        var permissionGateField = typeof(SirThaddeus.Agent.Pipeline.Steps.ToolLoopStep)
            .GetField("_permissionGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(permissionGateField);
        Assert.Null(permissionGateField.GetValue(pipeline.Steps[toolLoopIndex]));
    }

    // ── Production tool exposure (roadmap 4.3 step 3) ─────────────────

    [Fact]
    public async Task RespondAsync_exposes_calculator_and_python_eval_to_the_model_on_a_plain_chat_turn()
    {
        // calculator and python_eval are registered as always-allowed "meta"
        // tools (ToolGroupPolicy / ToolCapabilityRegistry). A normal chat turn
        // with no footman must hand both to the model.
        var tools = new[]
        {
            McpTool("calculator"),
            McpTool("python_eval"),
            McpTool("memory_retrieve"),
        };
        var (store, assistant, _, fake) = NewSut(tools: tools);
        var thread = await store.CreateAsync("t", CancellationToken.None);

        await assistant.RespondAsync(thread.Id, "hello there", CancellationToken.None);

        var sentTools = fake.ToolCalls.Single();
        Assert.Contains(sentTools, t => t.Function?.Name == "calculator");
        Assert.Contains(sentTools, t => t.Function?.Name == "python_eval");
    }

    [Fact]
    public async Task RespondAsync_honors_harness_tool_allowlist()
    {
        var previous = Environment.GetEnvironmentVariable("ST_HARNESS_ALLOWED_TOOLS");
        Environment.SetEnvironmentVariable("ST_HARNESS_ALLOWED_TOOLS", "wiki_root_create");
        try
        {
            var tools = new[]
            {
                McpTool("wiki_root_create"),
                McpTool("python_eval"),
            };
            var (store, assistant, _, fake) = NewSut(tools: tools);
            var thread = await store.CreateAsync("t", CancellationToken.None);

            await assistant.RespondAsync(thread.Id, "create the requested wiki", CancellationToken.None);

            var sentTools = fake.ToolCalls.Single();
            Assert.Single(sentTools);
            Assert.Equal("wiki_root_create", sentTools[0].Function.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ST_HARNESS_ALLOWED_TOOLS", previous);
        }
    }

    [Fact]
    public async Task RespondAsync_keeps_calculator_and_python_eval_when_footman_narrows_to_chat()
    {
        // The footman classifies a greeting as AgentState.Chat, which narrows
        // the tool list to the Chat family (MemoryRead | Meta). calculator and
        // python_eval map to ToolCapability.Meta, so they must survive the
        // narrowing — the same always-allowed class that keeps meta tools
        // reachable regardless of intent. web_search (WebSearch) is dropped.
        var tools = new[]
        {
            McpTool("calculator"),
            McpTool("python_eval"),
            McpTool("web_search"),
            McpTool("memory_retrieve"),
        };
        var (store, assistant, _, fake) = NewSutWithFootman(
            footman: new StubFootman(AgentState.Chat, confidence: 0.95),
            tools: tools);
        var thread = await store.CreateAsync("t", CancellationToken.None);

        await assistant.RespondAsync(thread.Id, "hi", CancellationToken.None);

        var sentTools = fake.ToolCalls.Single();
        Assert.Contains(sentTools, t => t.Function?.Name == "calculator");
        Assert.Contains(sentTools, t => t.Function?.Name == "python_eval");
        Assert.DoesNotContain(sentTools, t => t.Function?.Name == "web_search");
    }

    private static McpToolInfo McpTool(string name) => new()
    {
        Name = name,
        Description = name,
        InputSchema = new { type = "object" },
    };

    private (JsonFileThreadStore store, LmStudioAssistant assistant, List<RuntimeEvent<object?>> captured, FakeLlmClient fake)
        NewSutWithFootman(IFootmanRouter footman, IReadOnlyList<McpToolInfo> tools)
    {
        var store = new JsonFileThreadStore(_root, NullLogger<JsonFileThreadStore>.Instance);
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var publisher = new ChatTurnPublisher(bus);
        var fake = new FakeLlmClient { Reply = "ok" };
        var mcp = new FakeMcpClient { Tools = tools };
        var gate = new ToolPermissionGate(new FakeSettingsStore(), bus, NullLogger<ToolPermissionGate>.Instance);
        var audit = new TestAuditLogger();
        var assistant = new LmStudioAssistant(fake, mcp, gate, store, publisher, audit, NullLogger<LmStudioAssistant>.Instance)
        {
            DeltaDelay = TimeSpan.Zero,
            Footman = footman,
        };
        var captured = new List<RuntimeEvent<object?>>();
        bus.Subscribe((evt, _) => { captured.Add(evt); return Task.CompletedTask; });
        return (store, assistant, captured, fake);
    }

    private sealed class StubFootman : IFootmanRouter
    {
        private readonly RoutingDecision _decision;

        public StubFootman(AgentState state, double confidence)
        {
            _decision = new RoutingDecision
            {
                SchemaVersion = 1,
                RequestId = "stub",
                NextState = state,
                Confidence = confidence,
                Abstain = false,
                ReasonCode = "heuristic_chat",
            };
        }

        public Task<RoutingDecision> RouteAsync(string userMessage, RoutingFeatures features, CancellationToken cancellationToken = default)
            => Task.FromResult(_decision);
    }
}
