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
        => SettingsDocument.Defaults() with
        {
            Llm = new LlmSettings(
                provider,
                model ?? string.Empty,
                baseUrl,
                null,
                MaxTokens: 2048,
                ContextWindowTokens: 8192,
                Temperature: 0.7)
        };

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
    public async Task RespondAsync_transport_error_reply_names_the_failure_instead_of_echoing()
    {
        // Echoing the user's own words back through the development stub reads
        // as a real answer, which is a false success: the user believes they
        // were served when nothing ran. The fallback must say what broke.
        var (stub, store) = NewDeps();
        var settings = new InMemorySettings(DocWith("ollama"));
        var lm = new TaggedAssistant("LM") { Throw = new HttpRequestException("offline") };
        using var router = new AssistantRouter(settings, stub, _ => lm, NullLogger<AssistantRouter>.Instance);
        var thread = await store.CreateAsync("t", CancellationToken.None);

        var msg = await router.RespondAsync(thread.Id, "what is the capital of France", CancellationToken.None);

        Assert.DoesNotContain("stubbed reply", msg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("later phase", msg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("what is the capital of France", msg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nothing was sent anywhere", msg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not reach", msg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Retry", msg.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RespondAsync_rejected_request_is_not_reported_as_an_unreachable_provider()
    {
        // A provider that answers with 4xx/5xx is running fine. Telling the
        // user "I could not reach the model" sends them to restart a healthy
        // server while the real complaint — which the provider stated plainly —
        // never reaches them.
        var (stub, store) = NewDeps();
        var settings = new InMemorySettings(DocWith("lmstudio"));
        var lm = new TaggedAssistant("LM")
        {
            Throw = new HttpRequestException(
                "LLM returned 400 (Bad Request): n_keep: 11553 >= n_ctx: 8192 SECRET-PROVIDER-TEXT",
                inner: null,
                statusCode: System.Net.HttpStatusCode.BadRequest),
        };
        using var router = new AssistantRouter(settings, stub, _ => lm, NullLogger<AssistantRouter>.Instance);
        var thread = await store.CreateAsync("t", CancellationToken.None);

        var msg = await router.RespondAsync(thread.Id, "hello", CancellationToken.None);

        Assert.DoesNotContain("could not reach", msg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("received the request", msg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("400", msg.Text, StringComparison.Ordinal);
        Assert.Contains("context window", msg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Retry", msg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET-PROVIDER-TEXT", msg.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was sent anywhere", msg.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RespondAsync_factory_failure_reply_names_the_failure_instead_of_echoing()
    {
        var (stub, store) = NewDeps();
        var settings = new InMemorySettings(DocWith("ollama"));
        using var router = new AssistantRouter(settings, stub,
            _ => throw new InvalidOperationException("bad config SECRET-FACTORY-TEXT"),
            NullLogger<AssistantRouter>.Instance);
        var thread = await store.CreateAsync("t", CancellationToken.None);

        var msg = await router.RespondAsync(thread.Id, "summarize my notes", CancellationToken.None);

        Assert.DoesNotContain("stubbed reply", msg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("summarize my notes", msg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be initialized", msg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Retry", msg.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET-FACTORY-TEXT", msg.Text, StringComparison.Ordinal);
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

    [Fact]
    public void ResolveGatekeeperPolicy_SharedEndpointDifferentModel_UsesSeparateLlm()
    {
        var llm = LlmWithGatekeeper(
            primaryModel: "primary-large",
            gatekeeperModel: "footman-small",
            gatekeeperBaseUrl: "http://localhost:1234",
            reusePrimaryForSharedEndpoint: true);

        var policy = AssistantRouter.ResolveGatekeeperPolicy(llm);

        Assert.Equal(AssistantRouter.GatekeeperPolicyMode.SeparateLlm, policy.Mode);
        Assert.True(policy.AllowsHelperLlm);
    }

    [Fact]
    public void ResolveGatekeeperPolicy_SharedEndpointSameModel_UsesSharedPrimary()
    {
        var llm = LlmWithGatekeeper(
            primaryModel: "primary-large",
            gatekeeperModel: "primary-large",
            gatekeeperBaseUrl: "http://localhost:1234");

        var policy = AssistantRouter.ResolveGatekeeperPolicy(llm);

        Assert.Equal(AssistantRouter.GatekeeperPolicyMode.SharedPrimary, policy.Mode);
        Assert.True(policy.AllowsHelperLlm);
    }

    [Fact]
    public void ResolveGatekeeperPolicy_SeparateEndpoint_UsesSeparateLlm()
    {
        var llm = LlmWithGatekeeper(
            primaryModel: "primary-large",
            gatekeeperModel: "footman-small",
            gatekeeperBaseUrl: "http://localhost:2345");

        var policy = AssistantRouter.ResolveGatekeeperPolicy(llm);

        Assert.Equal(AssistantRouter.GatekeeperPolicyMode.SeparateLlm, policy.Mode);
        Assert.True(policy.AllowsHelperLlm);
    }

    [Fact]
    public void ResolveGatekeeperPolicy_Disabled_UsesOff()
    {
        var llm = LlmWithGatekeeper(
            primaryModel: "primary-large",
            gatekeeperModel: "footman-small",
            gatekeeperEnabled: false);

        var policy = AssistantRouter.ResolveGatekeeperPolicy(llm);

        Assert.Equal(AssistantRouter.GatekeeperPolicyMode.Off, policy.Mode);
        Assert.False(policy.AllowsHelperLlm);
    }

    [Fact]
    public void ResolveGatekeeperPolicy_CodexCli_UsesHeuristicsWithoutHelperCalls()
    {
        var llm = LlmWithGatekeeper(
            primaryModel: "gpt-5.6-luna",
            gatekeeperModel: "small-footman-model") with
        {
            Provider = "codex-cli"
        };

        var policy = AssistantRouter.ResolveGatekeeperPolicy(llm);

        Assert.Equal(AssistantRouter.GatekeeperPolicyMode.HeuristicOnly, policy.Mode);
        Assert.False(policy.AllowsHelperLlm);
    }

    private static LlmSettings LlmWithGatekeeper(
        string primaryModel,
        string gatekeeperModel,
        string? gatekeeperBaseUrl = null,
        bool reusePrimaryForSharedEndpoint = true,
        bool gatekeeperEnabled = true)
        => new(
            Provider: "lmstudio",
            ModelId: primaryModel,
            BaseUrl: "http://localhost:1234",
            ApiKey: null,
            MaxTokens: 2048,
            ContextWindowTokens: 8192,
            Temperature: 0.7,
            GatekeeperBaseUrl: gatekeeperBaseUrl,
            GatekeeperModelId: gatekeeperModel,
            ReusePrimaryForGatekeeperOnSharedEndpoint: reusePrimaryForSharedEndpoint,
            GatekeeperEnabled: gatekeeperEnabled);
}
