using Microsoft.Extensions.Logging.Abstractions;
using SirThaddeus.Agent;
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

        public Task<LlmResponse> ChatAsync(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<ToolDefinition>? tools = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(messages);
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
        public Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>(Array.Empty<McpToolInfo>());
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
        NewSut(string reply = "hello world from model", Exception? throwOnCall = null)
    {
        var store = new JsonFileThreadStore(_root, NullLogger<JsonFileThreadStore>.Instance);
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var publisher = new ChatTurnPublisher(bus);
        var fake = new FakeLlmClient { Reply = reply, Throw = throwOnCall };
        var mcp = new FakeMcpClient();
        var gate = new ToolPermissionGate(new FakeSettingsStore(), bus, NullLogger<ToolPermissionGate>.Instance);
        var assistant = new LmStudioAssistant(fake, mcp, gate, store, publisher, NullLogger<LmStudioAssistant>.Instance)
        {
            DeltaDelay = TimeSpan.Zero,
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
}
