using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Events;
using Thaddeus.Runtime.Settings;
using Thaddeus.Runtime.Tools;
using Thaddeus.SharedTypes;
using Xunit;
using AgentAuditMode = SirThaddeus.Agent.ToolPermissionAuditMode;

namespace Thaddeus.Runtime.Tests;

public class RuntimePermissionGateAdapterTests
{
    [Fact]
    public void Constructor_rejects_null_gate_or_blank_ids()
    {
        using var fixture = new GateFixture();

        Assert.Throws<ArgumentNullException>(() =>
            new RuntimePermissionGateAdapter(null!, "t1", "turn1"));
        Assert.Throws<ArgumentException>(() =>
            new RuntimePermissionGateAdapter(fixture.Gate, "", "turn1"));
        Assert.Throws<ArgumentException>(() =>
            new RuntimePermissionGateAdapter(fixture.Gate, "t1", "   "));
    }

    [Fact]
    public async Task CheckAsync_returns_Grant_for_Safe_group_tools()
    {
        using var fixture = new GateFixture();
        var adapter = new RuntimePermissionGateAdapter(fixture.Gate, "t1", "turn1");

        // "ping" is classified as Safe (see ToolGroupClassifier) and
        // short-circuits to Allow without any settings lookup or prompt.
        var result = await adapter.CheckAsync("ping", "{}", CancellationToken.None);

        Assert.True(result.Granted);
        Assert.True(result.PermissionRequired);
        Assert.Equal(AgentAuditMode.SessionGrant, result.AuditMode);
    }

    [Theory]
    [InlineData("tool_list_capabilities")]
    [InlineData("capabilities.describe")]
    [InlineData("policy.get_state")]
    [InlineData("health.check")]
    public void ToolGroupClassifier_MetaTools_AreSafe(string toolName)
    {
        Assert.Equal(ToolGroup.Safe, ToolGroupClassifier.Classify(toolName));
    }

