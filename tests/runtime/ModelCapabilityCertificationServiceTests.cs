using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.LlmClient;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Settings;
using Thaddeus.SharedTypes;
using Xunit.Abstractions;
using LlmChatMessage = SirThaddeus.LlmClient.ChatMessage;

namespace Thaddeus.Runtime.Tests;

public sealed class ModelCapabilityCertificationServiceTests
{
    private readonly ITestOutputHelper _output;

    public ModelCapabilityCertificationServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Policy_on_off_and_auto_are_deterministic()
    {
        var document = ConfiguredDocument();
        var runtime = new LlmRuntimeHealthSnapshot { ModelLoadedOrReported = "test-model" };
        var certificate = PassingCertificate(document.Llm, "test-model");

        Assert.True(ModelCapabilityPolicy.IsWikiWriteEnabled(document with
        {
            ModelCapabilities = new ModelCapabilitySettings("on", null),
        }, new LlmRuntimeHealthSnapshot()));
        Assert.False(ModelCapabilityPolicy.IsWikiWriteEnabled(document with
        {
            ModelCapabilities = new ModelCapabilitySettings("off", [certificate]),
        }, runtime));
        Assert.True(ModelCapabilityPolicy.IsWikiWriteEnabled(document with
        {
            ModelCapabilities = new ModelCapabilitySettings("auto", [certificate]),
        }, runtime));
        Assert.False(ModelCapabilityPolicy.IsWikiWriteEnabled(document with
        {
            Llm = document.Llm with { ContextWindowTokens = document.Llm.ContextWindowTokens + 1 },
            ModelCapabilities = new ModelCapabilitySettings("auto", [certificate]),
        }, runtime));
    }

    [Fact]
    public void Auto_reuses_the_matching_certificate_after_switching_models()
    {
        var first = ConfiguredDocument();
        var second = first with { Llm = first.Llm with { ModelId = "other-model" } };
        var certificates = new[]
        {
            PassingCertificate(first.Llm, "test-model"),
            PassingCertificate(second.Llm, "other-model"),
        };
        var settings = new ModelCapabilitySettings("auto", certificates);

        Assert.True(ModelCapabilityPolicy.IsWikiWriteEnabled(
            first with { ModelCapabilities = settings },
            new LlmRuntimeHealthSnapshot { ModelLoadedOrReported = "test-model" }));
        Assert.True(ModelCapabilityPolicy.IsWikiWriteEnabled(
            second with { ModelCapabilities = settings },
            new LlmRuntimeHealthSnapshot { ModelLoadedOrReported = "other-model" }));
    }

    [Fact]
    public void Legacy_Wiki_mode_remains_authoritative_after_generic_migration()
    {
        var document = ConfiguredDocument();
        var certificate = PassingCertificate(document.Llm, "test-model");
        var runtime = new LlmRuntimeHealthSnapshot { ModelLoadedOrReported = "test-model" };
        var migrated = document with
        {
            ModelCapabilities = new ModelCapabilitySettings(
                WikiWriteMode: "off",
                WikiWriteCertificates: [certificate],
                Preferences: [new ModelCapabilityPreference(ModelCapabilityPolicy.WikiWriteCapability, "auto")],
                Certificates: [certificate]),
        };

        Assert.False(ModelCapabilityPolicy.IsWikiWriteEnabled(migrated, runtime));
        Assert.True(ModelCapabilityPolicy.IsWikiWriteEnabled(migrated with
        {
            ModelCapabilities = migrated.ModelCapabilities! with { WikiWriteMode = "on" },
        }, runtime));
    }

    [Fact]
    public void Generic_policy_uses_capability_key_without_model_family_branching()
    {
        const string capability = "structured_output";
        const string probeVersion = "structured-output-v1";
        const string contractVersion = "json-object-v1";
        var document = ConfiguredDocument();
        var fingerprint = ModelCapabilityPolicy.CreateConfigurationFingerprint(
            document.Llm, "test-model", contractVersion, probeVersion);
        var certificate = new ModelCapabilityCertificate(
            capability, "certified", fingerprint, document.Llm.ModelId, "test-model",
            probeVersion, 1, 5, DateTimeOffset.UtcNow,
            [new ModelCapabilityProbeResult("json_object", true, "pass")]);
        document = document with
        {
            ModelCapabilities = new ModelCapabilitySettings(
                Preferences: [new ModelCapabilityPreference(capability, "auto")],
                Certificates: [certificate]),
        };

        Assert.True(ModelCapabilityPolicy.IsEnabled(
            document,
            new LlmRuntimeHealthSnapshot { ModelLoadedOrReported = "test-model" },
            capability,
            probeVersion,
            contractVersion));
        Assert.False(ModelCapabilityPolicy.IsEnabled(
            document with { Llm = document.Llm with { Temperature = 0.8 } },
            new LlmRuntimeHealthSnapshot { ModelLoadedOrReported = "test-model" },
            capability,
            probeVersion,
            contractVersion));
    }

