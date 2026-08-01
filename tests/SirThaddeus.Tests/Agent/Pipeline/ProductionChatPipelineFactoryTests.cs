using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;

namespace SirThaddeus.Tests.Agent.Pipeline;

public sealed class ProductionChatPipelineFactoryTests
{
    private static readonly string[] SharedStageOrder =
    [
        "SafetyBoundary",
        "PolicyStateUtility",
        "UtilityFastPath",
        "BenignFallback",
        "PersonalityInjection",
        "FeatureExtractor",
        "LogicPuzzleScaffold",
        "MemoryContext",
        "OnboardingInjection",
        "DialogueState",
        "ExistenceVerificationHint",
        "FootmanRouter",
        "Guardrails",
        "FreshnessRouter",
        "ToolLoop",
        "PostProcess:Sanitize",
        "CompletionValidation",
        "SearchFallback",
        "PostProcess:SearchFallbackSanitize",
        "AutoMemoryExtract",
        "ResponseComposer",
    ];

    [Fact]
    public void Build_owns_the_canonical_stage_order()
    {
        var pipeline = ProductionChatPipelineFactory.Build(NewOptions());

        Assert.Equal(SharedStageOrder, pipeline.Steps.Select(step => step.Name));
    }

    [Fact]
    public void Build_places_optional_core_memory_after_dynamic_memory()
    {
        var pipeline = ProductionChatPipelineFactory.Build(
            NewOptions(includeCoreMemory: true));
        var expected = SharedStageOrder.ToList();
        expected.Insert(expected.IndexOf("MemoryContext") + 1, "CoreMemory");

        Assert.Equal(expected, pipeline.Steps.Select(step => step.Name));
    }

    private static ProductionChatPipelineOptions NewOptions(bool includeCoreMemory = false)
    {
        var mcp = new FakeMcpClient("{}");
        return new ProductionChatPipelineOptions
        {
            Mcp = mcp,
            ToolLoop = new ToolLoopStep(new FakeLlmClient("ok"), mcp),
            Sanitize = (_, draft) => draft,
            IncludeCoreMemoryStep = includeCoreMemory,
        };
    }
}
