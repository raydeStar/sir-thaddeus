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
    public async Task CheckAsync_forwards_threadId_and_turnId_bound_at_construction()
    {
        // Automation allowlist keyed by threadId — if the adapter passes
        // the right threadId, pre-approved tools skip the prompt flow.
        using var fixture = new GateFixture();
        using var scope = fixture.Gate.RegisterThreadAllowlist("t1", new[] { "web_search" });

        var adapter = new RuntimePermissionGateAdapter(fixture.Gate, "t1", "turn1");
        var result = await adapter.CheckAsync("web_search", "{}", CancellationToken.None);

        Assert.True(result.Granted);
        Assert.Equal(AgentAuditMode.SessionGrant, result.AuditMode);
    }

    private sealed class GateFixture : IDisposable
    {
        public ToolPermissionGate Gate { get; }

        public GateFixture(string webPolicy = "ask")
        {
            var defaults = SettingsDocument.Defaults();
            var perms = defaults.Permissions! with
            {
                Web = webPolicy,
            };
            var doc = defaults with { Permissions = perms };

            var store = new InMemorySettingsStore(doc);
            var bus = new EventBus(NullLogger<EventBus>.Instance);
            Gate = new ToolPermissionGate(store, bus, NullLogger<ToolPermissionGate>.Instance);
        }

        public void Dispose() { }
    }

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        private SettingsDocument _doc;
        public event Action<SettingsDocument>? Changed;

        public InMemorySettingsStore(SettingsDocument doc) => _doc = doc;

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
