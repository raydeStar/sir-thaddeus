using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static SirThaddeus.Agent.OrchestratorMessageHelpers;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Context;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.PostProcessing;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.ConversationSegmentation;
using SirThaddeus.Agent.ToolLoop;
using SirThaddeus.Agent.Tools;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine;
using SirThaddeus.PersonalityEngine.Formatting;
using SirThaddeus.PersonalityEngine.Profiles;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator : IAgentOrchestrator
{
    private readonly ILlmClient _llm;
    private readonly IMcpToolClient _mcp;
    private readonly IAuditLogger _audit;
    private readonly string _systemPrompt;
    private readonly TimeProvider _timeProvider;

    private readonly List<ChatMessage> _history = [];

    private readonly SearchOrchestrator _searchOrchestrator;
    private readonly IDialogueStateStore _dialogueStore;
    private readonly SlotExtract _slotExtract;
    private readonly MergeSlots _mergeSlots;
    private readonly ValidateSlots _validateSlots;
    private readonly IToolPlanner _toolPlanner;
    private readonly ReasoningGuardrailsPipeline _reasoningGuardrailsPipeline;
    private readonly IRouter _router;
    private readonly IMemoryContextProvider _memoryContextProvider;
    private readonly IToolLoopExecutor _toolLoopExecutor;
    private readonly IDeterministicUtilityEngine _deterministicUtilityEngine;
    private readonly IGuardrailsCoordinator _guardrailsCoordinator;
    private readonly ToolDefinitionBuilder _toolDefinitionBuilder;
    private readonly DeterministicChatPostProcessor _postProcessor;
    private readonly ISelfMemorySummarizer _selfMemorySummarizer;
    private readonly ISearchFallbackExecutor _searchFallbackExecutor;
    private readonly IContextAnchoringService _contextAnchoringService;
    private readonly IUtilityIntentHandler _utilityIntentHandler;
    private readonly IConversationSegmenter _conversationSegmenter;
    private readonly MiniActionableExtractor _miniActionableExtractor;
    private readonly SegmentExecutionCoordinator _segmentExecutionCoordinator;
    private readonly UnifiedResponseComposer _unifiedResponseComposer;
    private readonly ResponseKindClassifier _responseKindClassifier = new();
    private readonly Tools.ToolAliasResolver _toolAliasResolver;
    private readonly IFootmanRouter? _footmanRouter;
    private readonly IAutoMemoryExtractor? _autoMemoryExtractor;

    private static readonly AsyncLocal<int> MultiIntentBypassDepth = new();

    private string? _lastPlaceContextName;
    private DateTimeOffset _lastLookupToolCallAt;
    private string? _lastPlaceContextCountryCode;
    private DateTimeOffset _lastPlaceContextAt;
    private string? _lastUtilityContextKey;
    private DateTimeOffset _lastUtilityContextAt;
    private string? _userLocationHint;
    private string? _preferredUnits = "auto";
    private IReadOnlyList<string> _lastFirstPrinciplesRationale = [];
    private DateTimeOffset _lastFirstPrinciplesAt;
    private string? _currentConversationId;
    private string? _currentTurnTag;

    private const int MaxToolRoundTrips  = 10;  // Safety valve
    private const int DefaultWebSearchMaxResults = 5;
    private static readonly TimeSpan PlaceContextTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan UtilityContextTtl = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan FirstPrinciplesFollowUpTtl = TimeSpan.FromMinutes(15);

    private const string WebSearchToolName    = ToolNames.WebSearch;
    private const string WebSearchToolNameAlt = ToolNames.WebSearchAlt;
    private const string WeatherGeocodeToolName    = ToolNames.WeatherGeocode;
    private const string WeatherGeocodeToolNameAlt = ToolNames.WeatherGeocodeAlt;
    private const string WeatherForecastToolName    = ToolNames.WeatherForecast;
    private const string WeatherForecastToolNameAlt = ToolNames.WeatherForecastAlt;
    private const string ResolveTimezoneToolName    = ToolNames.ResolveTimezone;
    private const string ResolveTimezoneToolNameAlt = ToolNames.ResolveTimezoneAlt;
    private const string HolidaysGetToolName        = ToolNames.HolidaysGet;
    private const string HolidaysGetToolNameAlt     = ToolNames.HolidaysGetAlt;
    private const string HolidaysNextToolName       = ToolNames.HolidaysNext;
    private const string HolidaysNextToolNameAlt    = ToolNames.HolidaysNextAlt;
    private const string HolidaysIsTodayToolName    = ToolNames.HolidaysIsToday;
    private const string HolidaysIsTodayToolNameAlt = ToolNames.HolidaysIsTodayAlt;
    private const string FeedFetchToolName          = ToolNames.FeedFetch;
    private const string FeedFetchToolNameAlt       = ToolNames.FeedFetchAlt;
    private const string StatusCheckToolName        = ToolNames.StatusCheck;
    private const string StatusCheckToolNameAlt     = ToolNames.StatusCheckAlt;
    private const string MemoryRetrieveToolName     = ToolNames.MemoryRetrieve;
    private const string MemoryRetrieveToolNameAlt  = ToolNames.MemoryRetrieveAlt;
    private const string MemoryListFactsToolName    = ToolNames.MemoryListFacts;
    private const string MemoryListFactsToolNameAlt = ToolNames.MemoryListFactsAlt;
    private const string MemoryStoreFactsToolName   = ToolNames.MemoryStoreFacts;
    private const string MemoryStoreFactsToolNameAlt = ToolNames.MemoryStoreFactsAlt;
    private const string ScreenCaptureToolName       = ToolNames.ScreenCapture;
    private const string ScreenCaptureToolNameAlt    = ToolNames.ScreenCaptureAlt;

    private const string WebSummaryInstruction = OrchestratorPrompts.WebSummaryInstruction;
    private const string WebFollowUpInstruction = OrchestratorPrompts.WebFollowUpInstruction;
    private const string WebFollowUpWithRelatedInstruction = OrchestratorPrompts.WebFollowUpWithRelatedInstruction;

    private int _maxTokensCasual      = 512;
    private int _maxTokensCasualRetry = 2048;
    private const int MaxTokensWebSummary     = 1024;
    private const int MaxTokensWebSummaryRetry = 2048;
    private const int MaxTokensTooling        = 1024;
    private const int MaxTokensUtilityRouting = 120;

    public int MaxTokensBudget
    {
        get => _maxTokensCasual;
        set
        {
            _maxTokensCasual = value > 0 ? value : 512;
            _maxTokensCasualRetry = Math.Max(_maxTokensCasual, 2048);
        }
    }

    private const string LogicPuzzleDecompositionModeSuffix = OrchestratorPrompts.LogicPuzzleDecompositionModeSuffix;

    private static readonly TimeSpan MemoryRetrievalTimeout = TimeSpan.FromMilliseconds(1500);

    private const string OnboardingColdPrompt = OrchestratorPrompts.OnboardingColdPrompt;
    private const string OnboardingFollowUpPrompt = OrchestratorPrompts.OnboardingFollowUpPrompt;

    private const int MaxHistoryTurns = 12;

    public string? ActiveProfileId { get; set; }

    public bool MemoryEnabled { get; set; } = true;

    public bool PanicModeEnabled { get; set; }

    public bool SafeModeEnabled { get; set; }

    public string? UserLocationHint
    {
        get => _userLocationHint;
        set
        {
            _userLocationHint = value;
            _searchOrchestrator.UserLocationHint = value;
        }
    }

    public string? UserTimezone { get; set; }

    public string? PreferredUnits
    {
        get => _preferredUnits;
        set
        {
            _preferredUnits = NormalizeUnitPreference(value);
            _searchOrchestrator.PreferredUnits = _preferredUnits;
        }
    }

    public void SeedHistory(IEnumerable<(string Role, string Content)> priorMessages)
    {
        foreach (var (role, content) in priorMessages)
        {
            if (string.IsNullOrWhiteSpace(content)) continue;

            switch (role)
            {
                case "user":
                    _history.Add(ChatMessage.User(content));
                    break;
                case "assistant":
                    _history.Add(ChatMessage.Assistant(content));
                    break;
            }
        }

        TrimHistory();
    }

    public bool ContextLocked
    {
        get => _dialogueStore.Get().ContextLocked;
        set
        {
            var current = _dialogueStore.Get();
            if (current.ContextLocked == value)
                return;

            _dialogueStore.Update(current with { ContextLocked = value });
        }
    }

    private enum ChatIntent
    {
        Casual,
        WebLookup,
        Tooling
    }

    public AgentOrchestrator(
        ILlmClient llm,
        IMcpToolClient mcp,
        IAuditLogger audit,
        string systemPrompt,
        TimeProvider? timeProvider = null,
        IDialogueStateStore? dialogueStateStore = null,
        SlotExtract? slotExtract = null,
        MergeSlots? mergeSlots = null,
        ValidateSlots? validateSlots = null,
        IToolPlanner? toolPlanner = null,
        string geocodeMismatchMode = "fallback_previous",
        IRouter? router = null,
        IMemoryContextProvider? memoryContextProvider = null,
        IToolLoopExecutor? toolLoopExecutor = null,
        IDeterministicUtilityEngine? deterministicUtilityEngine = null,
        IGuardrailsCoordinator? guardrailsCoordinator = null,
        ISelfMemorySummarizer? selfMemorySummarizer = null,
        ISearchFallbackExecutor? searchFallbackExecutor = null,
        IContextAnchoringService? contextAnchoringService = null,
        IUtilityIntentHandler? utilityIntentHandler = null,
        IConversationSegmenter? conversationSegmenter = null,
        MiniActionableExtractor? miniActionableExtractor = null,
        SegmentExecutionCoordinator? segmentExecutionCoordinator = null,
        UnifiedResponseComposer? unifiedResponseComposer = null,
        IPersonalityRuntime? personalityRuntime = null,
        string? activePersonalityId = null,
        string? personalityProfilesDirectory = null,
        IFootmanRouter? footmanRouter = null,
        IAutoMemoryExtractor? autoMemoryExtractor = null,
        ILlmClient? gatekeeperLlm = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _systemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _activePersonalityId = NormalizePersonalityId(activePersonalityId);
        _personalityProfilesDirectory = string.IsNullOrWhiteSpace(personalityProfilesDirectory)
            ? ResolveDefaultPersonalityProfilesDirectory()
            : personalityProfilesDirectory.Trim();
        _personalityRuntime = personalityRuntime ?? new PersonalityRuntime(
            _activePersonalityId,
            _personalityProfilesDirectory);

        _searchOrchestrator = new SearchOrchestrator(
            llm,
            mcp,
            audit,
            _personalityRuntime.BuildSystemPrompt(_systemPrompt))
        {
            PreferredUnits = _preferredUnits
        };
        _dialogueStore = dialogueStateStore ?? new DialogueStateStore(_timeProvider);
        _slotExtract = slotExtract ?? new SlotExtract(llm, audit);
        _mergeSlots = mergeSlots ?? new MergeSlots();
        _validateSlots = validateSlots ?? new ValidateSlots(new ValidationOptions
        {
            GeocodeMismatchMode = geocodeMismatchMode
        });
        _toolPlanner = toolPlanner ?? new ToolPlanner();
        
        var effectiveGatekeeper = gatekeeperLlm ?? llm;
        _reasoningGuardrailsPipeline = new ReasoningGuardrailsPipeline(effectiveGatekeeper, audit);
        _deterministicUtilityEngine = deterministicUtilityEngine ?? new DeterministicUtilityEngineAdapter();
        _router = router ?? new Routing.RouterV2(effectiveGatekeeper, _deterministicUtilityEngine);
        _memoryContextProvider = memoryContextProvider ?? new MemoryContextProvider(
            mcp,
            audit,
            new SmartIntentClassifier(effectiveGatekeeper, audit),
            _timeProvider);
        _toolLoopExecutor = toolLoopExecutor ?? new ToolLoopExecutor(llm, mcp);
        _guardrailsCoordinator = guardrailsCoordinator ?? new GuardrailsCoordinator(_reasoningGuardrailsPipeline);
        _toolDefinitionBuilder = new ToolDefinitionBuilder(mcp);
        _postProcessor = new DeterministicChatPostProcessor(() => _personalityRuntime.Snapshot.Profile);
        _selfMemorySummarizer = selfMemorySummarizer ?? new SelfMemorySummarizer(mcp);
        _searchFallbackExecutor = searchFallbackExecutor ?? new SearchFallbackExecutor(_searchOrchestrator);
        _contextAnchoringService = contextAnchoringService ?? new ContextAnchoringService(
            _dialogueStore,
            _searchOrchestrator,
            _timeProvider);
        _utilityIntentHandler = utilityIntentHandler ?? new UtilityIntentHandler();
        _conversationSegmenter = conversationSegmenter ?? new ConversationSegmenter();
        _miniActionableExtractor = miniActionableExtractor ?? new MiniActionableExtractor(_llm);
        _segmentExecutionCoordinator = segmentExecutionCoordinator ?? new SegmentExecutionCoordinator();
        _unifiedResponseComposer = unifiedResponseComposer ?? new UnifiedResponseComposer();
        _toolAliasResolver = new Tools.ToolAliasResolver(mcp);
        _footmanRouter = footmanRouter;
        _autoMemoryExtractor = autoMemoryExtractor;

        _history.Add(ChatMessage.System(BuildEffectiveSystemPrompt()));

        var personalitySnapshot = _personalityRuntime.Snapshot;
        EmitPersonalityAuditSnapshot(personalitySnapshot, _activePersonalityId);
    }

    public Task<AgentResponse> ProcessAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
        => ProcessAsync(userMessage, conversationId: null, cancellationToken);

    public async Task<AgentResponse> ProcessAsync(
        string userMessage,
        string? conversationId,
        CancellationToken cancellationToken = default)
    {
        var usageBaseline = CaptureUsageSnapshot();

        if (string.IsNullOrWhiteSpace(userMessage))
            return AttachContextSnapshot(AgentResponse.FromError("Empty message."), usageBaseline);

        (_mcp as AuditedMcpToolClient)?.NotifyNewTurn();

        _turnSequence++;
        var personalityTurnTag = $"turn-{_turnSequence:000000}";
        _currentTurnTag = personalityTurnTag;
        if (!IsMultiIntentBypassActive() || !string.IsNullOrWhiteSpace(conversationId))
            _currentConversationId = string.IsNullOrWhiteSpace(conversationId)
                ? null
                : conversationId.Trim();

        var personalityTurnContext = _personalityRuntime.BuildTurnContext(userMessage);
        var personalityAnchor = _personalityRuntime.BuildAnchor(personalityTurnTag, userMessage);
        LogEvent(
            "PERSONALITY_CONTEXT",
            $"persona={_personalityRuntime.Snapshot.Profile.Id}, " +
            $"tags=[{string.Join(", ", personalityTurnContext.Tags)}], " +
            $"tone_effective={{formality={personalityTurnContext.EffectiveTone.Formality:0.00}," +
            $"warmth={personalityTurnContext.EffectiveTone.Warmth:0.00}," +
            $"humor={personalityTurnContext.EffectiveTone.Humor:0.00}," +
            $"verbosity={personalityTurnContext.EffectiveTone.Verbosity:0.00}," +
            $"directness={personalityTurnContext.EffectiveTone.Directness:0.00}}}, " +
            $"reduction={{mode={personalityTurnContext.Reduction.Mode},applied={personalityTurnContext.Reduction.Applied}}}");

        var lowerIncoming = userMessage.Trim().ToLowerInvariant();

        if (LooksLikeHighRiskIllicitInstructionRequest(userMessage))
        {
            LogEvent("AGENT_SAFETY_BOUNDARY", "Detected high-risk illicit instruction request.");
            return AttachContextSnapshot(new AgentResponse
            {
                Text = BuildSafetyBoundaryWithAlternativeReply(),
                Success = true,
                ToolCallsMade = [],
                LlmRoundTrips = 0
            }, usageBaseline);
        }

        if (!LooksLikeReasoningFollowUp(lowerIncoming))
        {
            _lastFirstPrinciplesRationale = [];
            _lastFirstPrinciplesAt = default;
        }

        _history.Add(ChatMessage.User(userMessage));
        TrimHistory();
        LogEvent("AGENT_USER_MESSAGE", userMessage);

        if (MemoryEnabled && _autoMemoryExtractor != null && !SafeModeEnabled)
        {
            _autoMemoryExtractor.FireAndForgetExtraction(userMessage, ActiveProfileId, personalityTurnTag);
            _autoMemoryExtractor.FireAndForgetConversationChunk(
                userMessage,
                _currentConversationId,
                personalityTurnTag,
                role: "user");
        }
        var toolCallsMade = new List<ToolCallRecord>();
        var roundTrips = 0;

        var explicitContinuationPrompt =
            lowerIncoming.Contains("anything else", StringComparison.Ordinal) ||
            lowerIncoming.Contains("what else", StringComparison.Ordinal);
        if (explicitContinuationPrompt && !SafeModeEnabled)
        {
            var recentUserMessages = _history
                .Where(message => message.Role == "user")
                .Select(message => message.Content ?? string.Empty)
                .ToList();
            if (recentUserMessages.Count >= 2)
            {
                var priorUserLower = recentUserMessages[^2].Trim().ToLowerInvariant();
                var priorWasLookupLike =
                    IntentFeatureExtractor.LooksLikeWebSearchRequest(priorUserLower) ||
                    IntentFeatureExtractor.LooksLikeFactLookup(priorUserLower) ||
                    IntentFeatureExtractor.LooksLikeExplicitNewsLookup(priorUserLower) ||
                    priorUserLower.Contains("news", StringComparison.Ordinal);
                if (priorWasLookupLike)
                {
                    var dialogueLocation = _dialogueStore.Get().LocationName;
                    var continuationQuery = !string.IsNullOrWhiteSpace(dialogueLocation)
                        ? $"{dialogueLocation} more news"
                        : "more news";
                    var continuationArgs = JsonSerializer.Serialize(new
                    {
                        query = continuationQuery,
                        maxResults = 5,
                        recency = "week"
                    });

                    var continuationToolName = "web_search";
                    var continuationToolOk = false;
                    string continuationToolResult;
                    try
                    {
                        continuationToolResult = await _mcp.CallToolAsync(
                            continuationToolName,
                            continuationArgs,
                            cancellationToken);
                        continuationToolOk = true;
                    }
                    catch (Exception ex)
                    {
                        continuationToolResult = $"Tool error: {ex.Message}";
                    }

                    toolCallsMade.Add(new ToolCallRecord
                    {
                        ToolName = continuationToolName,
                        Arguments = continuationArgs,
                        Result = continuationToolResult,
                        Success = continuationToolOk
                    });
                }
            }
        }

        if (LooksLikeDownloadRamMythPrompt(userMessage))
        {
            var response = new AgentResponse
            {
                Text = "You can’t download RAM from the internet. RAM is physical hardware, so the real fixes are: close heavy apps/tabs, disable unnecessary startup apps, and if possible upgrade memory sticks (or use a machine with more RAM). If you want, I can walk through a quick speed-up checklist for your OS.",
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };

            AppendAssistantMessage(response.Text);
            LogEvent("AGENT_RESPONSE", response.Text);
            return AttachContextSnapshot(response, usageBaseline);
        }

        if (LooksLikeOauthOpenIdPrompt(userMessage))
        {
            var response = new AgentResponse
            {
                Text = BuildOauthOpenIdDeterministicAnswer(),
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };

            AppendAssistantMessage(response.Text);
            LogEvent("AGENT_RESPONSE", response.Text);
            return AttachContextSnapshot(response, usageBaseline);
        }

        if (!IsMultiIntentBypassActive())
        {
            var multiIntentResponse = await TryProcessMultiIntentTurnAsync(
                userMessage,
                toolCallsMade,
                cancellationToken);
            if (multiIntentResponse is not null)
                return AttachContextSnapshot(multiIntentResponse, usageBaseline);
        }

        if (TryBuildExplicitFileReadArgs(userMessage, out var earlyFileReadArgs, out var earlyFilePath))
        {
            var explicitFileReadResponse = await ExecuteExplicitFileReadAsync(
                earlyFileReadArgs,
                earlyFilePath,
                toolCallsMade,
                roundTrips,
                cancellationToken);

            return AttachContextSnapshot(explicitFileReadResponse, usageBaseline);
        }

        if (TryBuildExplicitFileListArgs(userMessage, out var earlyFileListArgs, out var earlyFolderPath))
        {
            var explicitFileListResponse = await ExecuteExplicitFileListAsync(
                earlyFileListArgs,
                earlyFolderPath,
                userMessage,
                toolCallsMade,
                roundTrips,
                cancellationToken);

            return AttachContextSnapshot(explicitFileListResponse, usageBaseline);
        }

        if (TryBuildExplicitKnowledgeStoreCreateListRoundTripArgs(
                userMessage,
                out var earlyKnowledgeStoreRootId,
                out var earlyKnowledgeStoreRelativePath,
                out _,
                out var earlyKnowledgeStoreListPath,
                out var earlyKnowledgeStoreCreateArgs,
                out var earlyKnowledgeStoreListArgs))
        {
            var explicitKnowledgeStoreCreateListResponse = await ExecuteExplicitKnowledgeStoreCreateListRoundTripAsync(
                earlyKnowledgeStoreRootId,
                earlyKnowledgeStoreRelativePath,
                earlyKnowledgeStoreListPath,
                earlyKnowledgeStoreCreateArgs,
                earlyKnowledgeStoreListArgs,
                toolCallsMade,
                roundTrips,
                cancellationToken);

            return AttachContextSnapshot(explicitKnowledgeStoreCreateListResponse, usageBaseline);
        }

        if (TryBuildExplicitKnowledgeStoreListRootsArgs(userMessage, out var earlyKnowledgeStoreListRootsArgs))
        {
            var explicitKnowledgeStoreListRootsResponse = await ExecuteExplicitKnowledgeStoreListRootsAsync(
                earlyKnowledgeStoreListRootsArgs,
                toolCallsMade,
                roundTrips,
                cancellationToken);

            return AttachContextSnapshot(explicitKnowledgeStoreListRootsResponse, usageBaseline);
        }

        var memoryTask = SafeModeEnabled ? Task.FromResult(new MemoryContextResult()) : GetMemoryContextSafeAsync(
            userMessage,
            _currentConversationId,
            cancellationToken);

        var slotStateBefore = _dialogueStore.Get();
        var slotTask = _slotExtract.RunAsync(userMessage, slotStateBefore, cancellationToken);

        var toolDefsTask = _toolDefinitionBuilder.BuildAsync(
            MemoryEnabled, PanicModeEnabled, SafeModeEnabled, LogEvent, cancellationToken);

        var routeResolution = await ResolveRouteAsync(userMessage, lowerIncoming, cancellationToken);
        var route = NormalizeRouteForPrompt(routeResolution.Route, lowerIncoming);
        var webEvidence = routeResolution.WebEvidence;

        var now = _timeProvider.GetUtcNow();
        var hasRecentSearchContext =
            _searchOrchestrator.Session.LastMode is not null &&
            (now - _searchOrchestrator.Session.UpdatedAt) < SearchSession.SessionTtl;
        var hadRecentLookupToolCall =
            _lastLookupToolCallAt != default &&
            (now - _lastLookupToolCallAt) < SearchSession.SessionTtl;
        var priorUserLookupSignal = false;
        var recentUserTurns = _history.Where(message => message.Role == "user").ToList();
        if (recentUserTurns.Count >= 2)
        {
            var priorUserLower = (recentUserTurns[^2].Content ?? string.Empty)
                .Trim()
                .ToLowerInvariant();
            priorUserLookupSignal =
                IntentFeatureExtractor.LooksLikeWebSearchRequest(priorUserLower) ||
                IntentFeatureExtractor.LooksLikeFactLookup(priorUserLower) ||
                IntentFeatureExtractor.LooksLikeExplicitNewsLookup(priorUserLower) ||
                priorUserLower.Contains("news", StringComparison.Ordinal);
        }
        var hasFollowUpLookupSignal =
            (hasRecentSearchContext || hadRecentLookupToolCall || priorUserLookupSignal) &&
            SearchModeRouter.IsFollowUpMessage(lowerIncoming);
        var hasTravelConditionsSignal =
            lowerIncoming.Contains("trip", StringComparison.Ordinal) &&
            lowerIncoming.Contains("condition", StringComparison.Ordinal);

        var shouldPromoteToLookup =
            IntentFeatureExtractor.LooksLikeFactLookup(lowerIncoming) ||
            IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lowerIncoming) ||
            lowerIncoming.Contains("news", StringComparison.Ordinal) ||
            hasFollowUpLookupSignal ||
            hasTravelConditionsSignal;

        var promotableNonLookupIntent =
            route.Intent.Equals(Intents.ChatOnly, StringComparison.OrdinalIgnoreCase) ||
            route.Intent.Equals(Intents.GeneralTool, StringComparison.OrdinalIgnoreCase);
        if (!RouteArbitrationPolicy.IsLookupIntent(route.Intent) &&
            promotableNonLookupIntent &&
            shouldPromoteToLookup)
        {
            route = DefaultRouter.MakeRoute(
                Intents.LookupFact,
                confidence: Math.Max(route.Confidence, webEvidence.Confidence),
                needsWeb: true,
                needsSearch: true);
            LogEvent("ROUTER_LOOKUP_PROMOTION",
                $"promoted_from={routeResolution.Route.Intent}, reason=fact_news_followup_signal");
        }

        var policy = PolicyGate.Evaluate(route, PanicModeEnabled, SafeModeEnabled);
        LogEvent("POLICY_DECISION",
            $"allowedCaps=[{string.Join(", ", policy.AllowedCapabilities)}], " +
            $"forbiddenCaps=[{string.Join(", ", policy.ForbiddenCapabilities)}], " +
            $"forbiddenTools=[{string.Join(", ", policy.ForbiddenTools)}], " +
            $"permissions=[{string.Join(", ", policy.RequiredPermissions)}], " +
            $"useToolLoop={policy.UseToolLoop}");

        var intent = MapRouteToLegacyIntent(route);

        var memoryContext = await memoryTask;
        var memoryPackText = memoryContext.PackText ?? "";
        var onboardingNeeded = memoryContext.OnboardingNeeded;
        var memoryError = memoryContext.Error ?? "";

        if (!SafeModeEnabled)
        {
            if (!MemoryEnabled)
            {
                LogEvent("MEMORY_DISABLED", "Memory is off — skipping retrieval.");
            }
            else if (memoryContext.Provenance.TimedOut)
            {
                LogEvent("MEMORY_TIMEOUT", "Memory retrieval exceeded timeout — skipped.");
            }

            if (MemoryEnabled && memoryContext.Provenance.Success)
            {
                toolCallsMade.Add(new ToolCallRecord
                {
                    ToolName = "MemoryRetrieve",
                    Arguments = $"{{\"query\":\"{Truncate(userMessage, 80)}\"}}",
                    Result = memoryContext.Provenance.Summary,
                    Success = memoryContext.Provenance.Success
                });
            }
        }

        if (onboardingNeeded && MemoryEnabled)
        {
            var isFirstTurn = _history.Count(m => m.Role == "user") <= 1;
            memoryPackText = isFirstTurn
                ? OnboardingColdPrompt
                : OnboardingFollowUpPrompt;

            if (intent == ChatIntent.Casual)
            {
                route = DefaultRouter.MakeRoute(Intents.MemoryWrite, confidence: 0.9,
                    needsMemoryWrite: true);
                policy = PolicyGate.Evaluate(route, PanicModeEnabled, SafeModeEnabled);
                intent = MapRouteToLegacyIntent(route);
            }

            LogEvent("ONBOARDING_INJECTED", isFirstTurn ? "First turn — introducing and asking who the user is." : "Follow-up — passively capturing info.");
        }

        var extractedSlots = await slotTask;
        var mergedSlots = _mergeSlots.Run(slotStateBefore, extractedSlots, _timeProvider.GetUtcNow());
        var validatedSlots = _validateSlots.Run(slotStateBefore, mergedSlots);
        UpdateDialogueStateFromValidatedSlots(validatedSlots);
        var toolPlan = _toolPlanner.Plan(validatedSlots, slotStateBefore, UserLocationHint, PreferredUnits);
        if (toolPlan.InjectionMitigationApplied)
            LogEvent("PROMPT_INJECTION_FILTER_APPLIED", $"reason={toolPlan.InjectionMitigationReason}");
        var contextualUserMessage = string.IsNullOrWhiteSpace(validatedSlots.NormalizedMessage)
            ? userMessage
            : validatedSlots.NormalizedMessage;
        contextualUserMessage = _contextAnchoringService.ApplyPlaceContextIfHelpful(contextualUserMessage);

        if (!string.Equals(contextualUserMessage, userMessage, StringComparison.Ordinal))
        {
            LogEvent("PLACE_CONTEXT_INFERRED", $"{Truncate(userMessage, 80)} -> {Truncate(contextualUserMessage, 120)}");
        }

        if (SafeModeEnabled &&
            (route.NeedsWeb || route.NeedsSearch || RouteArbitrationPolicy.IsLookupIntent(route.Intent)))
        {
            const string safeModeWebBlockMessage =
                "Web search is currently blocked because Safe Mode is enabled. " +
                "Disable Safe Mode in Runtime Safety to run web lookups.";
            LogEvent("WEB_LOOKUP_BLOCKED_SAFE_MODE", safeModeWebBlockMessage);
            AppendAssistantMessage(safeModeWebBlockMessage);

            return AttachContextSnapshot(new AgentResponse
            {
                Text = safeModeWebBlockMessage,
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips,
                SuppressSourceCardsUi = true,
                SuppressToolActivityUi = true
            }, usageBaseline);
        }

        var hasLoadedProfileContext =
            !string.IsNullOrWhiteSpace(ActiveProfileId) ||
            memoryPackText.Contains("[PROFILE]", StringComparison.OrdinalIgnoreCase);

        if (MemoryEnabled &&
            !_selfMemorySummarizer.IsSelfMemoryKnowledgeRequest(contextualUserMessage) &&
            _selfMemorySummarizer.IsPersonalizedUsingKnownSelfContextRequest(
                contextualUserMessage,
                hasLoadedProfileContext))
        {
            var personalizedFactsContext = await _selfMemorySummarizer.BuildContextBlockAsync(
                ActiveProfileId,
                toolCallsMade,
                LogEvent,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(personalizedFactsContext))
            {
                memoryPackText = string.IsNullOrWhiteSpace(memoryPackText)
                    ? personalizedFactsContext
                    : $"{memoryPackText}\n{personalizedFactsContext}";
            }
        }

        try
        {
            var explicitContinuationResponse = await TryHandleExplicitSearchContinuationAsync(
                lowerIncoming,
                contextualUserMessage,
                memoryPackText,
                personalityAnchor,
                personalityTurnTag,
                route,
                validatedSlots,
                toolCallsMade,
                usageBaseline,
                cancellationToken);
            if (explicitContinuationResponse is not null)
                return explicitContinuationResponse;

            var firstPrinciplesFollowUp = TryBuildFirstPrinciplesFollowUpResponse(
                contextualUserMessage,
                toolCallsMade,
                roundTrips);
            if (firstPrinciplesFollowUp is not null)
            {
                AppendAssistantMessage(firstPrinciplesFollowUp.Text);
                LogEvent("FIRST_PRINCIPLES_FOLLOWUP", firstPrinciplesFollowUp.Text);
                return AttachContextSnapshot(firstPrinciplesFollowUp, usageBaseline);
            }

            var shouldAllowDeterministicSpecialCase =
                !(route.NeedsWeb || route.NeedsSearch || RouteArbitrationPolicy.IsLookupIntent(route.Intent));

            var deterministicSpecialCase = shouldAllowDeterministicSpecialCase
                ? _guardrailsCoordinator.TryRunDeterministicSpecialCase(
                    contextualUserMessage,
                    Guardrails.ReasoningGuardrailsMode.Auto)
                : null;
            if (deterministicSpecialCase is not null)
            {
                var specialCaseText = deterministicSpecialCase.AnswerText;
                _lastFirstPrinciplesRationale = deterministicSpecialCase.RationaleLines.Take(3).ToArray();
                _lastFirstPrinciplesAt = _timeProvider.GetUtcNow();
                AppendAssistantMessage(specialCaseText);
                LogEvent("GUARDRAILS_RESPONSE",
                    $"risk={deterministicSpecialCase.TriggerRisk}, source={deterministicSpecialCase.TriggerSource}, why={deterministicSpecialCase.TriggerWhy}");
                LogEvent("AGENT_RESPONSE", specialCaseText);

                return AttachContextSnapshot(new AgentResponse
                {
                    Text = specialCaseText,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = roundTrips,
                    GuardrailsUsed = true,
                    GuardrailsRationale = deterministicSpecialCase.RationaleLines
                }, usageBaseline);
            }

            var utilityResponse = await TryHandleUtilityIntentAsync(
                lowerIncoming,
                contextualUserMessage,
                route,
                toolPlan,
                validatedSlots,
                toolCallsMade,
                roundTrips,
                hasRecentSearchContext,
                cancellationToken);
            if (utilityResponse is not null)
            {
                var lastAssistantText = _history.LastOrDefault(m => m.Role == "assistant")?.Content;
                if (!string.Equals(lastAssistantText, utilityResponse.Text, StringComparison.Ordinal))
                {
                    AppendAssistantMessage(utilityResponse.Text);
                    LogEvent("AGENT_RESPONSE", utilityResponse.Text);
                }

                return AttachContextSnapshot(
                    _contextAnchoringService.AddLocationInferenceDisclosure(utilityResponse, validatedSlots),
                    usageBaseline);
            }

            var guardrailsResult = intent == ChatIntent.WebLookup
                ? null
                : await _guardrailsCoordinator.TryRunAsync(
                    route,
                    contextualUserMessage,
                    Guardrails.ReasoningGuardrailsMode.Auto,
                    memoryPackText,
                    cancellationToken);

            if (guardrailsResult is not null)
            {
                roundTrips += guardrailsResult.LlmRoundTrips;

                var guardedText = _postProcessor.SanitizeFinalResponse(
                    guardrailsResult.AnswerText,
                    toolCallsMade,
                    contextualUserMessage);
                _lastFirstPrinciplesRationale = guardrailsResult.RationaleLines.Take(3).ToArray();
                _lastFirstPrinciplesAt = _timeProvider.GetUtcNow();
                AppendAssistantMessage(guardedText);
                LogEvent("GUARDRAILS_RESPONSE",
                    $"risk={guardrailsResult.TriggerRisk}, source={guardrailsResult.TriggerSource}, why={guardrailsResult.TriggerWhy}");
                LogEvent(
                    "FIRST_PRINCIPLES_TRACE",
                    string.Join(" || ", guardrailsResult.RationaleLines));
                LogEvent("AGENT_RESPONSE", guardedText);

                return AttachContextSnapshot(new AgentResponse
                {
                    Text = guardedText,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = roundTrips,
                    GuardrailsUsed = true,
                    GuardrailsRationale = guardrailsResult.RationaleLines
                }, usageBaseline);
            }

            if (MemoryEnabled && _selfMemorySummarizer.IsSelfMemoryKnowledgeRequest(contextualUserMessage))
            {
                var memorySummary = await _selfMemorySummarizer.BuildSummaryResponseAsync(
                    ActiveProfileId,
                    toolCallsMade,
                    roundTrips,
                    cancellationToken);
                AppendAssistantMessage(memorySummary.Text);
                LogEvent("AGENT_RESPONSE", memorySummary.Text);
                return AttachContextSnapshot(memorySummary, usageBaseline);
            }

            var forceLocalBusinessLookupFromFileIntent =
                route.Intent.Equals(Intents.FileTask, StringComparison.OrdinalIgnoreCase) &&
                IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lowerIncoming);

            if (forceLocalBusinessLookupFromFileIntent)
            {
                LogEvent(
                    "LOCAL_BUSINESS_FILE_INTENT_OVERRIDE",
                    "Rerouting local-business prompt from FileTask to web lookup pipeline.");
            }

            if (intent == ChatIntent.WebLookup || forceLocalBusinessLookupFromFileIntent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lookupModeHint = forceLocalBusinessLookupFromFileIntent
                    ? LookupModeHint.Fact
                    : ResolveLookupModeHint(route);

                if (!string.IsNullOrWhiteSpace(memoryPackText))
                    InjectMemoryIntoHistoryInPlace(_history, memoryPackText);
                InjectPersonalityAnchorIntoHistoryInPlace(_history, personalityAnchor, personalityTurnTag);

                var searchResponse = await _searchOrchestrator.ExecuteAsync(
                    contextualUserMessage,
                    memoryPackText,
                    _history,
                    toolCallsMade,
                    lookupModeHint,
                    cancellationToken);
                searchResponse = ApplySeasonEpisodeExistenceSanityGate(
                    contextualUserMessage,
                    searchResponse,
                    toolCallsMade);

                var stripped = Search.SearchOrchestrator.StripOfflineReasoningPrefix(searchResponse.Text);
                if (!string.Equals(stripped, searchResponse.Text, StringComparison.Ordinal))
                    searchResponse = searchResponse with { Text = stripped };

                if (searchResponse.Success)
                    AppendAssistantMessage(searchResponse.Text);

                LogEvent("AGENT_RESPONSE", searchResponse.Text);
                return AttachContextSnapshot(
                    _contextAnchoringService.AddLocationInferenceDisclosure(searchResponse, validatedSlots),
                    usageBaseline);
            }

            if (route.Intent.Equals(Intents.ScreenObserve, StringComparison.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await ExecuteDeterministicScreenCaptureAsync(
                    contextualUserMessage, memoryPackText,
                    personalityAnchor, personalityTurnTag,
                    toolCallsMade, roundTrips, usageBaseline,
                    cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(memoryPackText))
                InjectMemoryIntoHistoryInPlace(_history, memoryPackText);
            InjectPersonalityAnchorIntoHistoryInPlace(_history, personalityAnchor, personalityTurnTag);

            if (route.Intent.Equals(Intents.FileTask, StringComparison.OrdinalIgnoreCase) &&
                TryBuildExplicitFileReadArgs(userMessage, out var explicitFileReadArgs, out var explicitFilePath))
            {
                var explicitFileReadResponse = await ExecuteExplicitFileReadAsync(
                    explicitFileReadArgs,
                    explicitFilePath,
                    toolCallsMade,
                    roundTrips,
                    cancellationToken);

                return AttachContextSnapshot(explicitFileReadResponse, usageBaseline);
            }

            if (route.Intent.Equals(Intents.FileTask, StringComparison.OrdinalIgnoreCase) &&
                TryBuildExplicitFileReadArgs(contextualUserMessage, out explicitFileReadArgs, out explicitFilePath))
            {
                var explicitFileReadResponse = await ExecuteExplicitFileReadAsync(
                    explicitFileReadArgs,
                    explicitFilePath,
                    toolCallsMade,
                    roundTrips,
                    cancellationToken);

                return AttachContextSnapshot(explicitFileReadResponse, usageBaseline);
            }

            if (route.Intent.Equals(Intents.FileTask, StringComparison.OrdinalIgnoreCase) &&
                TryBuildExplicitKnowledgeStoreJournalRoundTripArgs(userMessage, out var knowledgeStoreRootId, out var knowledgeStoreEntry))
            {
                var knowledgeStoreResponse = await ExecuteExplicitKnowledgeStoreJournalRoundTripAsync(
                    knowledgeStoreRootId,
                    knowledgeStoreEntry,
                    toolCallsMade,
                    roundTrips,
                    cancellationToken);

                return AttachContextSnapshot(knowledgeStoreResponse, usageBaseline);
            }

            if (route.Intent.Equals(Intents.FileTask, StringComparison.OrdinalIgnoreCase) &&
                TryBuildExplicitKnowledgeStoreJournalRoundTripArgs(contextualUserMessage, out knowledgeStoreRootId, out knowledgeStoreEntry))
            {
                var knowledgeStoreResponse = await ExecuteExplicitKnowledgeStoreJournalRoundTripAsync(
                    knowledgeStoreRootId,
                    knowledgeStoreEntry,
                    toolCallsMade,
                    roundTrips,
                    cancellationToken);

                return AttachContextSnapshot(knowledgeStoreResponse, usageBaseline);
            }

            if (route.Intent.Equals(Intents.FileTask, StringComparison.OrdinalIgnoreCase) &&
                TryBuildExplicitKnowledgeStoreCreateListRoundTripArgs(
                    userMessage,
                    out var explicitKnowledgeStoreRootId,
                    out var explicitKnowledgeStoreRelativePath,
                    out _,
                    out var explicitKnowledgeStoreListPath,
                    out var explicitKnowledgeStoreCreateArgs,
                    out var explicitKnowledgeStoreListArgs))
            {
                var knowledgeStoreCreateListResponse = await ExecuteExplicitKnowledgeStoreCreateListRoundTripAsync(
                    explicitKnowledgeStoreRootId,
                    explicitKnowledgeStoreRelativePath,
                    explicitKnowledgeStoreListPath,
                    explicitKnowledgeStoreCreateArgs,
                    explicitKnowledgeStoreListArgs,
                    toolCallsMade,
                    roundTrips,
                    cancellationToken);

                return AttachContextSnapshot(knowledgeStoreCreateListResponse, usageBaseline);
            }

            if (route.Intent.Equals(Intents.FileTask, StringComparison.OrdinalIgnoreCase) &&
                TryBuildExplicitKnowledgeStoreCreateListRoundTripArgs(
                    contextualUserMessage,
                    out explicitKnowledgeStoreRootId,
                    out explicitKnowledgeStoreRelativePath,
                    out _,
                    out explicitKnowledgeStoreListPath,
                    out explicitKnowledgeStoreCreateArgs,
                    out explicitKnowledgeStoreListArgs))
            {
                var knowledgeStoreCreateListResponse = await ExecuteExplicitKnowledgeStoreCreateListRoundTripAsync(
                    explicitKnowledgeStoreRootId,
                    explicitKnowledgeStoreRelativePath,
                    explicitKnowledgeStoreListPath,
                    explicitKnowledgeStoreCreateArgs,
                    explicitKnowledgeStoreListArgs,
                    toolCallsMade,
                    roundTrips,
                    cancellationToken);

                return AttachContextSnapshot(knowledgeStoreCreateListResponse, usageBaseline);
            }

            if (route.Intent.Equals(Intents.FileTask, StringComparison.OrdinalIgnoreCase) &&
                TryBuildExplicitFileListArgs(userMessage, out var explicitFileListArgs, out var explicitFolderPath))
            {
                var explicitFileListResponse = await ExecuteExplicitFileListAsync(
                    explicitFileListArgs,
                    explicitFolderPath,
                    contextualUserMessage,
                    toolCallsMade,
                    roundTrips,
                    cancellationToken);

                return AttachContextSnapshot(explicitFileListResponse, usageBaseline);
            }

            if (route.Intent.Equals(Intents.FileTask, StringComparison.OrdinalIgnoreCase) &&
                TryBuildExplicitFileListArgs(contextualUserMessage, out explicitFileListArgs, out explicitFolderPath))
            {
                var explicitFileListResponse = await ExecuteExplicitFileListAsync(
                    explicitFileListArgs,
                    explicitFolderPath,
                    contextualUserMessage,
                    toolCallsMade,
                    roundTrips,
                    cancellationToken);

                return AttachContextSnapshot(explicitFileListResponse, usageBaseline);
            }

            if (!policy.UseToolLoop)
            {
                cancellationToken.ThrowIfCancellationRequested();
                roundTrips++;

                var messages = _history.ToList();
                if (LooksLikeLogicPuzzlePrompt(lowerIncoming))
                {
                    messages = InjectModeIntoSystemPrompt(messages, LogicPuzzleDecompositionModeSuffix);
                    LogEvent("LOGIC_PUZZLE_SCAFFOLD",
                        "Injected first-principles decomposition scaffold for chat-only solve.");
                }
                
                InjectPersonalityAnchorIntoHistoryInPlace(messages, personalityAnchor, personalityTurnTag);
                InjectFewShotExamplesInPlace(messages, _personalityRuntime.Snapshot.Profile.Instructions.FewShotExamples);

                var response = await CallLlmWithRetrySafe(
                    messages, roundTrips, _maxTokensCasual, cancellationToken);

                if (string.Equals(response.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
                {
                    LogEvent("CASUAL_TRUNCATED",
                        $"Response truncated at {_maxTokensCasual} tokens — retrying with {_maxTokensCasualRetry}.");
                    roundTrips++;
                    response = await CallLlmWithRetrySafe(
                        messages, roundTrips, _maxTokensCasualRetry, cancellationToken);
                }

                var text = _postProcessor.ProcessChatOnlyDraft(
                    response.Content ?? "[No response]",
                    contextualUserMessage,
                    toolCallsMade,
                    LogEvent);

                if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(response.Content))
                {
                    var sanitizedFallback = _postProcessor.SanitizeFinalResponse(
                        response.Content,
                        toolCallsMade,
                        contextualUserMessage);

                    text = string.IsNullOrWhiteSpace(sanitizedFallback)
                        ? response.Content.Trim()
                        : sanitizedFallback;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    text =
                        "I couldn't produce a complete response this turn, but I can retry immediately.";
                }

                var deterministicRouteMatched = IsDeterministicInlineRoute(route);
                var hasRefusalOrUncertaintySignals = HasRefusalOrUncertaintySignals(
                    response.Content ?? "",
                    text);

                if (string.IsNullOrWhiteSpace(text) && deterministicRouteMatched)
                {
                    const string deterministicFallback =
                        "I could not finish that deterministic conversion. " +
                        "Try a direct format like \"350F in C\".";
                    LogEvent("DETERMINISTIC_NO_WEB_ENFORCED",
                        "Suppressed chat fallback web search for deterministic route.");
                    AppendAssistantMessage(deterministicFallback);
                    LogEvent("AGENT_RESPONSE", deterministicFallback);
                    return AttachContextSnapshot(new AgentResponse
                    {
                        Text = deterministicFallback,
                        Success = true,
                        ToolCallsMade = toolCallsMade,
                        LlmRoundTrips = roundTrips,
                        SuppressSourceCardsUi = true,
                        SuppressToolActivityUi = true
                    }, usageBaseline);
                }

                var lookupAlreadyExecuted = RouteArbitrationPolicy.IsLookupIntent(route.Intent);
                var fallbackEligible =
                    hasRefusalOrUncertaintySignals &&
                    route.Intent.Equals(Intents.ChatOnly, StringComparison.OrdinalIgnoreCase) &&
                    !IntentFeatureExtractor.LooksLikePreferenceOrOpinionPrompt(lowerIncoming) &&
                    !IntentFeatureExtractor.LooksLikeIdentityLookup(lowerIncoming) &&
                    (IntentFeatureExtractor.LooksLikeWebSearchRequest(lowerIncoming) ||
                     IntentFeatureExtractor.LooksLikeFactLookup(lowerIncoming) ||
                     IntentFeatureExtractor.LooksLikeExplicitNewsLookup(lowerIncoming)) &&
                    !deterministicRouteMatched &&
                    !lookupAlreadyExecuted;

                if (fallbackEligible)
                {
                    LogEvent("CHAT_FALLBACK_TO_SEARCH",
                        "Chat-only refusal/uncertainty detected — " +
                        "falling back to search pipeline.");

                    return AttachContextSnapshot(await _searchFallbackExecutor.ExecuteAsync(
                        new SearchFallbackRequest
                        {
                            UserMessage = contextualUserMessage,
                            MemoryPackText = memoryPackText,
                            History = _history,
                            ToolCallsMade = toolCallsMade,
                            RoundTrips = roundTrips,
                            ModeHint = ResolveLookupModeHint(route),
                            DeterministicRouteMatched = deterministicRouteMatched,
                            LookupAlreadyExecuted = lookupAlreadyExecuted,
                            HasRefusalOrUncertaintySignals = hasRefusalOrUncertaintySignals,
                            LogEvent = LogEvent
                        },
                        cancellationToken), usageBaseline);
                }

                var screenFallbackEligible =
                    hasRefusalOrUncertaintySignals &&
                    route.Intent.Equals(Intents.ChatOnly, StringComparison.OrdinalIgnoreCase) &&
                    IntentFeatureExtractor.LooksLikeScreenRequest(lowerIncoming) &&
                    !deterministicRouteMatched;

                if (screenFallbackEligible)
                {
                    LogEvent("CHAT_FALLBACK_TO_SCREEN",
                        "Chat-only refusal/uncertainty detected — falling back to deterministic screen capture.");

                    return AttachContextSnapshot(await ExecuteDeterministicScreenCaptureAsync(
                        contextualUserMessage,
                        memoryPackText,
                        personalityAnchor,
                        personalityTurnTag,
                        toolCallsMade,
                        roundTrips,
                        usageBaseline,
                        cancellationToken), usageBaseline);
                }

                var fileFallbackEligible =
                    hasRefusalOrUncertaintySignals &&
                    route.Intent.Equals(Intents.ChatOnly, StringComparison.OrdinalIgnoreCase) &&
                    IntentFeatureExtractor.LooksLikeFileRequest(lowerIncoming) &&
                    !deterministicRouteMatched;

                if (fileFallbackEligible)
                {
                    LogEvent("CHAT_FALLBACK_TO_FILE",
                        "Chat-only refusal/uncertainty detected — falling back to file tools.");

                    if (TryBuildExplicitFileReadArgs(userMessage, out var fallbackFileReadArgs, out var fallbackFilePath) ||
                        TryBuildExplicitFileReadArgs(contextualUserMessage, out fallbackFileReadArgs, out fallbackFilePath))
                    {
                        return AttachContextSnapshot(
                            await ExecuteExplicitFileReadAsync(
                                fallbackFileReadArgs,
                                fallbackFilePath,
                                toolCallsMade,
                                roundTrips,
                                cancellationToken),
                            usageBaseline);
                    }

                    if (TryBuildExplicitFileListArgs(userMessage, out var fallbackFileListArgs, out var fallbackFolderPath) ||
                        TryBuildExplicitFileListArgs(contextualUserMessage, out fallbackFileListArgs, out fallbackFolderPath))
                    {
                        return AttachContextSnapshot(
                            await ExecuteExplicitFileListAsync(
                                fallbackFileListArgs,
                                fallbackFolderPath,
                                contextualUserMessage,
                                toolCallsMade,
                                roundTrips,
                                cancellationToken),
                            usageBaseline);
                    }

                    var fallbackToolsCatalog = await toolDefsTask;
                    var filePolicy = PolicyGate.Evaluate(new RouterOutput
                    {
                        Intent = Intents.FileTask,
                        NeedsFileAccess = true,
                        RequiredCapabilities = [ToolCapability.FileRead],
                        Confidence = 1.0
                    });
                    var fileTools = FilterKnowledgeStoreToolsIfNeeded(
                        PolicyGate.FilterTools(fallbackToolsCatalog, filePolicy),
                        contextualUserMessage);

                    LogEvent("AGENT_TOOLS_POLICY_FILTERED",
                        $"{fileTools.Count} file tool(s) exposed for fallback: [{string.Join(", ", fileTools.Select(t => t.Function.Name))}]");

                    var fileToolLoopResponse = await RunToolLoopAsync(
                        fileTools,
                        toolCallsMade,
                        roundTrips,
                        cancellationToken);

                    return AttachContextSnapshot(fileToolLoopResponse, usageBaseline);
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    text = "I wasn't able to generate a clean answer for that. Could you try asking a different way?";
                    LogEvent("CHAT_RESPONSE_EMPTY_NO_FALLBACK", "Returned local fallback message.");
                }

                AppendAssistantMessage(text);
                LogEvent("AGENT_RESPONSE", text);

                return AttachContextSnapshot(new AgentResponse
                {
                    Text = text,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = roundTrips,
                    AllowToolResultPersonalityPresentation = true
                }, usageBaseline);
            }

            var allTools = await toolDefsTask;
            var tools = FilterKnowledgeStoreToolsIfNeeded(
                PolicyGate.FilterTools(allTools, policy),
                contextualUserMessage);

            if (route.NeedsWeb || route.NeedsSearch || RouteArbitrationPolicy.IsLookupIntent(route.Intent))
            {
                tools = tools
                    .Where(t => !t.Function.Name.StartsWith("file_", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (route.Intent.Equals(Intents.GeneralTool, StringComparison.OrdinalIgnoreCase) &&
                !tools.Any(t => t.Function.Name.Equals("tool_list_capabilities", StringComparison.OrdinalIgnoreCase)))
            {
                var metaTool = allTools.FirstOrDefault(t =>
                    t.Function.Name.Equals("tool_list_capabilities", StringComparison.OrdinalIgnoreCase));

                if (metaTool is not null && !string.IsNullOrWhiteSpace(metaTool.Function.Name))
                    tools = [.. tools, metaTool];
            }

            LogEvent("AGENT_TOOLS_POLICY_FILTERED",
                $"{tools.Count} tool(s) from {allTools.Count} total: " +
                $"[{string.Join(", ", tools.Select(t => t.Function.Name))}]");

            var toolLoopResponse = await RunToolLoopAsync(
                tools, toolCallsMade, roundTrips, cancellationToken);
            toolLoopResponse = NormalizeMetaToolHealthResponse(toolLoopResponse);

            var deterministicMemoryFallback = await TryRunDeterministicMemoryStoreFallbackAsync(
                route,
                contextualUserMessage,
                tools,
                toolCallsMade,
                toolLoopResponse,
                cancellationToken);
            if (deterministicMemoryFallback is not null)
                return AttachContextSnapshot(deterministicMemoryFallback, usageBaseline);

            return AttachContextSnapshot(toolLoopResponse, usageBaseline);
        }
        catch (OperationCanceledException)
        {
            var hasWebToolActivity = toolCallsMade.Any(call =>
                call.ToolName.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
                call.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase));

            var likelyLookupIntent = route.NeedsWeb || route.NeedsSearch ||
                                     RouteArbitrationPolicy.IsLookupIntent(route.Intent);

            if (hasWebToolActivity || likelyLookupIntent)
            {
                var offlineFallback = await Search.OfflineWebReasoningResponder.BuildAsync(
                    _llm,
                    _systemPrompt,
                    contextualUserMessage,
                    memoryPackText,
                    _history,
                    toolCallsMade,
                    "Web lookup timed out before completion.",
                    CancellationToken.None);

                LogEvent("AGENT_CANCELLED_RECOVERED", "Recovered with offline web fallback.");
                AppendAssistantMessage(offlineFallback.Text);

                return AttachContextSnapshot(offlineFallback with
                {
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = Math.Max(roundTrips, offlineFallback.LlmRoundTrips)
                }, usageBaseline);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                const string contextualFallback =
                    "I couldn't complete that in time, but I can retry right away.";
                LogEvent("AGENT_CANCELLED_RECOVERED", contextualFallback);
                AppendAssistantMessage(contextualFallback);

                return AttachContextSnapshot(new AgentResponse
                {
                    Text = contextualFallback,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = roundTrips
                }, usageBaseline);
            }

            const string gracefulCancellation =
                "I couldn't complete that request before the time limit, but I can retry it now.";
            LogEvent("AGENT_CANCELLED", gracefulCancellation);
            AppendAssistantMessage(gracefulCancellation);
            return AttachContextSnapshot(new AgentResponse
            {
                Text = gracefulCancellation,
                Success = true,
                Error = "Cancelled",
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            }, usageBaseline);
        }
        catch (Exception ex)
        {
            LogEvent("AGENT_ERROR", ex.Message);

            if (ex is HttpRequestException httpRequestException && IsLlmConnectivityFailure(httpRequestException))
            {
                var fallback = BuildConnectivityFallbackResponse(
                    [ChatMessage.User(userMessage)]);
                var fallbackText = _postProcessor.SanitizeFinalResponse(
                    fallback.Content ?? string.Empty,
                    toolCallsMade,
                    userMessage);

                if (string.IsNullOrWhiteSpace(fallbackText) &&
                    !string.IsNullOrWhiteSpace(fallback.Content))
                {
                    fallbackText = fallback.Content;
                }

                if (string.IsNullOrWhiteSpace(fallbackText))
                {
                    fallbackText =
                        "I can't reach the configured local language model endpoint right now. " +
                        "I can still help with direct tools and deterministic tasks, or we can retry once the model endpoint is reachable.";
                }

                AppendAssistantMessage(fallbackText);
                LogEvent("AGENT_CONNECTIVITY_RECOVERED", fallbackText);

                return AttachContextSnapshot(new AgentResponse
                {
                    Text = fallbackText,
                    Success = true,
                    Error = ex.Message,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = roundTrips
                }, usageBaseline);
            }

            return AttachContextSnapshot(new AgentResponse
            {
                Text          = $"Error: {ex.Message}",
                Success       = false,
                Error         = ex.Message,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            }, usageBaseline);
        }
    }

}
