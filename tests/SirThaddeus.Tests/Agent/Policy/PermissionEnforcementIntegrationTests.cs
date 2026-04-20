using Microsoft.Extensions.Time.Testing;
using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.PermissionBroker;
using SirThaddeus.ToolRunner;

namespace SirThaddeus.Tests.Agent.Policy;

/// <summary>
/// End-to-end tests that exercise the permission model along the actual
/// enforcement paths in production code: the agent-side gate check in
/// <see cref="AuditedMcpToolClient"/> and the runtime-side token validation
/// in <see cref="EnforcingToolRunner"/>. These complement the unit tests in
/// PermissionBrokerTests by proving the gate-denial, token-expiry, and
/// STOP-ALL branches actually stop tools from running — which is the
/// brand promise ("if you press STOP, it stops").
/// </summary>
public sealed class PermissionEnforcementIntegrationTests
{
    // ─────────────────────────────────────────────────────────────────
    // AuditedMcpToolClient: gate-denial path
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GateDenial_BlocksToolExecution_AndAuditsReason()
    {
        var audit = new TestAuditLogger();
        var inner = new RecordingMcpToolClient("unreachable");
        var sut = new AuditedMcpToolClient(
            inner,
            audit,
            new AlwaysDenyGate("user said no"),
            sessionId: "session-1");

        var result = await sut.CallToolAsync("weather_lookup", "{}");

        Assert.Contains("blocked", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user said no", result);
        Assert.Equal(0, inner.CallCount);

        var events = audit.ReadTail(100);
        Assert.Contains(events, e =>
            e.Action == "MCP_TOOL_CALL_END" &&
            e.Result == "blocked");
    }

    [Fact]
    public async Task GateGrant_AllowsToolExecution_AndAuditsApproval()
    {
        var audit = new TestAuditLogger();
        var inner = new RecordingMcpToolClient("42°F");
        var sut = new AuditedMcpToolClient(
            inner,
            audit,
            new AlwaysGrantGate(),
            sessionId: "session-2");

        var result = await sut.CallToolAsync("weather_lookup", "{}");

        Assert.Equal("42°F", result);
        Assert.Equal(1, inner.CallCount);

        var events = audit.ReadTail(100);
        Assert.Contains(events, e =>
            e.Action == "MCP_TOOL_CALL_END" &&
            e.Result == "ok");
    }

    [Fact]
    public async Task BrokerIssuingGate_RecordsTokenIdInAuditLog()
    {
        // Proves the gate → broker handoff: when the gate issues a real
        // token, the AuditedMcpToolClient's audit events carry that token
        // id forward for post-mortem correlation.
        var audit = new TestAuditLogger();
        var broker = new InMemoryPermissionBroker(audit, new FakeTimeProvider(DateTimeOffset.UtcNow));
        var inner = new RecordingMcpToolClient("ok");
        var gate = new BrokerIssuingGate(broker, Capability.WebAccess);

        var sut = new AuditedMcpToolClient(inner, audit, gate, sessionId: "session-3");
        await sut.CallToolAsync("web_search", "{}");

        Assert.Single(gate.IssuedTokenIds);
        var issuedTokenId = gate.IssuedTokenIds[0];

        var events = audit.ReadTail(100);
        var endEvent = Assert.Single(events, e => e.Action == "MCP_TOOL_CALL_END");
        Assert.Equal(issuedTokenId, endEvent.PermissionTokenId);
    }

    // ─────────────────────────────────────────────────────────────────
    // EnforcingToolRunner: token-expiry and STOP-ALL paths
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExpiredToken_FailsValidationAtToolRunner_AndToolNeverExecutes()
    {
        var audit = new TestAuditLogger();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var broker = new InMemoryPermissionBroker(audit, time);

        var token = broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.WebAccess,
            Purpose = "integration test",
            Duration = TimeSpan.FromSeconds(30),
        });

        var runner = new EnforcingToolRunner(broker, audit);
        var tool = new RecordingTool("probe", Capability.WebAccess);
        runner.RegisterTool(tool);

        // Advance past expiry. Per-call token validation should reject
        // the now-stale token before the tool can run.
        time.Advance(TimeSpan.FromSeconds(60));

