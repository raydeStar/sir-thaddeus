using SirThaddeus.Agent.Search;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Default hybrid router: deterministic fast-path + heuristics + LLM classify + fallback.
/// </summary>
public sealed class DefaultRouter : IRouter
{
    private readonly ILlmClient _llm;
    private readonly IDeterministicUtilityEngine _deterministicUtilityEngine;

    private enum ChatIntent
    {
        Casual,
        FactLookup,
        ProductLookup,
        DeepDive,
        NewsLookup,
        Tooling
    }

    public DefaultRouter(
        ILlmClient llm,
        IDeterministicUtilityEngine deterministicUtilityEngine)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _deterministicUtilityEngine = deterministicUtilityEngine ??
                                      throw new ArgumentNullException(nameof(deterministicUtilityEngine));
    }

    public async Task<RouterOutput> RouteAsync(RouterRequest request, CancellationToken cancellationToken = default)
    {
        var userMessage = request?.UserMessage ?? "";
        var lower = userMessage.Trim().ToLowerInvariant();

        if (lower.StartsWith("/browse ", StringComparison.Ordinal) || lower.StartsWith("browse:", StringComparison.Ordinal))
        {
            return MakeRoute(Intents.BrowseOnce, confidence: 1.0, needsWeb: true, needsBrowser: true);
        }

        if (IntentFeatureExtractor.LooksLikeReasoningFollowUp(lower) &&
            request is { HasRecentFirstPrinciplesRationale: true })
        {
            return MakeRoute(Intents.ChatOnly, confidence: 0.92);
        }

        if (IntentFeatureExtractor.LooksLikeLogicPuzzlePrompt(lower))
            return MakeRoute(Intents.ChatOnly, confidence: 0.95);

        if (IntentFeatureExtractor.LooksLikeVoiceMicCheck(lower))
            return MakeRoute(Intents.ChatOnly, confidence: 0.98);

        if (IntentFeatureExtractor.LooksLikeStrayTranscriptFragment(lower))
            return MakeRoute(Intents.ChatOnly, confidence: 0.92);

        if (IntentFeatureExtractor.LooksLikeSelfContainedKnowledgeOrReasoningPrompt(lower))
            return MakeRoute(Intents.ChatOnly, confidence: 0.96);

        if (SearchModeRouter.IsFollowUpMessage(lower) &&
            request is { HasRecentSearchResults: true })
        {
            return MakeRoute(Intents.LookupSearch, confidence: 0.95, needsWeb: true, needsSearch: true, needsBrowser: true);
        }

        if (IntentFeatureExtractor.LooksLikeDeepDiveLookup(lower))
            return MakeRoute(Intents.LookupDeepDive, confidence: 0.95, needsWeb: true, needsSearch: true, needsBrowser: true);

        if (IntentFeatureExtractor.LooksLikeScreenRequest(lower))
            return MakeRoute(Intents.ScreenObserve, confidence: 0.95, needsScreen: true);

        var deterministicPreRoute = _deterministicUtilityEngine.TryMatch(userMessage);
        if (deterministicPreRoute is not null)
        {
            var confidence = deterministicPreRoute.Confidence == DeterministicMatchConfidence.High
                ? 0.99
                : 0.75;

            return MakeRoute(
                Intents.UtilityDeterministic,
                confidence: confidence,
                needsWeb: false,
                needsSearch: false);
        }

        if (IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lower))
            return MakeRoute(Intents.LookupNews, confidence: 0.93, needsWeb: true, needsSearch: true);

        if (IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lower))
            return MakeRoute(Intents.LookupFact, confidence: 0.93, needsWeb: true, needsSearch: true);

        if (IntentFeatureExtractor.LooksLikeProductRecommendationLookup(lower))
            return MakeRoute(Intents.LookupProduct, confidence: 0.93, needsWeb: true, needsSearch: true);

        if (IntentFeatureExtractor.LooksLikeFactLookup(lower))
            return MakeRoute(
                Intents.LookupFact,
                confidence: ComputeFactLookupConfidence(lower),
                needsWeb: true,
                needsSearch: true);

        var explicitToolIntent = IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(lower);
        if (!string.IsNullOrWhiteSpace(explicitToolIntent))
        {
            return explicitToolIntent switch
            {
                Intents.FileTask => MakeRoute(Intents.FileTask, confidence: 0.97, needsFile: true),
                Intents.ScreenObserve => MakeRoute(Intents.ScreenObserve, confidence: 0.97, needsScreen: true),
                Intents.SystemTask => MakeRoute(Intents.SystemTask, confidence: 0.97, needsSystem: true, risk: "medium"),
                Intents.LookupSearch => MakeRoute(Intents.LookupSearch, confidence: 0.97, needsWeb: true, needsSearch: true, needsBrowser: true),
                Intents.MemoryWrite => MakeRoute(Intents.MemoryWrite, confidence: 0.97, needsMemoryWrite: true),
                _ => MakeRoute(Intents.GeneralTool, confidence: 0.96)
            };
        }

        if (IntentFeatureExtractor.LooksLikeGreetingOnlyOrSmallTalk(lower))
            return MakeRoute(Intents.ChatOnly, confidence: 0.94);

        var intent = await ClassifyIntentAsync(userMessage, cancellationToken);

        return intent switch
        {
            ChatIntent.Casual => MakeRoute(Intents.ChatOnly, confidence: 0.8),
            ChatIntent.FactLookup => MakeRoute(Intents.LookupFact, confidence: 0.88, needsWeb: true, needsSearch: true),
            ChatIntent.ProductLookup => MakeRoute(Intents.LookupProduct, confidence: 0.9, needsWeb: true, needsSearch: true),
            ChatIntent.DeepDive => MakeRoute(Intents.LookupDeepDive, confidence: 0.9, needsWeb: true, needsSearch: true, needsBrowser: true),
            ChatIntent.NewsLookup => MakeRoute(Intents.LookupNews, confidence: 0.88, needsWeb: true, needsSearch: true),
            ChatIntent.Tooling => RefineToolingIntent(lower),
            _ => MakeRoute(Intents.GeneralTool, confidence: 0.3)
        };
    }

    private static RouterOutput RefineToolingIntent(string lower)
    {
        if (IntentFeatureExtractor.LooksLikeMemoryWriteRequest(lower))
            return MakeRoute(Intents.MemoryWrite, confidence: 0.9, needsMemoryWrite: true);

        if (IntentFeatureExtractor.LooksLikeScreenRequest(lower))
            return MakeRoute(Intents.ScreenObserve, confidence: 0.85, needsScreen: true);

        if (IntentFeatureExtractor.LooksLikeFileRequest(lower))
            return MakeRoute(Intents.FileTask, confidence: 0.85, needsFile: true);

        if (IntentFeatureExtractor.LooksLikeSystemCommand(lower))
            return MakeRoute(Intents.SystemTask, confidence: 0.8, needsSystem: true, risk: "medium");

        if (IntentFeatureExtractor.LooksLikeBrowseRequest(lower))
            return MakeRoute(Intents.BrowseOnce, confidence: 0.85, needsWeb: true, needsBrowser: true);

        return MakeRoute(Intents.GeneralTool, confidence: 0.4);
    }

    private async Task<ChatIntent> ClassifyIntentAsync(
        string userMessage, CancellationToken cancellationToken)
    {
        var msg = (userMessage ?? "").Trim();
        if (msg.Length == 0)
            return ChatIntent.Casual;

        var lower = msg.ToLowerInvariant();

        if (lower.StartsWith("/search ", StringComparison.Ordinal) || lower.StartsWith("search:", StringComparison.Ordinal))
            return ChatIntent.FactLookup;

        if (lower.StartsWith("/news ", StringComparison.Ordinal) || lower.StartsWith("news:", StringComparison.Ordinal))
            return ChatIntent.NewsLookup;

        if (lower.StartsWith("/chat ", StringComparison.Ordinal) || lower.StartsWith("chat:", StringComparison.Ordinal))
            return ChatIntent.Casual;

        if (IntentFeatureExtractor.LooksLikeMemoryWriteRequest(lower))
            return ChatIntent.Tooling;

        if (IntentFeatureExtractor.LooksLikeScreenRequest(lower) ||
            IntentFeatureExtractor.LooksLikeFileRequest(lower) ||
            IntentFeatureExtractor.LooksLikeSystemCommand(lower) ||
            IntentFeatureExtractor.LooksLikeBrowseRequest(lower))
        {
            return ChatIntent.Tooling;
        }

        if (IntentFeatureExtractor.LooksLikeLogicPuzzlePrompt(lower))
            return ChatIntent.Casual;

        if (IntentFeatureExtractor.LooksLikeVoiceMicCheck(lower))
            return ChatIntent.Casual;

        if (IntentFeatureExtractor.LooksLikeDeepDiveLookup(lower))
            return ChatIntent.DeepDive;

        if (IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lower))
            return ChatIntent.NewsLookup;

        if (IntentFeatureExtractor.LooksLikeProductRecommendationLookup(lower))
            return ChatIntent.ProductLookup;

        if (IntentFeatureExtractor.LooksLikeFactLookup(lower))
            return ChatIntent.FactLookup;

        const int classifyMaxTokens = 6;
        try
        {
            var messages = new List<ChatMessage>
            {
                ChatMessage.System(
                    "Classify the user message into exactly ONE category. " +
                    "Reply with a single word - nothing else.\n\n" +
                    "chat   = greetings, small talk, opinions, casual conversation\n" +
                    "search = needs current info, news, prices, weather, facts, events, looking something up\n" +
                    "tool   = wants you to interact with their computer (screenshot, read file, run command, " +
                    "remember/save/store/update/correct information, changed their mind about something, take a note)\n\n" +
                    "Reply with: chat, search, or tool"),
                ChatMessage.User(msg)
            };

            var response = await _llm.ChatAsync(
                messages, tools: null, classifyMaxTokens, cancellationToken);

            var raw = (response.Content ?? "").Trim().ToLowerInvariant();
            if (raw.Contains("search", StringComparison.Ordinal))
                return InferSearchIntent(lower);
            if (raw.Contains("tool", StringComparison.Ordinal))
                return ChatIntent.Tooling;
            if (raw.Contains("chat", StringComparison.Ordinal))
                return ChatIntent.Casual;

            return InferFallbackIntent(lower);
        }
        catch
        {
            return InferFallbackIntent(lower);
        }
    }

    private static ChatIntent InferFallbackIntent(string lower)
    {
        if (IntentFeatureExtractor.LooksLikeLogicPuzzlePrompt(lower))
            return ChatIntent.Casual;
        if (IntentFeatureExtractor.LooksLikeVoiceMicCheck(lower))
            return ChatIntent.Casual;
        if (IntentFeatureExtractor.LooksLikeDeepDiveLookup(lower))
            return ChatIntent.DeepDive;
        if (IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lower))
            return ChatIntent.NewsLookup;
        if (IntentFeatureExtractor.LooksLikeProductRecommendationLookup(lower))
            return ChatIntent.ProductLookup;
        if (IntentFeatureExtractor.LooksLikeFactLookup(lower))
            return ChatIntent.FactLookup;
        if (IntentFeatureExtractor.LooksLikeMemoryWriteRequest(lower))
            return ChatIntent.Tooling;
        if (IntentFeatureExtractor.LooksLikeScreenRequest(lower) ||
            IntentFeatureExtractor.LooksLikeFileRequest(lower) ||
            IntentFeatureExtractor.LooksLikeSystemCommand(lower) ||
            IntentFeatureExtractor.LooksLikeBrowseRequest(lower))
        {
            return ChatIntent.Tooling;
        }

        // Safety net: when the LLM classifier returns an unparseable
        // response, questions that look like information requests should
        // route to search rather than defaulting to bare chat.
        if (LooksLikeInformationQuestion(lower))
            return ChatIntent.FactLookup;

        return ChatIntent.Casual;
    }

    /// <summary>
    /// Lightweight check for queries that are clearly asking for
    /// real-world information but didn't match any specific heuristic.
    /// Used as a last-resort before defaulting to casual chat.
    /// </summary>
    private static bool LooksLikeInformationQuestion(string lower)
    {
        if (lower.Length < 15)
            return false;

        if (IntentFeatureExtractor.LooksLikeGreeting(lower))
            return false;

        var hasQuestionMark = lower.Contains('?');
        var hasQuestionWord =
            lower.Contains("can you", StringComparison.Ordinal) ||
            lower.Contains("could you", StringComparison.Ordinal) ||
            lower.Contains("what", StringComparison.Ordinal) ||
            lower.Contains("how", StringComparison.Ordinal) ||
            lower.Contains("where", StringComparison.Ordinal) ||
            lower.Contains("when", StringComparison.Ordinal) ||
            lower.Contains("is it", StringComparison.Ordinal) ||
            lower.Contains("is there", StringComparison.Ordinal) ||
            lower.Contains("are there", StringComparison.Ordinal) ||
            lower.Contains("do you know", StringComparison.Ordinal);

        return hasQuestionMark && hasQuestionWord;
    }

    private static ChatIntent InferSearchIntent(string lower)
        => IntentFeatureExtractor.LooksLikeDeepDiveLookup(lower)
            ? ChatIntent.DeepDive
            : IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lower)
                ? ChatIntent.NewsLookup
                : IntentFeatureExtractor.LooksLikeProductRecommendationLookup(lower)
                    ? ChatIntent.ProductLookup
                    : ChatIntent.FactLookup;

    private static double ComputeFactLookupConfidence(string lower)
    {
        var evidence = IntentFeatureExtractor.GetWebLookupHeuristicEvidence(lower);
        if (evidence.ShouldLookup)
            return Math.Clamp(Math.Max(0.88, evidence.Confidence), 0.88, 0.96);

        return 0.96;
    }

    internal static RouterOutput MakeRoute(
        string intent,
        double confidence = 0.5,
        bool needsWeb = false,
        bool needsBrowser = false,
        bool needsSearch = false,
        bool needsMemoryRead = false,
        bool needsMemoryWrite = false,
        bool needsFile = false,
        bool needsScreen = false,
        bool needsSystem = false,
        string risk = "low")
    {
        var requiredCapabilities = BuildRequiredCapabilities(
            intent,
            needsWeb,
            needsBrowser,
            needsSearch,
            needsMemoryRead,
            needsMemoryWrite,
            needsFile,
            needsScreen,
            needsSystem);

        return new RouterOutput
        {
            Intent = intent,
            Confidence = confidence,
            NeedsWeb = needsWeb,
            NeedsBrowserAutomation = needsBrowser,
            NeedsSearch = needsSearch,
            NeedsMemoryRead = needsMemoryRead,
            NeedsMemoryWrite = needsMemoryWrite,
            NeedsFileAccess = needsFile,
            NeedsScreenRead = needsScreen,
            NeedsSystemExecute = needsSystem,
            RequiredCapabilities = requiredCapabilities,
            RiskLevel = risk
        };
    }

    private static IReadOnlyList<ToolCapability> BuildRequiredCapabilities(
        string intent,
        bool needsWeb,
        bool needsBrowser,
        bool needsSearch,
        bool needsMemoryRead,
        bool needsMemoryWrite,
        bool needsFile,
        bool needsScreen,
        bool needsSystem)
    {
        var capabilities = new HashSet<ToolCapability>();

        if (intent.Equals(Intents.UtilityDeterministic, StringComparison.OrdinalIgnoreCase))
            capabilities.Add(ToolCapability.DeterministicUtility);

        if (intent.Equals(Intents.GeneralTool, StringComparison.OrdinalIgnoreCase))
        {
            capabilities.Add(ToolCapability.MemoryRead);
            capabilities.Add(ToolCapability.Meta);
        }

        if (needsWeb || needsSearch)
            capabilities.Add(ToolCapability.WebSearch);
        if (needsBrowser)
            capabilities.Add(ToolCapability.BrowserNavigate);
        if (needsMemoryRead)
            capabilities.Add(ToolCapability.MemoryRead);
        if (needsMemoryWrite)
            capabilities.Add(ToolCapability.MemoryWrite);
        if (needsFile)
            capabilities.Add(ToolCapability.FileRead);
        if (needsScreen)
            capabilities.Add(ToolCapability.ScreenCapture);
        if (needsSystem)
            capabilities.Add(ToolCapability.SystemExecute);

        return capabilities.ToList();
    }
}
