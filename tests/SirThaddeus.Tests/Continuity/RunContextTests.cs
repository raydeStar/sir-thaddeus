using SirThaddeus.Agent.Orchestration.Correlation;

namespace SirThaddeus.Tests.Continuity;

public sealed class RunContextTests
{
    // ── CorrelationId ────────────────────────────────────────────────

    [Fact]
    public void CorrelationId_New_Produces12CharHex()
    {
        var id = CorrelationId.New();
        Assert.Equal(12, id.Value.Length);
        Assert.True(id.Value.All(c => "0123456789abcdef".Contains(c)));
    }

    [Fact]
    public void CorrelationId_New_ProducesUniqueValues()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => CorrelationId.New().Value).ToHashSet();
        Assert.Equal(100, ids.Count);
    }

    [Fact]
    public void CorrelationId_ImplicitStringConversion()
    {
        var id = new CorrelationId("abc123def456");
        string s = id;
        Assert.Equal("abc123def456", s);
    }

    [Fact]
    public void CorrelationId_ToString_ReturnsValue()
    {
        var id = new CorrelationId("test12345678");
        Assert.Equal("test12345678", id.ToString());
    }

    [Fact]
    public void CorrelationId_NullValue_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CorrelationId(null!));
    }

    [Fact]
    public void CorrelationId_EqualityByValue()
    {
        var a = new CorrelationId("aabbccddee11");
        var b = new CorrelationId("aabbccddee11");
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    // ── RunContext Lifecycle ──────────────────────────────────────────

    [Fact]
    public void RunContext_New_HasFreshCorrelationId()
    {
        var ctx = RunContext.New();
        Assert.False(string.IsNullOrEmpty(ctx.CorrelationId.Value));
        Assert.Equal(12, ctx.CorrelationId.Value.Length);
    }

    [Fact]
    public void RunContext_WithId_UsesProvidedId()
    {
        var id = new CorrelationId("custom123456");
        var ctx = RunContext.WithId(id);
        Assert.Equal("custom123456", ctx.CorrelationId.Value);
    }

    [Fact]
    public void RunContext_StartedAt_IsReasonable()
    {
        var before = DateTimeOffset.UtcNow;
        var ctx = RunContext.New();
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(ctx.StartedAt, before, after);
    }

    [Fact]
    public void RunContext_Elapsed_Advances()
    {
        var ctx = RunContext.New();
        Thread.Sleep(10);
        Assert.True(ctx.Elapsed.TotalMilliseconds >= 5);
    }

    [Fact]
    public void RunContext_Stop_FreezesElapsed()
    {
        var ctx = RunContext.New();
        Thread.Sleep(10);
        ctx.Stop();
        var frozen = ctx.Elapsed;
        Thread.Sleep(20);
        Assert.Equal(frozen, ctx.Elapsed);
    }

    // ── Budget Tracking ──────────────────────────────────────────────

    [Fact]
    public void RunContext_DefaultBudgets()
    {
        var ctx = RunContext.New();
        Assert.Equal(20, ctx.MaxToolCalls);
        Assert.Equal(10, ctx.MaxLlmRoundTrips);
        Assert.Equal(2, ctx.MaxRepairs);
    }

    [Fact]
    public void RunContext_CustomBudgets()
    {
        var ctx = RunContext.New(maxToolCalls: 5, maxLlmRoundTrips: 3, maxRepairs: 1);
        Assert.Equal(5, ctx.MaxToolCalls);
        Assert.Equal(3, ctx.MaxLlmRoundTrips);
        Assert.Equal(1, ctx.MaxRepairs);
    }

    [Fact]
    public void RecordToolCall_IncrementsCount()
    {
        var ctx = RunContext.New();
        Assert.Equal(0, ctx.ToolCallCount);
        Assert.True(ctx.RecordToolCall());
        Assert.Equal(1, ctx.ToolCallCount);
    }

    [Fact]
    public void RecordToolCall_ReturnsFalseWhenExhausted()
    {
        var ctx = RunContext.New(maxToolCalls: 2);
        Assert.True(ctx.RecordToolCall());
        Assert.True(ctx.RecordToolCall());
        Assert.True(ctx.ToolBudgetExhausted);
        Assert.False(ctx.RecordToolCall());
        Assert.Equal(2, ctx.ToolCallCount);
    }

    [Fact]
    public void RecordLlmRoundTrip_IncrementsCount()
    {
        var ctx = RunContext.New();
        Assert.True(ctx.RecordLlmRoundTrip());
        Assert.Equal(1, ctx.LlmRoundTripCount);
    }

    [Fact]
    public void RecordLlmRoundTrip_ReturnsFalseWhenExhausted()
    {
        var ctx = RunContext.New(maxLlmRoundTrips: 1);
        Assert.True(ctx.RecordLlmRoundTrip());
        Assert.True(ctx.LlmBudgetExhausted);
        Assert.False(ctx.RecordLlmRoundTrip());
        Assert.Equal(1, ctx.LlmRoundTripCount);
    }

    [Fact]
    public void RecordRepair_IncrementsCount()
    {
        var ctx = RunContext.New();
        Assert.True(ctx.RecordRepair());
        Assert.Equal(1, ctx.RepairCount);
    }

    [Fact]
    public void RecordRepair_ReturnsFalseWhenExhausted()
    {
        var ctx = RunContext.New(maxRepairs: 1);
        Assert.True(ctx.RecordRepair());
        Assert.True(ctx.RepairBudgetExhausted);
        Assert.False(ctx.RecordRepair());
        Assert.Equal(1, ctx.RepairCount);
    }

    [Fact]
    public void RunContext_InitialState_NoBudgetExhausted()
    {
        var ctx = RunContext.New();
        Assert.False(ctx.ToolBudgetExhausted);
        Assert.False(ctx.LlmBudgetExhausted);
        Assert.False(ctx.RepairBudgetExhausted);
        Assert.Equal(0, ctx.ToolCallCount);
        Assert.Equal(0, ctx.LlmRoundTripCount);
        Assert.Equal(0, ctx.RepairCount);
    }

    [Fact]
    public void RunContext_Intent_DefaultsToEmpty()
    {
        var ctx = RunContext.New();
        Assert.Equal(string.Empty, ctx.Intent);
    }

    [Fact]
    public void RunContext_Intent_CanBeSet()
    {
        var ctx = RunContext.New();
        ctx.Intent = "LookupFact";
        Assert.Equal("LookupFact", ctx.Intent);
    }

    [Fact]
    public void RunContext_ToString_ContainsCorrelationId()
    {
        var id = new CorrelationId("test00000000");
        var ctx = RunContext.WithId(id);
        ctx.Intent = "ChatOnly";
        var s = ctx.ToString();
        Assert.Contains("test00000000", s);
        Assert.Contains("ChatOnly", s);
        Assert.Contains("tools=0/20", s);
    }
}
