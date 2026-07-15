using SirThaddeus.LlmClient;
using Thaddeus.Runtime.Chat;

namespace Thaddeus.Runtime.Tests;

public sealed class LlmRuntimeRegistryTests
{
    [Fact]
    public void SetPrimary_ExposesUsageFromTheSameTransport()
    {
        var registry = new LlmRuntimeRegistry();
        registry.SetPrimary(new DiagnosticsWithUsage());

        var usage = registry.GetUsageSnapshot();

        Assert.Equal(3, usage.RequestCount);
        Assert.Equal(120, usage.PromptTokens);
        Assert.Equal(24, usage.CompletionTokens);
        Assert.Equal(144, usage.TotalTokens);
        Assert.Equal(8192, usage.ContextWindowTokens);
    }

    [Fact]
    public void SetPrimary_WithoutUsageTelemetry_ClearsPriorUsageSource()
    {
        var registry = new LlmRuntimeRegistry();
        registry.SetPrimary(new DiagnosticsWithUsage());
        registry.SetPrimary(new DiagnosticsOnly());

        Assert.Equal(0, registry.GetUsageSnapshot().RequestCount);
    }

    private sealed class DiagnosticsWithUsage : ILlmRuntimeDiagnostics, ILlmUsageTelemetry
    {
        public LlmRuntimeHealthSnapshot GetRuntimeHealthSnapshot() => new();

        public LlmUsageSnapshot GetUsageSnapshot() => new()
        {
            RequestCount = 3,
            PromptTokens = 120,
            CompletionTokens = 24,
            TotalTokens = 144,
            ContextWindowTokens = 8192,
        };
    }

    private sealed class DiagnosticsOnly : ILlmRuntimeDiagnostics
    {
        public LlmRuntimeHealthSnapshot GetRuntimeHealthSnapshot() => new();
    }
}
