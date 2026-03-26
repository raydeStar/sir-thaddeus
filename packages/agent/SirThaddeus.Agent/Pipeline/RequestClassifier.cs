using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Classifies preprocessed intents by routing each through the existing
/// IRouter + PolicyGate pipeline. Maps router intent strings to pipeline
/// intent types for downstream stage dispatch.
/// </summary>
public sealed class RequestClassifier : IRequestClassifier
{
    private readonly IRouter _router;

    public RequestClassifier(IRouter router)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
    }

    public async Task<ClassifierResult> ClassifyAsync(
        PreprocessorResult preprocessed,
        ClassifierContext? context = null,
        CancellationToken cancellationToken = default)
    {
        if (preprocessed.Intents.Count == 0)
        {
            return new ClassifierResult
            {
                ClassifiedIntents = [],
                AllDeterministic = true
            };
        }

        var classified = new List<ClassifiedIntent>(preprocessed.Intents.Count);
        var allDeterministic = true;

        foreach (var intent in preprocessed.Intents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var routerRequest = new RouterRequest
            {
                UserMessage = intent.NormalizedRequest,
                HasRecentFirstPrinciplesRationale = context?.HasRecentFirstPrinciplesRationale ?? false,
                HasRecentSearchResults = context?.HasRecentSearchResults ?? false
            };

            var routerOutput = await _router.RouteAsync(routerRequest, cancellationToken);
            var policy = PolicyGate.Evaluate(routerOutput);
            var mappedType = MapIntentToType(routerOutput.Intent);

            // If it went through LLM classification (confidence < 0.9), it's not deterministic
            if (routerOutput.Confidence < 0.9)
                allDeterministic = false;

            classified.Add(new ClassifiedIntent
            {
                Source = intent,
                ResolvedIntent = routerOutput.Intent,
                RouterOutput = routerOutput,
                Policy = policy,
                MappedType = mappedType,
                Confidence = routerOutput.Confidence
            });
        }

        return new ClassifierResult
        {
            ClassifiedIntents = classified,
            AllDeterministic = allDeterministic
        };
    }

    internal static PipelineIntentType MapIntentToType(string intent)
    {
        return intent switch
        {
            Intents.ChatOnly => PipelineIntentType.Chat,
            Intents.UtilityDeterministic => PipelineIntentType.Chat,
            Intents.LookupSearch => PipelineIntentType.WebSearch,
            Intents.LookupFact => PipelineIntentType.WebSearch,
            Intents.LookupNews => PipelineIntentType.WebSearch,
            Intents.LookupDeepDive => PipelineIntentType.WebSearch,
            Intents.BrowseOnce => PipelineIntentType.WebSearch,
            Intents.OneShotDiscovery => PipelineIntentType.WebSearch,
            Intents.ScreenObserve => PipelineIntentType.McpCall,
            Intents.FileTask => PipelineIntentType.FileRead,
            Intents.SystemTask => PipelineIntentType.CodeExecution,
            Intents.MemoryRead => PipelineIntentType.FileRead,
            Intents.MemoryWrite => PipelineIntentType.FileWrite,
            Intents.GeneralTool => PipelineIntentType.McpCall,
            _ => PipelineIntentType.Unknown
        };
    }
}
