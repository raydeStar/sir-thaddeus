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

/// <summary>
/// State/phase coordinator for a single agent turn.
/// Contract: this file sequences modules, updates session state/history,
/// handles cancellation/errors, and assembles the final response.
/// Business logic lives in extracted module implementations.
/// </summary>
public sealed partial class AgentOrchestrator : IAgentOrchestrator
{
    private readonly ILlmClient _llm;
    private readonly IMcpToolClient _mcp;
    private readonly IAuditLogger _audit;
    private readonly string _systemPrompt;
    private readonly TimeProvider _timeProvider;

    private readonly List<ChatMessage> _history = [];

    /// <summary>
    /// The search pipeline — owns SearchSession, mode routing, entity
    /// resolution, query construction, and the 3 pipelines.
    /// </summary>
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

    // Last resolved place from weather flow. Used to anchor short
    // follow-up weather/news prompts like "forecast for today?"
    // without forcing the user to repeat the city every turn.
    private string? _lastPlaceContextName;
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

    // ── Web search tool names ────────────────────────────────────────
    // Canonical tool names live in ToolNames static class.
    // These aliases keep the internal code unchanged during extraction.
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

    // ── Summary instructions ─────────────────────────────────────────
    // Canonical prompt strings live in OrchestratorPrompts.
    private const string WebSummaryInstruction = OrchestratorPrompts.WebSummaryInstruction;
    private const string WebFollowUpInstruction = OrchestratorPrompts.WebFollowUpInstruction;
    private const string WebFollowUpWithRelatedInstruction = OrchestratorPrompts.WebFollowUpWithRelatedInstruction;

    // ── Token budget per intent ──────────────────────────────────────
    // Configurable caps — when MaxTokensBudget is set from user
    // settings the casual / retry ceilings scale accordingly.  The
    // remaining per-intent caps stay fixed because they guard
    // specialised LLM calls (routing, summaries) that don't benefit
    // from a larger completion window.
    private int _maxTokensCasual      = 512;
    private int _maxTokensCasualRetry = 2048;
    private const int MaxTokensWebSummary     = 1024;
    private const int MaxTokensWebSummaryRetry = 2048;
    private const int MaxTokensTooling        = 1024;
    private const int MaxTokensUtilityRouting = 120;

    /// <summary>
    /// User-facing max-token budget for casual chat responses.
    /// When set (e.g. from <c>LlmSettings.MaxTokens</c>), overrides
    /// the default 512 cap and scales the truncation-retry ceiling
    /// to <c>Max(budget, 2048)</c>.
    /// </summary>
    public int MaxTokensBudget
    {
        get => _maxTokensCasual;
        set
        {
            _maxTokensCasual = value > 0 ? value : 512;
            _maxTokensCasualRetry = Math.Max(_maxTokensCasual, 2048);
        }
    }

    // ── Logic puzzle decomposition scaffold ──────────────────────────
    private const string LogicPuzzleDecompositionModeSuffix = OrchestratorPrompts.LogicPuzzleDecompositionModeSuffix;

    // Hard ceiling on memory retrieval. If the MCP tool + SQLite +
    // optional embeddings don't finish in this window, we skip memory
    // entirely and proceed with the conversation. Non-negotiable.
    private static readonly TimeSpan MemoryRetrievalTimeout = TimeSpan.FromMilliseconds(1500);

    // ── Onboarding prompts ────────────────────────────────────────────
    private const string OnboardingColdPrompt = OrchestratorPrompts.OnboardingColdPrompt;
    private const string OnboardingFollowUpPrompt = OrchestratorPrompts.OnboardingFollowUpPrompt;

    // ── History sliding window ───────────────────────────────────────
    // Keep the last N user+assistant turns so the context window stays
    // within a small model's effective range. The system prompt is
    // always retained as message[0].
    private const int MaxHistoryTurns = 12;

    /// <summary>
    /// The profile_id of the currently active user. Set from the
    /// Settings tab's dropdown. Passed to the MemoryRetrieve tool
    /// on every call so the MCP server knows who's talking —
    /// env vars can't cross process boundaries at runtime.
    /// </summary>
    public string? ActiveProfileId { get; set; }

