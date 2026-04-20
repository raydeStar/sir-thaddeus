using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Events;
using Thaddeus.Runtime.Settings;
using Thaddeus.SharedTypes;
using Xunit;

namespace Thaddeus.Runtime.Tests;

public class AssistantRouterTests : IDisposable
{
    private readonly string _root;

    public AssistantRouterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "thaddeus-router-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class InMemorySettings : ISettingsStore
    {
        private SettingsDocument _doc;
        public InMemorySettings(SettingsDocument doc) { _doc = doc; }
        public Task<SettingsDocument> GetAsync(CancellationToken ct) => Task.FromResult(_doc);
        public Task<SettingsDocument> ReplaceAsync(SettingsDocument document, CancellationToken ct)
        {
            _doc = document;
            Changed?.Invoke(document);
            return Task.FromResult(document);
        }
        public event Action<SettingsDocument>? Changed;
    }

    private sealed class TaggedAssistant : IAssistant
    {
        public string Tag { get; }
        public int Calls { get; private set; }
        public Exception? Throw { get; init; }
        public TaggedAssistant(string tag) { Tag = tag; }
        public Task<ChatMessage> RespondAsync(string threadId, string userText, CancellationToken ct)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(new ChatMessage($"m_{Tag}", ChatRole.Assistant, $"{Tag}:{userText}", DateTimeOffset.UtcNow));
        }
    }

    private (StubAssistant stub, JsonFileThreadStore store) NewDeps()
    {
        var store = new JsonFileThreadStore(_root, NullLogger<JsonFileThreadStore>.Instance);
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var publisher = new ChatTurnPublisher(bus);
        var stub = new StubAssistant(store, publisher, NullLogger<StubAssistant>.Instance)
        {
            DeltaDelay = TimeSpan.Zero,
        };
        return (stub, store);
    }

    private static SettingsDocument DocWith(string provider, string? baseUrl = "http://x", string? model = "m")
        => SettingsDocument.Defaults() with { Llm = new LlmSettings(provider, model ?? string.Empty, baseUrl, null) };

    [Fact]
    public async Task RespondAsync_uses_stub_when_provider_is_stub()
    {
        var (stub, store) = NewDeps();
        var settings = new InMemorySettings(DocWith("stub"));
        var lm = new TaggedAssistant("LM");
        using var router = new AssistantRouter(settings, stub, _ => lm, NullLogger<AssistantRouter>.Instance);
        var thread = await store.CreateAsync("t", CancellationToken.None);

        var msg = await router.RespondAsync(thread.Id, "hi", CancellationToken.None);

        Assert.Equal(0, lm.Calls);
        Assert.DoesNotContain("LM:", msg.Text);
    }

    [Fact]
    public async Task RespondAsync_uses_stub_when_baseurl_blank()
    {
        var (stub, store) = NewDeps();
        var settings = new InMemorySettings(DocWith("ollama", baseUrl: ""));
        var lm = new TaggedAssistant("LM");
        using var router = new AssistantRouter(settings, stub, _ => lm, NullLogger<AssistantRouter>.Instance);
        var thread = await store.CreateAsync("t", CancellationToken.None);

        await router.RespondAsync(thread.Id, "hi", CancellationToken.None);

        Assert.Equal(0, lm.Calls);
    }

    [Fact]
    public async Task RespondAsync_dispatches_to_lm_when_configured()
    {
        var (stub, store) = NewDeps();
        var settings = new InMemorySettings(DocWith("ollama"));
        var lm = new TaggedAssistant("LM");
        using var router = new AssistantRouter(settings, stub, _ => lm, NullLogger<AssistantRouter>.Instance);
        var thread = await store.CreateAsync("t", CancellationToken.None);

        var msg = await router.RespondAsync(thread.Id, "hi", CancellationToken.None);

        Assert.Equal(1, lm.Calls);
        Assert.Equal("LM:hi", msg.Text);
    }

    [Fact]
    public async Task RespondAsync_falls_back_to_stub_on_transport_error()
    {
        var (stub, store) = NewDeps();
        var settings = new InMemorySettings(DocWith("ollama"));
        var lm = new TaggedAssistant("LM") { Throw = new HttpRequestException("offline") };
        using var router = new AssistantRouter(settings, stub, _ => lm, NullLogger<AssistantRouter>.Instance);
        var thread = await store.CreateAsync("t", CancellationToken.None);

        var msg = await router.RespondAsync(thread.Id, "hi", CancellationToken.None);

        Assert.Equal(1, lm.Calls);
        Assert.Equal(ChatRole.Assistant, msg.Role);
        Assert.DoesNotContain("LM:", msg.Text); // stub reply, not LM
    }

    [Fact]
    public async Task RespondAsync_falls_back_to_stub_when_factory_throws()
    {
        var (stub, store) = NewDeps();
        var settings = new InMemorySettings(DocWith("ollama"));
        var built = 0;
        using var router = new AssistantRouter(settings, stub,
            _ => { built++; throw new InvalidOperationException("bad config"); },
            NullLogger<AssistantRouter>.Instance);
        var thread = await store.CreateAsync("t", CancellationToken.None);

        var msg = await router.RespondAsync(thread.Id, "hi", CancellationToken.None);

        Assert.Equal(1, built);
        Assert.Equal(ChatRole.Assistant, msg.Role);
    }
}