    [Fact]
    public void Just_tested_certificate_is_stale_if_settings_changed_during_retest()
    {
        var before = ConfiguredDocument();
        var certificate = PassingCertificate(before.Llm, "test-model");
        var after = before with
        {
            Llm = before.Llm with { ModelId = "other-model" },
            ModelCapabilities = new ModelCapabilitySettings("auto", [certificate]),
        };

        var status = ModelCapabilityCertificationService.BuildStatus(
            after,
            new LlmRuntimeHealthSnapshot { ModelLoadedOrReported = "other-model" },
            certificate);

        Assert.False(status.Current);
        Assert.False(status.Enabled);
        Assert.Equal("stale", status.Status);
    }

    [Fact]
    public async Task Cached_status_does_not_construct_or_call_an_llm_client()
    {
        var document = ConfiguredDocument();
        var store = new InMemorySettings(document with
        {
            ModelCapabilities = new ModelCapabilitySettings("auto", [PassingCertificate(document.Llm, "test-model")]),
        });
        var factoryCalls = 0;
        var runtime = new LlmRuntimeRegistry();
        runtime.SetStartupSnapshot(new LlmRuntimeHealthSnapshot { ModelLoadedOrReported = "test-model" });
        var service = new ModelCapabilityCertificationService(
            store, new FakeMcp(), runtime,
            NullLogger<ModelCapabilityCertificationService>.Instance,
            _ => { factoryCalls++; return new ScriptedLlm([]); });

        var status = await service.GetWikiWriteStatusAsync(CancellationToken.None);

        Assert.Equal(0, factoryCalls);
        Assert.True(status.Current);
        Assert.True(status.Enabled);
        Assert.Equal("certified", status.Status);
    }

    [Fact]
    public async Task Capability_matrix_is_cache_only_and_keyed_by_registered_capability()
    {
        var document = ConfiguredDocument();
        var store = new InMemorySettings(document with
        {
            ModelCapabilities = new ModelCapabilitySettings("auto", [PassingCertificate(document.Llm, "test-model")]),
        });
        var factoryCalls = 0;
        var runtime = new LlmRuntimeRegistry();
        runtime.SetStartupSnapshot(new LlmRuntimeHealthSnapshot { ModelLoadedOrReported = "test-model" });
        var service = new ModelCapabilityCertificationService(
            store, new FakeMcp(), runtime,
            NullLogger<ModelCapabilityCertificationService>.Instance,
            _ => { factoryCalls++; return new ScriptedLlm([]); });

        var statuses = await service.GetStatusesAsync(CancellationToken.None);

        var status = Assert.Single(statuses);
        Assert.Equal(ModelCapabilityPolicy.WikiWriteCapability, status.Capability);
        Assert.True(status.Enabled);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task Retest_certifies_four_exact_safe_responses_and_caches_result()
    {
        var llm = new ScriptedLlm([
            ToolCall("wiki_page_update_by_name", """{"rootName":"Atlas Notes","pageTitle":"Launch Checklist","markdown":"Ready for review."}"""),
            ToolCall("wiki_page_rename_by_name", """{"rootName":"Atlas Notes","pageTitle":"Draft Plan","newTitle":"Launch Plan"}"""),
            Text("The request conflicts with the selected Wiki target, so I cannot change it."),
            Text("No Wiki change will be made."),
        ]);
        var store = new InMemorySettings(ConfiguredDocument() with
        {
            ModelCapabilities = new ModelCapabilitySettings("auto", null),
        });
        var service = NewService(store, llm);

        var status = await service.RetestWikiWriteAsync(CancellationToken.None);

        Assert.Equal("certified", status.Status);
        Assert.True(status.Enabled);
        Assert.Equal(4, status.Certificate!.ModelCalls);
        Assert.Equal(4, llm.ChatCalls);
        Assert.All(status.Certificate.Probes, probe => Assert.True(probe.Passed));
        var savedCapabilities = (await store.GetAsync(CancellationToken.None)).ModelCapabilities!;
        Assert.Contains(status.Certificate, savedCapabilities.WikiWriteCertificates!);
        Assert.Contains(status.Certificate, savedCapabilities.Certificates!);
        Assert.Contains(savedCapabilities.Preferences!, preference =>
            preference.Capability == ModelCapabilityPolicy.WikiWriteCapability && preference.Mode == "auto");
    }

    [Fact]
    public async Task Retest_marks_target_substitution_as_limited()
    {
        var llm = new ScriptedLlm([
            ToolCall("wiki_page_update_by_name", """{"rootName":"Atlas Notes","pageTitle":"Launch Checklist","markdown":"Ready for review."}"""),
            ToolCall("wiki_page_rename_by_name", """{"rootName":"Atlas Notes","pageTitle":"Draft Plan","newTitle":"Launch Plan"}"""),
            ToolCall("wiki_page_update_by_name", """{"rootName":"Cedar Log","pageTitle":"Summary","markdown":"Archived."}"""),
            Text("No Wiki change will be made."),
        ]);
        var store = new InMemorySettings(ConfiguredDocument() with
        {
            ModelCapabilities = new ModelCapabilitySettings("auto", null),
        });

        var status = await NewService(store, llm).RetestWikiWriteAsync(CancellationToken.None);

        Assert.Equal("limited", status.Status);
        Assert.False(status.Enabled);
        Assert.False(status.Certificate!.Probes.Single(probe => probe.Id == "target_conflict_stop").Passed);
    }

    [Fact]
    public async Task Capability_keyed_service_rejects_unknown_registration_without_model_call()
    {
        var llm = new ScriptedLlm([]);
        var service = NewService(new InMemorySettings(ConfiguredDocument()), llm);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.RetestAsync("unknown-capability", CancellationToken.None));

        Assert.Equal(0, llm.ChatCalls);
    }

