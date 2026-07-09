using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SirThaddeus.AuditLog;
using Thaddeus.Runtime.Api;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Events;
using Thaddeus.Runtime.Modules;
using Thaddeus.Runtime.Settings;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tests;

public sealed class ModuleRuntimeServiceTests : IDisposable
{
    private readonly string _root;

    public ModuleRuntimeServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "thaddeus-modules-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Lists_manifest_backed_external_modules()
    {
        var manifest = WriteFakeHealthModule();
        var service = NewService(manifest);

        var modules = await service.ListAsync(CancellationToken.None);

        var health = Assert.Single(modules, m => m.Id == ModuleRuntimeService.HealthPackModuleId);
        Assert.Equal("Health Pack Fixture", health.Name);
        Assert.Equal(ModuleApprovalStatus.Pending, health.ApprovalStatus);
        Assert.Equal(2, health.ToolCount);
        Assert.True(health.PermissionCount > 0);
    }

    [Fact]
    public async Task Approval_state_persists()
    {
        var manifest = WriteFakeHealthModule();
        var statePath = Path.Combine(_root, "module-state.json");
        var first = NewService(manifest, statePath);
        await first.ApproveAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);

        var second = NewService(manifest, statePath);
        var detail = await second.GetDetailAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(ModuleApprovalStatus.Approved, detail!.ApprovalStatus);
    }

    [Fact]
    public async Task Disabled_modules_cannot_be_invoked()
    {
        var manifest = WriteFakeHealthModule();
        var service = NewService(manifest);
        await service.ApproveAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);
        await service.DisableAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ModuleRuntimeException>(() =>
            service.InvokeToolAsync(
                ModuleRuntimeService.HealthPackModuleId,
                "health.provider_status",
                null,
                CancellationToken.None));

        Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
        var detail = await service.GetDetailAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);
        Assert.NotNull(detail);
        var audit = Assert.Single(detail!.RecentAuditEvents, evt => evt.Action == "module.tool_invoked" && evt.ToolName == "health.provider_status");
        Assert.Equal("denied", audit.Result);
        Assert.Contains("disabled", audit.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unapproved_modules_require_approval()
    {
        var manifest = WriteFakeHealthModule();
        var service = NewService(manifest);

        var ex = await Assert.ThrowsAsync<ModuleRuntimeException>(() =>
            service.InvokeToolAsync(
                ModuleRuntimeService.HealthPackModuleId,
                "health.provider_status",
                null,
                CancellationToken.None));

        Assert.Contains("approval", ex.Message, StringComparison.OrdinalIgnoreCase);
        var detail = await service.GetDetailAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);
        Assert.NotNull(detail);
        var audit = Assert.Single(detail!.RecentAuditEvents, evt => evt.Action == "module.tool_invoked" && evt.ToolName == "health.provider_status");
        Assert.Equal("denied", audit.Result);
        Assert.Contains("approval", audit.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_module_tool_invocation_is_audited_without_exposing_secrets()
    {
        var manifest = WriteFakeHealthModule();
        var service = NewService(manifest);
        await service.ApproveAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ModuleRuntimeException>(() =>
            service.InvokeToolAsync(
                ModuleRuntimeService.HealthPackModuleId,
                "health.not_real",
                null,
                CancellationToken.None));

        Assert.Contains("does not expose", ex.Message, StringComparison.OrdinalIgnoreCase);
        var detail = await service.GetDetailAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);
        Assert.NotNull(detail);
        var audit = Assert.Single(detail!.RecentAuditEvents, evt => evt.Action == "module.tool_invoked" && evt.ToolName == "health.not_real");
        Assert.Equal("denied", audit.Result);
        Assert.Contains("does not expose", audit.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret-value", JsonSerializer.Serialize(detail, ModulesJsonContext.Default.ModuleDetailDto));
    }

    [Fact]
    public async Task Status_check_on_unapproved_module_is_audited_without_runtime_error_state()
    {
        var manifest = WriteFakeHealthModule();
        var service = NewService(manifest);

        var status = await service.CheckStatusAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);
        var detail = await service.GetDetailAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("pending", status!.Status);
        Assert.Null(status.LastError);
        Assert.Null(status.ProviderStatus);
        Assert.NotNull(detail);
        Assert.Null(detail!.LastError);
        Assert.NotNull(detail.LastStatusCheck);
        var audit = Assert.Single(detail.RecentAuditEvents, evt => evt.Action == "module.status_check");
        Assert.Equal("denied", audit.Result);
        Assert.Contains("approval", audit.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_check_on_disabled_module_is_audited_without_invoking_tools()
    {
        var manifest = WriteFakeHealthModule();
        var service = NewService(manifest);
        await service.ApproveAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);
        await service.DisableAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);

        var status = await service.CheckStatusAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);
        var detail = await service.GetDetailAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("disabled", status!.Status);
        Assert.Null(status.ProviderStatus);
        Assert.NotNull(detail);
        var audit = Assert.Single(detail!.RecentAuditEvents, evt => evt.Action == "module.status_check");
        Assert.Equal("denied", audit.Result);
        Assert.Contains("disabled", audit.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Manual_health_pack_tool_invocation_works_through_runtime()
    {
        var manifest = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "thaddeus-health-pack", "manifest.json"));
        Assert.True(File.Exists(manifest), $"Expected Health Pack manifest at {manifest}");

        // This is a real integration test: it boots the Health Pack MCP sidecar
        // over stdio via `npm run mcp` (tsx). The pack's node_modules are
        // gitignored, so a fresh clone or an isolated git worktree that hasn't
        // run `npm install` in thaddeus-health-pack cannot launch the sidecar --
        // which otherwise surfaces as a cryptic "MCP stdout stream closed
        // unexpectedly" mid-test. When the dependencies aren't installed, skip
        // (with an actionable reason) rather than fail the whole runtime suite.
        var packRoot = Path.GetDirectoryName(manifest)!;
        Skip.IfNot(
            Directory.Exists(Path.Combine(packRoot, "node_modules", ".bin")),
            $"Health Pack sidecar dependencies are not installed. Run `npm install` in {packRoot} " +
            "to exercise this integration test (its node_modules are gitignored).");

        var service = NewService(manifest);
        await service.ApproveAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);

        var result = await service.InvokeToolAsync(
            ModuleRuntimeService.HealthPackModuleId,
            "health.provider_status",
            null,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("provider", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret-value", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Chat_routes_health_brief_request_to_approved_module()
    {
        var manifest = WriteFakeHealthModule();
        var modules = NewService(manifest);
        await modules.ApproveAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);
        var store = new JsonFileThreadStore(Path.Combine(_root, "threads"), NullLogger<JsonFileThreadStore>.Instance);
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var stub = new StubAssistant(store, new ChatTurnPublisher(bus), NullLogger<StubAssistant>.Instance)
        {
            DeltaDelay = TimeSpan.Zero
        };
        using var router = new AssistantRouter(
            new InMemorySettings(SettingsDocument.Defaults()),
            stub,
            _ => throw new InvalidOperationException("LLM should not be used for module-routed health brief."),
            NullLogger<AssistantRouter>.Instance,
            modules);
        var thread = await store.CreateAsync("health", CancellationToken.None);

        var response = await router.RespondAsync(thread.Id, "Give me my morning health brief", CancellationToken.None);

        Assert.Contains("morning health brief", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Readiness", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stubbed reply", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Chat_points_to_modules_when_health_provider_needs_setup()
    {
        var manifest = WriteFakeHealthModule(
            """{"providerName":"google-health","selectedProvider":"google-health","lifecycle":"not_configured","connected":false,"configured":false,"authenticated":false,"missingConfig":["GOOGLE_HEALTH_CLIENT_ID"],"warnings":[],"errors":[],"credentials":{},"scopes":[]}""");
        var modules = NewService(manifest);
        await modules.ApproveAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);
        var store = new JsonFileThreadStore(Path.Combine(_root, "threads"), NullLogger<JsonFileThreadStore>.Instance);
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var stub = new StubAssistant(store, new ChatTurnPublisher(bus), NullLogger<StubAssistant>.Instance)
        {
            DeltaDelay = TimeSpan.Zero
        };
        using var router = new AssistantRouter(
            new InMemorySettings(SettingsDocument.Defaults()),
            stub,
            _ => throw new InvalidOperationException("LLM should not be used for module-routed health setup guidance."),
            NullLogger<AssistantRouter>.Instance,
            modules);
        var thread = await store.CreateAsync("health", CancellationToken.None);

        var response = await router.RespondAsync(thread.Id, "Give me my morning health brief", CancellationToken.None);

        Assert.Contains("not ready", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Modules -> Health Pack", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Readiness", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stubbed reply", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Module_api_responses_redact_env_values()
    {
        var manifest = WriteFakeHealthModule();
        var service = NewService(manifest);

        var detail = await service.GetDetailAsync(ModuleRuntimeService.HealthPackModuleId, CancellationToken.None);
        var json = JsonSerializer.Serialize(detail, ModulesJsonContext.Default.ModuleDetailDto);

        Assert.Contains("SECRET_TOKEN", json);
        Assert.DoesNotContain("super-secret-value", json);
    }

    [Fact]
    public async Task Endpoint_dtos_have_stable_camelcase_shape()
    {
        var manifest = WriteFakeHealthModule();
        var service = NewService(manifest);
        var list = new ModuleListResponse(await service.ListAsync(CancellationToken.None));

        var json = JsonSerializer.Serialize(list, ModulesJsonContext.Default.ModuleListResponse);

        Assert.Contains("\"modules\"", json);
        Assert.Contains("\"approvalStatus\"", json);
        Assert.Contains("\"permissionCount\"", json);
        Assert.DoesNotContain("\"ApprovalStatus\"", json);
    }

    private ModuleRuntimeService NewService(string manifestPath, string? statePath = null)
    {
        statePath ??= Path.Combine(_root, "module-state-" + Guid.NewGuid().ToString("N") + ".json");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:ManifestPaths:0"] = manifestPath
            })
            .Build();
        var store = new JsonFileModuleStateStore(statePath, NullLogger<JsonFileModuleStateStore>.Instance);
        return new ModuleRuntimeService(
            config,
            store,
            new TestAuditLogger(),
            NullLogger<ModuleRuntimeService>.Instance);
    }

    private string WriteFakeHealthModule(string? providerStatusJson = null)
    {
        providerStatusJson ??= """{"provider":"mock","ok":true,"configured":true}""";
        var moduleDir = Path.Combine(_root, "fake-health");
        Directory.CreateDirectory(moduleDir);
        var serverPath = Path.Combine(moduleDir, "server.js");
        File.WriteAllText(serverPath, FakeMcpServerSource.Replace("__PROVIDER_STATUS_JSON__", providerStatusJson));
        var manifestPath = Path.Combine(moduleDir, "manifest.json");
        File.WriteAllText(manifestPath, $$"""
        {
          "id": "com.thaddeus.health",
          "name": "Health Pack Fixture",
          "version": "0.1.0",
          "description": "Fixture module for runtime tests.",
          "permissions": {
            "externalAccounts": [
              { "provider": "google-health", "scopes": ["sleep.read"] }
            ],
            "memory": {
              "read": ["fitness_goals"],
              "write": ["daily_health_snapshots"]
            }
          },
          "tools": [
            "health.provider_status",
            "health.get_morning_strategy_brief"
          ],
          "jobs": [],
          "hooks": [],
          "memoryNamespaces": ["daily_health_snapshots"],
          "execution": {
            "type": "stdio",
            "command": "node",
            "args": ["{{serverPath.Replace("\\", "\\\\")}}"],
            "env": {
              "SECRET_TOKEN": "super-secret-value"
            }
          }
        }
        """);
        return manifestPath;
    }

    private const string FakeMcpServerSource = """
    const readline = require('readline');
    const rl = readline.createInterface({ input: process.stdin, output: process.stdout, terminal: false });
    function write(value) { process.stdout.write(JSON.stringify(value) + '\n'); }
    rl.on('line', (line) => {
      if (!line.trim()) return;
      const msg = JSON.parse(line);
      if (!msg.id) return;
      if (msg.method === 'initialize') {
        write({ jsonrpc: '2.0', id: msg.id, result: { protocolVersion: '2024-11-05', capabilities: {}, serverInfo: { name: 'fake-health', version: '0.1.0' } } });
        return;
      }
      if (msg.method === 'tools/list') {
        write({ jsonrpc: '2.0', id: msg.id, result: { tools: [
          { name: 'health.provider_status', description: 'Provider status', inputSchema: { type: 'object' } },
          { name: 'health.get_morning_strategy_brief', description: 'Morning brief', inputSchema: { type: 'object' } }
        ] } });
        return;
      }
      if (msg.method === 'tools/call') {
        const name = msg.params.name;
        const statusPayload = __PROVIDER_STATUS_JSON__;
        const payload = name === 'health.provider_status'
          ? statusPayload
          : { date: '2026-06-03', readinessLevel: 'steady', keySignals: ['Sleep is stable'], recommendations: ['Keep training moderate'], caveats: ['Fixture data'] };
        write({ jsonrpc: '2.0', id: msg.id, result: { content: [{ type: 'text', text: JSON.stringify(payload) }] } });
        return;
      }
      write({ jsonrpc: '2.0', id: msg.id, result: {} });
    });
    """;

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
}
