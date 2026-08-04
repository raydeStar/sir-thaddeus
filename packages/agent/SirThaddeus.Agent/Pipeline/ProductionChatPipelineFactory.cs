using SirThaddeus.Agent;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.Validation;
using SirThaddeus.Memory;
using SirThaddeus.PersonalityEngine;

namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Host-provided collaborators for the supported production chat pipeline.
/// Desktop, console, and harness hosts supply their boundary adapters here;
/// the factory owns the common stage set and its security-sensitive order.
/// </summary>
public sealed record ProductionChatPipelineOptions
{
    public required IMcpToolClient Mcp { get; init; }
    public required ToolLoopStep ToolLoop { get; init; }
    public required Func<TurnContext, string, string> Sanitize { get; init; }

    public IChatEventSink? EventSink { get; init; }
    public IChatEventSink? PolicyStateEventSink { get; init; }
    public Action<string, string>? PolicyStateLog { get; init; }
    public Action<string, string>? LogEvent { get; init; }
    public ITurnExecutionControl? ExecutionControl { get; init; }

    public Func<string?>? ResolveActiveProfileId { get; init; }
    public IPersonalityRuntime? PersonalityRuntime { get; init; }
    public IMemoryContextProvider? MemoryContextProvider { get; init; }
    public Func<TurnContext, MemoryContextRequest>? MemoryRequestBuilder { get; init; }
    public MemoryContextStep.MemoryRecalledHandler? OnMemoryRecalled { get; init; }
    public bool IncludeCoreMemoryStep { get; init; }
    public IMemoryStore? CoreMemoryStore { get; init; }
    public IDialogueStateAccessor? DialogueStateAccessor { get; init; }

    public IFootmanRouter? FootmanRouter { get; init; }
    public IReadOnlyList<string>? AlwaysAllowToolNames { get; init; }
    public ReasoningGuardrailsPipeline? GuardrailsPipeline { get; init; }
    public CompletionValidator? CompletionValidator { get; init; }
    public RepairLoop? CompletionRepairLoop { get; init; }
    public ISearchFallbackExecutor? SearchFallbackExecutor { get; init; }
    public IAutoMemoryExtractor? AutoMemoryExtractor { get; init; }
    public Func<TurnContext, string?>? ActiveProfileIdGetter { get; init; }
}

/// <summary>
/// Builds the one supported production pipeline shared by every host.
/// Host-specific behavior belongs in the supplied ports, not in a duplicated
/// stage list.
/// </summary>
public static class ProductionChatPipelineFactory
{
    public static ChatPipeline Build(ProductionChatPipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Mcp);
        ArgumentNullException.ThrowIfNull(options.ToolLoop);
        ArgumentNullException.ThrowIfNull(options.Sanitize);

        var sink = options.EventSink ?? NullChatEventSink.Instance;
        var steps = new List<ITurnStep>
        {
            new SafetyBoundaryStep(options.ResolveActiveProfileId),
            new PolicyStateUtilityStep(
                options.Mcp,
                options.PolicyStateEventSink,
                options.PolicyStateLog),
            new UtilityFastPathStep(),
            new BenignFallbackStep(),
            new PersonalityInjectionStep(options.PersonalityRuntime),
            new FeatureExtractorStep(),
            new LogicPuzzleScaffoldStep(),
            new MemoryContextStep(
                options.MemoryContextProvider,
                options.MemoryRequestBuilder,
                options.OnMemoryRecalled),
        };

        // The desktop currently has a direct IMemoryStore for pinned core
        // memory. The legacy console host does not. Keep that host difference
        // explicit while preserving the common ordering contract.
        if (options.IncludeCoreMemoryStep)
            steps.Add(new CoreMemoryStep(options.CoreMemoryStore));

        steps.AddRange(
        [
            new OnboardingInjectionStep(ctx => ctx.IsNewUser
                ? OnboardingMode.Cold
                : OnboardingMode.NotNeeded),
            new DialogueStateStep(options.DialogueStateAccessor),
            new ExistenceVerificationHintStep(),
            new FootmanRouterStep(
                options.FootmanRouter,
                sink,
                options.AlwaysAllowToolNames),
            new GuardrailsStep(options.GuardrailsPipeline),
            new FreshnessRouterStep(),
            options.ToolLoop,
            new PostProcessStep(options.Sanitize, "PostProcess:Sanitize"),
            new CompletionValidationStep(
                options.CompletionValidator,
                options.CompletionRepairLoop,
                options.LogEvent),
            new SearchFallbackStep(
                options.SearchFallbackExecutor,
                BuildSearchFallbackRequest),
            new PostProcessStep(options.Sanitize, "PostProcess:SearchFallbackSanitize"),
            new AutoMemoryExtractStep(
                options.AutoMemoryExtractor,
                options.ActiveProfileIdGetter),
            new ResponseComposerStep(options.LogEvent),
        ]);

        return new ChatPipeline(steps, options.LogEvent, options.ExecutionControl);
    }

    private static SearchFallbackRequest? BuildSearchFallbackRequest(TurnContext context)
    {
        if (!context.ToolDefs.Any(def =>
                string.Equals(
                    def.Function?.Name,
                    ToolNames.WebSearch,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var draft = context.AssistantDraft ?? string.Empty;
        if (LooksLikeCompletedWeatherNewsEvidenceDraft(draft))
            return null;

        var refusal = RefusalDetector.HasRefusalOrUncertaintySignals(draft, draft);
        var hedged = HedgeSignalDetector.ShouldVerify(draft, context.UserText);
        if (!refusal && !hedged)
            return null;

        return new SearchFallbackRequest
        {
            UserMessage = context.UserText ?? string.Empty,
            History = context.LlmMessages.ToList(),
            ToolCallsMade = context.ToolCallsMade.ToList(),
            HasRefusalOrUncertaintySignals = true,
        };
    }

    private static bool LooksLikeCompletedWeatherNewsEvidenceDraft(string draft)
    {
        if (string.IsNullOrWhiteSpace(draft))
            return false;

        var lower = draft.ToLowerInvariant();
        return lower.Contains("weather in ", StringComparison.Ordinal) &&
               lower.Contains("local news in ", StringComparison.Ordinal) &&
               (lower.Contains("current conditions are", StringComparison.Ordinal) ||
                lower.Contains("live forecast lookup returned", StringComparison.Ordinal)) &&
               (lower.Contains("live search returned", StringComparison.Ordinal) ||
                lower.Contains("live search did not return", StringComparison.Ordinal));
    }
}