    [Fact]
    public async Task CheckAsync_returns_Deny_when_policy_is_off()
    {
        // Web group with policy=off must deny without a prompt — the
        // adapter must forward that deterministic no.
        using var fixture = new GateFixture(webPolicy: "off");
        var adapter = new RuntimePermissionGateAdapter(fixture.Gate, "t1", "turn1");

        var result = await adapter.CheckAsync("web_search", "{\"q\":\"hi\"}", CancellationToken.None);

        Assert.False(result.Granted);
        Assert.True(result.PermissionRequired);
        Assert.Contains("blocked", result.DenialReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_denies_Web_group_when_offline_mode_is_on()
    {
        using var fixture = new GateFixture(webPolicy: "always", offlineMode: true);
        var adapter = new RuntimePermissionGateAdapter(fixture.Gate, "t1", "turn1");

        var result = await adapter.CheckAsync("web_search", "{\"q\":\"hi\"}", CancellationToken.None);

        Assert.False(result.Granted);
        Assert.True(result.PermissionRequired);
        Assert.Contains("blocked", result.DenialReason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Per-tool override enforcement (drives ToolPermissionGate directly) ──

    [Fact]
    public async Task PerToolOff_BeatsGroupAlways_ButOtherGroupToolsAllowed()
    {
        using var fixture = new GateFixture(
            webPolicy: "always",
            toolOverrides: new Dictionary<string, string> { ["web_search"] = "off" });

        // The single overridden tool is denied even though the group is "always".
        var denied = await fixture.Gate.DecideAsync("web_search", "{}", "t1", "turn1", CancellationToken.None);
        Assert.Equal(ToolPermissionDecision.Deny, denied);

        // A sibling web tool with no override still rides the group "always".
        var allowed = await fixture.Gate.DecideAsync("weather_geocode", "{}", "t1", "turn1", CancellationToken.None);
        Assert.Equal(ToolPermissionDecision.Allow, allowed);
    }

    [Fact]
    public async Task OfflineMode_DeniesWeb_EvenWithPerToolAlways()
    {
        using var fixture = new GateFixture(
            webPolicy: "off",
            offlineMode: true,
            toolOverrides: new Dictionary<string, string> { ["web_search"] = "always" });

        var decision = await fixture.Gate.DecideAsync("web_search", "{}", "t1", "turn1", CancellationToken.None);
        Assert.Equal(ToolPermissionDecision.Deny, decision);
    }

    [Fact]
    public async Task PascalCaseToolName_ResolvesSameOverride_AsCanonical()
    {
        // Override stored under canonical snake_case; a PascalCase call name
        // canonicalizes to the same key and is denied.
        using var fixture = new GateFixture(
            webPolicy: "always",
            toolOverrides: new Dictionary<string, string> { ["web_search"] = "off" });

        var decision = await fixture.Gate.DecideAsync("WebSearch", "{}", "t1", "turn1", CancellationToken.None);
        Assert.Equal(ToolPermissionDecision.Deny, decision);
    }

    [Fact]
    public async Task PerToolAsk_PromptsEvenAfterGroupSessionGrant_AndSessionAllowsOnlyThatTool()
    {
        using var fixture = new GateFixture(
            webPolicy: "ask",
            toolOverrides: new Dictionary<string, string>
            {
                ["web_search"] = "ask",
                ["browser_navigate"] = "ask",
            });

        // 1. Grant the whole Web group for the session via a no-override tool.
        var groupPending = await DriveToPromptAsync(fixture.Gate, "weather_geocode");
        Assert.Equal("group", groupPending.Pending.Scope);
        Assert.True(fixture.Gate.Respond(groupPending.Pending.Id, ToolPermissionResponse.Session, "group"));
        Assert.Equal(ToolPermissionDecision.Allow, await groupPending.Decision);

        // Sibling no-override tool now rides the group session grant (no prompt).
        Assert.Equal(ToolPermissionDecision.Allow,
            await fixture.Gate.DecideAsync("resolve_timezone", "{}", "t1", "turn1", CancellationToken.None));

        // 2. web_search has an explicit per-tool "ask" — it must STILL prompt,
        //    and the prompt is tool-scoped.
        var toolPending = await DriveToPromptAsync(fixture.Gate, "web_search");
        Assert.Equal("tool", toolPending.Pending.Scope);
        Assert.True(fixture.Gate.Respond(toolPending.Pending.Id, ToolPermissionResponse.Session, "tool"));
        Assert.Equal(ToolPermissionDecision.Allow, await toolPending.Decision);

        // 3. web_search now auto-allows from the per-tool session cache.
        Assert.Equal(ToolPermissionDecision.Allow,
            await fixture.Gate.DecideAsync("web_search", "{}", "t1", "turn1", CancellationToken.None));

        // 4. A DIFFERENT explicit-ask tool is unaffected — still prompts.
        var otherPending = await DriveToPromptAsync(fixture.Gate, "browser_navigate");
        Assert.Equal("tool", otherPending.Pending.Scope);
        fixture.Gate.Respond(otherPending.Pending.Id, ToolPermissionResponse.Deny, "tool");
        Assert.Equal(ToolPermissionDecision.Deny, await otherPending.Decision);
    }

    [Fact]
    public async Task RespondAlwaysToolScoped_PersistsToolOverride_WithoutChangingGroupPolicy()
    {
        using var fixture = new GateFixture(
            webPolicy: "ask",
            toolOverrides: new Dictionary<string, string> { ["web_search"] = "ask" });

        var pending = await DriveToPromptAsync(fixture.Gate, "web_search");
        Assert.Equal("tool", pending.Pending.Scope);
        Assert.True(fixture.Gate.Respond(pending.Pending.Id, ToolPermissionResponse.Always, "tool"));
        Assert.Equal(ToolPermissionDecision.Allow, await pending.Decision);

        // PersistToolAlwaysAsync is fire-and-forget; wait for the store write.
        await WaitUntilAsync(() =>
            fixture.Store.Current.Permissions?.ToolOverrides is { } o &&
            o.TryGetValue("web_search", out var v) && v == "always");

        var perms = fixture.Store.Current.Permissions!;
        Assert.Equal("always", perms.ToolOverrides!["web_search"]);
        Assert.Equal("ask", perms.Web); // group policy untouched
    }

    // ── Catalog shape ──────────────────────────────────────────────────

    [Fact]
    public void BuildCatalog_ProducesExpectedShape()
    {
        using var fixture = new GateFixture(
            webPolicy: "ask",
            toolOverrides: new Dictionary<string, string> { ["web_search"] = "off" });

        var catalog = fixture.Gate.BuildCatalog(fixture.Store.Current);

        Assert.Equal("none", catalog.DeveloperOverride);

        // Fixed group order, camelCase keys, no Safe/meta group.
        Assert.Equal(
            new[] { "screen", "files", "system", "web", "memoryRead", "memoryWrite" },
            catalog.Groups.Select(g => g.Key).ToArray());

        var web = catalog.Groups.Single(g => g.Key == "web");
        Assert.Equal("ask", web.Policy);

        var webSearch = web.Tools.Single(t => t.Name == "web_search");
        Assert.Equal("off", webSearch.Override);
        Assert.Equal("off", webSearch.Effective);

        var geocode = web.Tools.Single(t => t.Name == "weather_geocode");
        Assert.Null(geocode.Override);
        Assert.Equal("ask", geocode.Effective);

        // Tool names are lowercase, sorted, and de-duplicated within each group,
        // and none classify as Safe (the enforcement-truth invariant).
        foreach (var group in catalog.Groups)
        {
            Assert.Equal(group.Tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal),
                group.Tools.Select(t => t.Name));
            Assert.Equal(group.Tools.Select(t => t.Name).Distinct().Count(), group.Tools.Count);
            foreach (var tool in group.Tools)
            {
                Assert.DoesNotContain(tool.Name, c => char.IsUpper(c));
                Assert.NotEqual(ToolGroup.Safe, ToolGroupClassifier.Classify(tool.Name));
            }
        }

        // Genuinely Safe/meta tools (per the RUNTIME classifier) are excluded.
        // Note: calculator/python_eval are NOT excluded — the runtime classifier
        // treats them as System (unknown→System), unlike the agent classifier
        // which maps them to meta. The catalog reflects runtime enforcement.
        var allTools = catalog.Groups.SelectMany(g => g.Tools).Select(t => t.Name).ToArray();
        Assert.DoesNotContain("time_now", allTools);
        Assert.DoesNotContain("tool_ping", allTools);
        Assert.DoesNotContain("health.check", allTools);
        Assert.DoesNotContain("capabilities.describe", allTools);
        Assert.DoesNotContain("policy.get_state", allTools);
    }

    [Fact]
    public void BuildCatalog_Effective_ReflectsDeveloperOverride_ForDangerousGroups()
    {
        var defaults = SettingsDocument.Defaults();
        var doc = defaults with
        {
            Permissions = defaults.Permissions! with { DeveloperOverride = "always" },
        };
        using var fixture = new GateFixture();

        var catalog = fixture.Gate.BuildCatalog(doc);
        Assert.Equal("always", catalog.DeveloperOverride);

        // Dangerous group (files) inherits the developer override in effective.
        var files = catalog.Groups.Single(g => g.Key == "files");
        Assert.All(files.Tools, t => Assert.Equal("always", t.Effective));

        // Memory groups are NOT dangerous → developer override does not apply.
        var memWrite = catalog.Groups.Single(g => g.Key == "memoryWrite");
        Assert.All(memWrite.Tools, t => Assert.Equal(memWrite.Policy, t.Effective));
    }

    private sealed record PromptHandle(Task<ToolPermissionDecision> Decision, PendingPermission Pending);

    /// <summary>
    /// Starts a DecideAsync call that is expected to prompt, waits for the
    /// pending request to surface, and returns both the in-flight decision
    /// task and the pending snapshot.
    /// </summary>
    private static async Task<PromptHandle> DriveToPromptAsync(ToolPermissionGate gate, string tool)
    {
        var decision = gate.DecideAsync(tool, "{}", "t1", "turn1", CancellationToken.None);

        PendingPermission? pending = null;
        for (var i = 0; i < 200 && pending is null; i++)
        {
            pending = gate.ListPending().FirstOrDefault(p => p.Tool == tool);
            if (pending is null) await Task.Delay(10);
        }

        Assert.NotNull(pending);
        return new PromptHandle(decision, pending!);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int attempts = 200)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "condition was not met within the timeout");
    }

    private sealed class GateFixture : IDisposable
    {
        public ToolPermissionGate Gate { get; }
        public InMemorySettingsStore Store { get; }

        public GateFixture(
            string webPolicy = "ask",
            bool offlineMode = false,
            Dictionary<string, string>? toolOverrides = null)
        {
            var defaults = SettingsDocument.Defaults();
            var perms = defaults.Permissions! with
            {
                Web = webPolicy,
                ToolOverrides = toolOverrides,
            };
            var doc = defaults with
            {
                Permissions = perms,
                Privacy = defaults.Privacy with { OfflineMode = offlineMode },
            };

            Store = new InMemorySettingsStore(doc);
            var bus = new EventBus(NullLogger<EventBus>.Instance);
            Gate = new ToolPermissionGate(Store, bus, NullLogger<ToolPermissionGate>.Instance);
        }

        public void Dispose() { }
    }

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        private SettingsDocument _doc;
        public event Action<SettingsDocument>? Changed;

        public InMemorySettingsStore(SettingsDocument doc) => _doc = doc;

        /// <summary>Latest stored document — test seam for persistence assertions.</summary>
        public SettingsDocument Current => _doc;

        public Task<SettingsDocument> GetAsync(CancellationToken ct)
            => Task.FromResult(_doc);

        public Task<SettingsDocument> ReplaceAsync(SettingsDocument next, CancellationToken ct)
        {
            _doc = next;
            Changed?.Invoke(_doc);
            return Task.FromResult(_doc);
        }
    }
}