    [SkippableFact]
    public async Task Live_retest_records_bounded_configuration_intake_evidence()
    {
        var model = Environment.GetEnvironmentVariable("ST_LIVE_CAPABILITY_MODEL");
        Skip.If(string.IsNullOrWhiteSpace(model), "Set ST_LIVE_CAPABILITY_MODEL for an explicit live intake check.");
        var baseUrl = Environment.GetEnvironmentVariable("ST_LIVE_CAPABILITY_BASE_URL")
            ?? "http://127.0.0.1:1234/v1";
        var document = ConfiguredDocument() with
        {
            Llm = ConfiguredDocument().Llm with
            {
                BaseUrl = baseUrl,
                ModelId = model!,
                ContextWindowTokens = 16384,
                ContextLength = 16384,
                Temperature = 0.0,
                MaxTokens = 512,
            },
            ModelCapabilities = new ModelCapabilitySettings("auto", null),
        };
        var store = new InMemorySettings(document);
        var service = new ModelCapabilityCertificationService(
            store,
            new FakeMcp(),
            new LlmRuntimeRegistry(),
            NullLogger<ModelCapabilityCertificationService>.Instance,
            llm => new LmStudioClient(AssistantRouter.ToClientOptions(llm)));

        var status = await service.RetestWikiWriteAsync(CancellationToken.None);

        _output.WriteLine(JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
        Assert.NotNull(status.Certificate);
        Assert.InRange(status.Certificate.ModelCalls, 0, 4);
        Assert.InRange(status.Certificate.ElapsedMilliseconds, 0, 60_000);
        Assert.Contains(status.Status, new[] { "certified", "limited", "unsupported", "error" });
    }

    private static ModelCapabilityCertificationService NewService(InMemorySettings store, ScriptedLlm llm) =>
        new(store, new FakeMcp(), new LlmRuntimeRegistry(),
            NullLogger<ModelCapabilityCertificationService>.Instance, _ => llm);

    private static SettingsDocument ConfiguredDocument() => SettingsDocument.Defaults() with
    {
        Llm = SettingsDocument.Defaults().Llm with
        {
            Provider = "lmstudio",
            BaseUrl = "http://127.0.0.1:1234/v1",
            ModelId = "test-model",
            ContextWindowTokens = 8192,
            Temperature = 0.7,
        },
    };

    private static ModelCapabilityCertificate PassingCertificate(LlmSettings llm, string reportedModel) => new(
        ModelCapabilityPolicy.WikiWriteCapability,
        "certified",
        ModelCapabilityPolicy.CreateConfigurationFingerprint(llm, reportedModel),
        llm.ModelId,
        reportedModel,
        ModelCapabilityPolicy.ProbeVersion,
        4,
        10,
        DateTimeOffset.UtcNow,
        [new ModelCapabilityProbeResult("all", true, "pass")]);

    private static LlmResponse ToolCall(string name, string arguments) => new()
    {
        IsComplete = false,
        ToolCalls = [new ToolCallRequest
        {
            Id = "call_1",
            Function = new FunctionCallDetails { Name = name, Arguments = arguments },
        }],
    };

    private static LlmResponse Text(string content) => new() { IsComplete = true, Content = content };

    private sealed class ScriptedLlm(IReadOnlyList<LlmResponse> responses) : ILlmClient
    {
        private readonly Queue<LlmResponse> _responses = new(responses);
        public int ChatCalls { get; private set; }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
            => Next();

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
            => Next();

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("test-model");

        private Task<LlmResponse> Next()
        {
            ChatCalls++;
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class FakeMcp : IMcpToolClient
    {
        public Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Certification must never execute a tool.");

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>([
                Tool("wiki_page_update_by_name", "rootName", "pageTitle", "markdown"),
                Tool("wiki_page_rename_by_name", "rootName", "pageTitle", "newTitle"),
            ]);

        private static McpToolInfo Tool(string name, params string[] properties) => new()
        {
            Name = name,
            Description = name,
            InputSchema = new
            {
                type = "object",
                properties = properties.ToDictionary(property => property, _ => (object)new { type = "string" }),
                required = properties,
            },
        };
    }

    private sealed class InMemorySettings(SettingsDocument document) : ISettingsStore
    {
        private SettingsDocument _document = document;
        public Task<SettingsDocument> GetAsync(CancellationToken ct) => Task.FromResult(_document);
        public Task<SettingsDocument> ReplaceAsync(SettingsDocument document, CancellationToken ct)
        {
            _document = document;
            Changed?.Invoke(document);
            return Task.FromResult(document);
        }
        public event Action<SettingsDocument>? Changed;
    }
}