        var result = await runner.ExecuteAsync(new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "probe",
            RequiredCapability = Capability.WebAccess,
            Purpose = "post-expiry call",
        }, tokenId: token.Id);

        Assert.False(result.Success);
        Assert.Contains("Permission denied", result.Error ?? "");
        Assert.Equal(0, tool.ExecuteCount);
    }

    [Fact]
    public async Task RevokeAll_StopSwitch_InvalidatesActiveToken_AndBlocksNextCall()
    {
        var audit = new TestAuditLogger();
        var broker = new InMemoryPermissionBroker(audit, new FakeTimeProvider(DateTimeOffset.UtcNow));

        var token = broker.IssueToken(new PermissionRequest
        {
            Capability = Capability.WebAccess,
            Purpose = "mid-loop token",
            Duration = TimeSpan.FromMinutes(5),
        });
        Assert.True(broker.Validate(token.Id, Capability.WebAccess).IsValid,
            "Sanity: newly issued token should validate before STOP");

        var revokedCount = broker.RevokeAll("user pressed STOP");
        Assert.Equal(1, revokedCount);

        var runner = new EnforcingToolRunner(broker, audit);
        var tool = new RecordingTool("probe", Capability.WebAccess);
        runner.RegisterTool(tool);

        var result = await runner.ExecuteAsync(new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "probe",
            RequiredCapability = Capability.WebAccess,
            Purpose = "post-STOP call",
        }, tokenId: token.Id);

        Assert.False(result.Success);
        Assert.Contains("Permission denied", result.Error ?? "");
        Assert.Equal(0, tool.ExecuteCount);

        // The STOP itself must be auditable so operators can prove after
        // the fact that a revoke happened and which tokens were affected.
        var events = audit.ReadTail(100);
        Assert.Contains(events, e => e.Action == "PERMISSION_REVOKE_ALL");
    }

    [Fact]
    public async Task MissingToken_AtToolRunner_IsRejectedWithoutCallingBroker()
    {
        var audit = new TestAuditLogger();
        var broker = new InMemoryPermissionBroker(audit, new FakeTimeProvider(DateTimeOffset.UtcNow));
        var runner = new EnforcingToolRunner(broker, audit);
        var tool = new RecordingTool("probe", Capability.WebAccess);
        runner.RegisterTool(tool);

        var result = await runner.ExecuteAsync(new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "probe",
            RequiredCapability = Capability.WebAccess,
            Purpose = "no-token call",
        }, tokenId: null);

        Assert.False(result.Success);
        Assert.Contains("Permission denied", result.Error ?? "");
        Assert.Equal(0, tool.ExecuteCount);
    }

    // ─────────────────────────────────────────────────────────────────
    // Test doubles
    // ─────────────────────────────────────────────────────────────────

    private sealed class RecordingMcpToolClient : IMcpToolClient
    {
        private readonly string _output;

        public RecordingMcpToolClient(string output) => _output = output;

        public int CallCount { get; private set; }

        public Task<string> CallToolAsync(
            string toolName, string argumentsJson, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_output);
        }

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>([]);
    }

    private sealed class RecordingTool : ITool
    {
        public RecordingTool(string name, Capability requiredCapability)
        {
            Name = name;
            RequiredCapability = requiredCapability;
        }

        public string Name { get; }
        public string Description => "Test tool that records its executions.";
        public Capability RequiredCapability { get; }
        public int ExecuteCount { get; private set; }

        public Task<object?> ExecuteAsync(ToolExecutionContext context)
        {
            ExecuteCount++;
            return Task.FromResult<object?>("ran");
        }
    }

    /// <summary>
    /// Gate that forwards every check through a real broker, returning a
    /// granted result carrying the issued token id. Used to prove the
    /// gate → broker handoff end-to-end.
    /// </summary>
    private sealed class BrokerIssuingGate : IToolPermissionGate
    {
        private readonly IPermissionBroker _broker;
        private readonly Capability _capability;

        public BrokerIssuingGate(IPermissionBroker broker, Capability capability)
        {
            _broker = broker;
            _capability = capability;
        }

        public List<string> IssuedTokenIds { get; } = new();

        public Task<ToolPermissionResult> CheckAsync(
            string toolName, string argumentsJson, CancellationToken ct)
        {
            var token = _broker.IssueToken(new PermissionRequest
            {
                Capability = _capability,
                Purpose = $"tool: {toolName}",
                Duration = TimeSpan.FromSeconds(30),
            });
            IssuedTokenIds.Add(token.Id);
            return Task.FromResult(ToolPermissionResult.Grant(token.Id));
        }
    }
}
