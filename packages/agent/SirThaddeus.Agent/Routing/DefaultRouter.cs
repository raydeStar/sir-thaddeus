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

        if (SearchModeRouter.IsFollowUpMessage(lower) &&
            request is { HasRecentSearchResults: true })
        {
            return MakeRoute(Intents.LookupSearch, confidence: 0.95, needsWeb: true, needsSearch: true, needsBrowser: true);
        }

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

        if (IntentFeatureExtractor.LooksLikeFactLookup(lower))
            return MakeRoute(Intents.LookupFact, confidence: 0.9, needsWeb: true, needsSearch: true);

        var intent = await ClassifyIntentAsync(userMessage, cancellationToken);

        return intent switch
        {
            ChatIntent.Casual => MakeRoute(Intents.ChatOnly, confidence: 0.8),
            ChatIntent.FactLookup => MakeRoute(Intents.LookupFact, confidence: 0.88, needsWeb: true, needsSearch: true),
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

        if (IntentFeatureExtractor.LooksLikeLogicPuzzlePrompt(lower))
            return ChatIntent.Casual;

        if (IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lower))
            return ChatIntent.NewsLookup;

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
        if (IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lower))
            return ChatIntent.NewsLookup;
        if (IntentFeatureExtractor.LooksLikeFactLookup(lower))
            return ChatIntent.FactLookup;
        if (IntentFeatureExtractor.LooksLikeMemoryWriteRequest(lower))
            return ChatIntent.Tooling;
        return ChatIntent.Casual;
    }

    private static ChatIntent InferSearchIntent(string lower)
        => IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lower)
            ? ChatIntent.NewsLookup
            : ChatIntent.FactLookup;

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