    /// <summary>
    /// Master switch for memory features. When false:
    ///   1. Skips <c>RetrieveMemoryContextAsync</c> entirely
    ///   2. Suppresses onboarding prompts that force memory_write
    ///   3. Filters out memory_* tools from tool definitions
    /// Set from <c>memory.enabled</c> in settings.
    /// </summary>
    public bool MemoryEnabled { get; set; } = true;

    /// <summary>
    /// Global kill switch for side-effecting tools.
    /// </summary>
    public bool PanicModeEnabled { get; set; }

    /// <summary>
    /// Fail-closed runtime mode where tool execution is disabled.
    /// </summary>
    public bool SafeModeEnabled { get; set; }

    /// <summary>
    /// User's configured location hint (e.g. "Portland, OR").
    /// Set from the active profile's manual location value.
    /// Injected into the system prompt so location-dependent queries
    /// (weather, places, local news) default to the user's area.
    /// </summary>
    public string? UserLocationHint
    {
        get => _userLocationHint;
        set
        {
            _userLocationHint = value;
            // Propagate to search orchestrator for deep-dive place lookups
            _searchOrchestrator.UserLocationHint = value;
        }
    }

    /// <summary>
    /// User's configured timezone (e.g. "America/Los_Angeles").
    /// Set from the active profile's optional location timezone value.
    /// </summary>
    public string? UserTimezone { get; set; }

    /// <summary>
    /// Preferred unit system for weather/measurement responses.
    /// Values: "imperial", "metric", or "auto".
    /// Injected into system prompt so the LLM presents data in the
    /// user's preferred units unless explicitly asked otherwise.
    /// </summary>
    public string? PreferredUnits
    {
        get => _preferredUnits;
        set
        {
            _preferredUnits = NormalizeUnitPreference(value);
            _searchOrchestrator.PreferredUnits = _preferredUnits;
        }
    }

    /// <summary>
    /// Seeds the conversation history with prior user/assistant turns
    /// so multi-turn follow-ups work across stateless HTTP requests.
    /// Call this <b>before</b> <see cref="ProcessAsync(string, CancellationToken)"/> to replay
    /// the conversation context.  Only user and assistant messages
    /// are accepted — system messages are silently skipped because
    /// the constructor already seeds the system prompt.
    /// </summary>
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
                // Skip system/tool — system prompt is already in _history[0].
            }
        }

        TrimHistory();
    }

    /// <inheritdoc />
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

        // Seed the conversation with the system prompt
        _history.Add(ChatMessage.System(BuildEffectiveSystemPrompt()));

        var personalitySnapshot = _personalityRuntime.Snapshot;
        EmitPersonalityAuditSnapshot(personalitySnapshot, _activePersonalityId);
    }

    /// <inheritdoc />
    public Task<AgentResponse> ProcessAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
        => ProcessAsync(userMessage, conversationId: null, cancellationToken);

    /// <inheritdoc />
    public async Task<AgentResponse> ProcessAsync(
        string userMessage,
        string? conversationId,
        CancellationToken cancellationToken = default)
    {
        var usageBaseline = CaptureUsageSnapshot();

        if (string.IsNullOrWhiteSpace(userMessage))
            return AttachContextSnapshot(AgentResponse.FromError("Empty message."), usageBaseline);

        // Reset per-turn budget so each user message gets a fresh budget.
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

        // ── Add user message to history ──────────────────────────────
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

        // ── Parallel I/O Setup ───────────────────────────────────────
        // Kick off independent async tasks simultaneously to minimize
        // total turn latency.
        var memoryTask = SafeModeEnabled ? Task.FromResult(new MemoryContextResult()) : GetMemoryContextSafeAsync(
            userMessage,
            _currentConversationId,
            cancellationToken);

        var slotStateBefore = _dialogueStore.Get();
        var slotTask = _slotExtract.RunAsync(userMessage, slotStateBefore, cancellationToken);

        // Pre-warm tool definitions in the background (MCP IPC).
        // Many paths short-circuit before needing tools, but if we
        // reach the tool loop the round-trip is already done.
        var toolDefsTask = _toolDefinitionBuilder.BuildAsync(
            MemoryEnabled, PanicModeEnabled, SafeModeEnabled, LogEvent, cancellationToken);

        // ── Route: classify intent + determine requirements ──────────
        var routeResolution = await ResolveRouteAsync(userMessage, lowerIncoming, cancellationToken);
        var route = NormalizeRouteForPrompt(routeResolution.Route, lowerIncoming);
        var webEvidence = routeResolution.WebEvidence;

        var policy = PolicyGate.Evaluate(route, PanicModeEnabled, SafeModeEnabled);
        LogEvent("POLICY_DECISION",
            $"allowedCaps=[{string.Join(", ", policy.AllowedCapabilities)}], " +
            $"forbiddenCaps=[{string.Join(", ", policy.ForbiddenCapabilities)}], " +
            $"forbiddenTools=[{string.Join(", ", policy.ForbiddenTools)}], " +
            $"permissions=[{string.Join(", ", policy.RequiredPermissions)}], " +
            $"useToolLoop={policy.UseToolLoop}");

        // Keep the old intent for the WebLookup deterministic path
        var intent = MapRouteToLegacyIntent(route);

        // ── Await Memory and Slots ───────────────────────────────────
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

        // ── Onboarding injection ──────────────────────────────────────
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

            var deterministicSpecialCase = _guardrailsCoordinator.TryRunDeterministicSpecialCase(
                contextualUserMessage,
                Guardrails.ReasoningGuardrailsMode.Auto);
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

            var activePersonality = _personalityRuntime.Snapshot.Profile;
            var utilityResponse = await _utilityIntentHandler.TryHandleAsync(
                new UtilityIntentExecutionRequest
                {
                    UserMessage = contextualUserMessage,
                    Route = route,
                    ToolPlan = toolPlan,
                    ActivePersonalityId = activePersonality.Id,
                    ActivePersonalityDisplayName = activePersonality.DisplayName,
                    ActivePersonalitySelfName = activePersonality.Identity.SelfName,
                    ActivePersonalitySelfDescription = activePersonality.Identity.SelfDescription,
                    ValidatedSlots = validatedSlots,
                    ToolCallsMade = toolCallsMade,
                    RoundTrips = roundTrips,
                    UserLocationHint = UserLocationHint,
                    PreferredUnits = PreferredUnits,
                    TryDeterministicMatch = _deterministicUtilityEngine.TryMatch,
                    ToUtilityResult = ToUtilityResult,
                    BuildFromToolPlan = BuildUtilityResultFromToolPlan,
                    TryContextFollowUp = _contextAnchoringService.TryHandleUtilityFollowUpWithContext,
                    TryInferWithLlmAsync = TryInferUtilityRouteWithLlmAsync,
                    RememberUtilityContext = utilityResult =>
                    {
                        var utilityPatch = _contextAnchoringService.TryBuildUtilityPatch(utilityResult);
                        if (utilityPatch is not null)
                            _contextAnchoringService.ApplyPatch(utilityPatch);
                    },
                    ExecuteWeatherAsync = async (message, utilityResult, calls, trips, token, slots) =>
                        await ExecuteWeatherUtilityAsync(
                            message,
                            utilityResult,
                            calls as List<ToolCallRecord> ?? toolCallsMade,
                            trips,
                            token,
                            slots),
                    ExecuteTimeAsync = async (message, utilityResult, calls, trips, token, slots) =>
                        await ExecuteTimeUtilityAsync(
                            message,
                            utilityResult,
                            calls as List<ToolCallRecord> ?? toolCallsMade,
                            trips,
                            token,
                            slots),
                    ExecuteHolidayAsync = async (utilityResult, calls, trips, token) =>
                        await ExecuteHolidayUtilityAsync(
                            utilityResult,
                            calls as List<ToolCallRecord> ?? toolCallsMade,
                            trips,
                            token),
                    ExecuteFeedAsync = async (utilityResult, calls, trips, token) =>
                        await ExecuteFeedUtilityAsync(
                            utilityResult,
                            calls as List<ToolCallRecord> ?? toolCallsMade,
                            trips,
                            token),
                    ExecuteStatusAsync = async (utilityResult, calls, trips, token) =>
                        await ExecuteStatusUtilityAsync(
                            utilityResult,
                            calls as List<ToolCallRecord> ?? toolCallsMade,
                            trips,
                            token),
                    ExecuteGenericToolCallAsync = async (utilityResult, calls, token) =>
                    {
                        if (utilityResult.McpToolName is null || utilityResult.McpToolArgs is null)
                            return;

                        try
                        {
                            var toolResult = await _mcp.CallToolAsync(
                                utilityResult.McpToolName,
                                utilityResult.McpToolArgs,
                                token);
                            calls.Add(new ToolCallRecord
                            {
                                ToolName = utilityResult.McpToolName,
                                Arguments = utilityResult.McpToolArgs,
                                Result = toolResult,
                                Success = true
                            });
                        }
                        catch
                        {
                            // Utility MCP call failed — fall through to normal pipeline
                        }
                    },
                    BuildInlineResponse = BuildInlineUtilityResponse,
                    ShouldSuppressUiArtifacts = ShouldSuppressUtilityUiArtifacts,
                    LogEvent = LogEvent
                },
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

            var guardrailsResult = await _guardrailsCoordinator.TryRunAsync(
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

            // ── Web lookup: delegate to SearchOrchestrator ─────────────
            if (intent == ChatIntent.WebLookup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lookupModeHint = ResolveLookupModeHint(route);

                // Inject memory context before search pipeline
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

                // Strip internal offline-reasoning scaffolding before the response
                // reaches the user. The prefix carries diagnostic framing that is
                // helpful for logging but harmful in user-facing text.
                var stripped = Search.SearchOrchestrator.StripOfflineReasoningPrefix(searchResponse.Text);
                if (!string.Equals(stripped, searchResponse.Text, StringComparison.Ordinal))
                    searchResponse = searchResponse with { Text = stripped };

                // Add the assistant's response to conversation history
                if (searchResponse.Success)
                    AppendAssistantMessage(searchResponse.Text);

                LogEvent("AGENT_RESPONSE", searchResponse.Text);
                return AttachContextSnapshot(
                    _contextAnchoringService.AddLocationInferenceDisclosure(searchResponse, validatedSlots),
                    usageBaseline);
            }

            // ── Screen observe: deterministic capture + LLM describe ──
            if (route.Intent.Equals(Intents.ScreenObserve, StringComparison.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await ExecuteDeterministicScreenCaptureAsync(
                    contextualUserMessage, memoryPackText,
                    personalityAnchor, personalityTurnTag,
                    toolCallsMade, roundTrips, usageBaseline,
                    cancellationToken);
            }

            // ── Inject memory context ─────────────────────────────────
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

            // ── Chat-only: skip tool loop entirely ───────────────────
            // No tools, no function-calling grammar. The LLM just
            // responds with text. Fastest path for casual conversation.
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

                // ── Truncation recovery ──────────────────────────────
                // If the LLM hit the token ceiling mid-sentence, retry
                // once with a larger budget so it can finish its thought.
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

                // ── Fallback gating (deterministic + non-looping) ─────
                // We only attempt search fallback for chat-only turns
                // that clearly contain refusal/uncertainty signals.
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

            // ── Policy-filtered tool loop ────────────────────────────
            // Await the pre-warmed tool definitions (kicked off early
            // alongside memory/slots to overlap with routing latency).
            var allTools = await toolDefsTask;
            var tools = FilterKnowledgeStoreToolsIfNeeded(
                PolicyGate.FilterTools(allTools, policy),
                contextualUserMessage);

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
            LogEvent("AGENT_CANCELLED", "Processing was cancelled.");
            return AttachContextSnapshot(new AgentResponse
            {
                Text = "Request was cancelled.",
                Success = false,
                Error = "Cancelled",
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            }, usageBaseline);
        }
        catch (Exception ex)
        {
            LogEvent("AGENT_ERROR", ex.Message);
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

    /// <summary>
    /// Clears the cached MCP tool list so the next turn re-fetches from the server.
    /// Call after MCP reconnect or tool manifest changes.
    /// </summary>
    public void InvalidateToolCache() => _toolDefinitionBuilder.InvalidateCache();

    /// <summary>
    /// Gets the current state of the conversation history.
    /// </summary>
    public IReadOnlyList<ChatMessage> GetCurrentHistory() => _history;

    /// <summary>
    /// Removes the last message from the history if it exists.
    /// </summary>
    public void PopUserMessage()
    {
        if (_history.Count > 0)
            _history.RemoveAt(_history.Count - 1);
    }

    private static AgentResponse ApplySeasonEpisodeExistenceSanityGate(
        string userMessage,
        AgentResponse response,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (!LooksLikeSeasonEpisodePrompt(userMessage))
            return response;

        if (!LooksSpeculativeNarrative(response.Text))
            return response;

        var sawNoResults = toolCallsMade.Any(c =>
            !string.IsNullOrWhiteSpace(c.Result) &&
            c.Result.StartsWith("No results found for ", StringComparison.OrdinalIgnoreCase));
        var sawCancelSignal = toolCallsMade.Any(c =>
            !string.IsNullOrWhiteSpace(c.Result) &&
            c.Result.Contains("cancel", StringComparison.OrdinalIgnoreCase));

        if (!sawNoResults && !sawCancelSignal)
            return response;

        var seasonLabel = TryExtractSeasonLabel(userMessage);
        var seasonPhrase = seasonLabel is null ? "that requested season" : seasonLabel;
        var corrected =
            $"Based on the available evidence, {seasonPhrase} does not exist. " +
            "It appears the show was canceled or never produced for that season, so there is no official episode plot to summarize.";

        return response with { Text = corrected };
    }

    private static AgentResponse NormalizeMetaToolHealthResponse(AgentResponse response)
    {
        if (!response.Success)
            return response;

        var sawHealthyToolPing = response.ToolCallsMade.Any(call =>
            call.Success &&
            call.ToolName.Equals("tool_ping", StringComparison.OrdinalIgnoreCase));

        if (!sawHealthyToolPing)
            return response;

        if (response.Text.Contains("healthy", StringComparison.OrdinalIgnoreCase))
            return response;

        var normalizedText = $"MCP tool execution is healthy. {response.Text}".Trim();
        return response with { Text = normalizedText };
    }

    private static bool LooksLikeSeasonEpisodePrompt(string userMessage)
    {
        var lower = (userMessage ?? "").ToLowerInvariant();
        return Regex.IsMatch(lower, @"\bseason\s+\d+\b", RegexOptions.IgnoreCase) &&
               Regex.IsMatch(lower, @"\bepisode\s+\d+\b", RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeDownloadRamMythPrompt(string userMessage)
    {
        var lower = (userMessage ?? "").ToLowerInvariant();
        return lower.Contains("download", StringComparison.Ordinal) &&
               lower.Contains("ram", StringComparison.Ordinal) &&
               lower.Contains("internet", StringComparison.Ordinal);
    }

    private static bool LooksSpeculativeNarrative(string text)
    {
        var lower = (text ?? "").ToLowerInvariant();
        return lower.Contains("would likely", StringComparison.Ordinal) ||
               lower.Contains("probably", StringComparison.Ordinal) ||
               lower.Contains("might", StringComparison.Ordinal) ||
               lower.Contains("expect", StringComparison.Ordinal);
    }

    private static string? TryExtractSeasonLabel(string text)
    {
        var match = Regex.Match(text ?? "", @"\bseason\s+\d+\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    private static IReadOnlyList<ToolDefinition> FilterKnowledgeStoreToolsIfNeeded(
        IReadOnlyList<ToolDefinition> tools,
        string userMessage)
    {
        if (tools.Count == 0)
            return tools;

        var lower = (userMessage ?? string.Empty).ToLowerInvariant();
        if (lower.Contains("knowledge_store_", StringComparison.Ordinal))
        {
            return tools
                .Where(tool => !IsGenericFileTool(tool.Function.Name))
                .ToList();
        }

        if (ShouldExposeKnowledgeStoreTools(userMessage))
            return tools;

        return tools
            .Where(tool => !IsKnowledgeStoreTool(tool.Function.Name))
            .ToList();
    }

    private static bool ShouldExposeKnowledgeStoreTools(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var lower = userMessage.ToLowerInvariant();
        var explicitKnowledgeStoreIntent =
            lower.Contains("knowledge_store", StringComparison.Ordinal) ||
            lower.Contains("knowledge store", StringComparison.Ordinal) ||
            lower.Contains("wiki", StringComparison.Ordinal) ||
            lower.Contains("note in my", StringComparison.Ordinal) ||
            lower.Contains("notes folder", StringComparison.Ordinal) ||
            lower.Contains("local notes", StringComparison.Ordinal) ||
            lower.Contains("save a note", StringComparison.Ordinal);

        var journalingIntent =
            lower.Contains("journal", StringComparison.Ordinal) &&
            (lower.Contains("write", StringComparison.Ordinal) ||
             lower.Contains("save", StringComparison.Ordinal) ||
             lower.Contains("log", StringComparison.Ordinal) ||
             lower.Contains("entry", StringComparison.Ordinal) ||
             lower.Contains("note", StringComparison.Ordinal) ||
             lower.Contains("daily", StringComparison.Ordinal) ||
             lower.Contains("today", StringComparison.Ordinal));

        if (!explicitKnowledgeStoreIntent && !journalingIntent)
            return false;

        if (!explicitKnowledgeStoreIntent &&
            (IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lower) ||
             IntentFeatureExtractor.LooksLikeWebSearchRequest(lower) ||
             IntentFeatureExtractor.LooksLikeFactLookup(lower) ||
             IntentFeatureExtractor.LooksLikePreferenceOrOpinionPrompt(lower)))
        {
            return false;
        }

        return true;
    }

    private static bool IsKnowledgeStoreTool(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        return toolName.Contains("knowledge_store", StringComparison.OrdinalIgnoreCase) ||
               toolName.StartsWith("KnowledgeStore", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenericFileTool(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        return toolName.Equals("file_read", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("file_list", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("FileRead", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("FileList", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("file_read_preview", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("file_read_apply", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("file_list_preview", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("file_list_apply", StringComparison.OrdinalIgnoreCase);
    }

    private static RouterOutput NormalizeRouteForPrompt(RouterOutput route, string lowerIncoming)
    {
        if (string.IsNullOrWhiteSpace(lowerIncoming))
            return route;

        if (lowerIncoming.Contains("knowledge_store_", StringComparison.Ordinal) ||
            lowerIncoming.Contains("knowledge store", StringComparison.Ordinal))
        {
            return DefaultRouter.MakeRoute(Intents.FileTask, confidence: 0.99, needsFile: true);
        }

        if ((IntentFeatureExtractor.LooksLikeFileRequest(lowerIncoming) ||
             string.Equals(
                 IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(lowerIncoming),
                 Intents.FileTask,
                 StringComparison.OrdinalIgnoreCase)) &&
            !route.Intent.Equals(Intents.FileTask, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultRouter.MakeRoute(Intents.FileTask, confidence: 0.98, needsFile: true);
        }

        if (IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lowerIncoming) &&
            !RouteArbitrationPolicy.IsLookupIntent(route.Intent))
        {
            return DefaultRouter.MakeRoute(Intents.LookupFact, confidence: 0.95, needsWeb: true, needsSearch: true);
        }

        if ((IntentFeatureExtractor.LooksLikePreferenceOrOpinionPrompt(lowerIncoming) ||
             IntentFeatureExtractor.LooksLikeSelfContainedReasoningPrompt(lowerIncoming)) &&
            !route.Intent.Equals(Intents.ChatOnly, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultRouter.MakeRoute(Intents.ChatOnly, confidence: 0.96);
        }

        return route;
    }

}
