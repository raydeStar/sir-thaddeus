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
using SirThaddeus.Agent.ToolLoop;
using SirThaddeus.Agent.Tools;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

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

    // Last resolved place from weather flow. Used to anchor short
    // follow-up weather/news prompts like "forecast for today?"
    // without forcing the user to repeat the city every turn.
    private string? _lastPlaceContextName;
    private string? _lastPlaceContextCountryCode;
    private DateTimeOffset _lastPlaceContextAt;
    private string? _lastUtilityContextKey;
    private DateTimeOffset _lastUtilityContextAt;
    private string _reasoningGuardrailsMode = "off";
    private IReadOnlyList<string> _lastFirstPrinciplesRationale = [];
    private DateTimeOffset _lastFirstPrinciplesAt;

    private const int MaxToolRoundTrips  = 10;  // Safety valve
    private const int DefaultWebSearchMaxResults = 5;
    private static readonly TimeSpan PlaceContextTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan UtilityContextTtl = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan FirstPrinciplesFollowUpTtl = TimeSpan.FromMinutes(15);

    // ── Web search tool names ────────────────────────────────────────
    // MCP stacks may register tools in snake_case or PascalCase.
    // Try the canonical name first, fall back to the alternate.
    private const string WebSearchToolName    = "web_search";
    private const string WebSearchToolNameAlt = "WebSearch";
    private const string WeatherGeocodeToolName    = "weather_geocode";
    private const string WeatherGeocodeToolNameAlt = "WeatherGeocode";
    private const string WeatherForecastToolName    = "weather_forecast";
    private const string WeatherForecastToolNameAlt = "WeatherForecast";
    private const string ResolveTimezoneToolName    = "resolve_timezone";
    private const string ResolveTimezoneToolNameAlt = "ResolveTimezone";
    private const string HolidaysGetToolName        = "holidays_get";
    private const string HolidaysGetToolNameAlt     = "HolidaysGet";
    private const string HolidaysNextToolName       = "holidays_next";
    private const string HolidaysNextToolNameAlt    = "HolidaysNext";
    private const string HolidaysIsTodayToolName    = "holidays_is_today";
    private const string HolidaysIsTodayToolNameAlt = "HolidaysIsToday";
    private const string FeedFetchToolName          = "feed_fetch";
    private const string FeedFetchToolNameAlt       = "FeedFetch";
    private const string StatusCheckToolName        = "status_check_url";
    private const string StatusCheckToolNameAlt     = "StatusCheckUrl";
    private const string MemoryRetrieveToolName     = "memory_retrieve";
    private const string MemoryRetrieveToolNameAlt  = "MemoryRetrieve";
    private const string MemoryListFactsToolName    = "memory_list_facts";
    private const string MemoryListFactsToolNameAlt = "MemoryListFacts";

    // ── Summary instruction injected after search results ────────────
    private const string WebSummaryInstruction =
        "\n\nSearch results are in the next message. " +
        "Synthesize across all sources into a concise, practical answer. " +
        "Lead with the bottom line in one sentence, then 3-5 short points. " +
        "No markdown tables. No URLs. " +
        "ONLY use facts from the provided sources. " +
        "Do NOT invent or guess details not in the results.";

    // ── Summary instruction injected for follow-up deep dives ───────
    private const string WebFollowUpInstruction =
        "\n\nFull article content from prior sources is in the next message. " +
        "Answer the user's latest question using ONLY the provided content. " +
        "Be thorough. No markdown tables. No URLs. " +
        "If a detail is not present in the content, say so.";

    private const string WebFollowUpWithRelatedInstruction =
        "\n\nYou are answering a follow-up question about a specific news story. " +
        "Full text from the primary article(s) is included first, followed by " +
        "related coverage search results.\n" +
        "Answer the user's question. Lead with the bottom line. Then explain:\n" +
        "- What the primary article(s) say\n" +
        "- What related sources add or contradict\n" +
        "- Whether key details are confirmed or still alleged\n" +
        "No markdown tables. No URLs. Do not list sources unless you need to explain a disagreement.";

    // ── Token budget per intent ──────────────────────────────────────
    // Small models fill available space with filler. Tight caps force
    // them to be concise and reduce self-dialogue / instruction echoing.
    private const int MaxTokensCasual    = 160;
    private const int MaxTokensWebSummary = 768;
    private const int MaxTokensTooling   = 1024;
    private const int MaxTokensUtilityRouting = 120;

    // Hard ceiling on memory retrieval. If the MCP tool + SQLite +
    // optional embeddings don't finish in this window, we skip memory
    // entirely and proceed with the conversation. Non-negotiable.
    private static readonly TimeSpan MemoryRetrievalTimeout = TimeSpan.FromMilliseconds(1500);

    // ── Onboarding prompts ────────────────────────────────────────────
    // Injected when no active profile is loaded for this session.
    //
    // Cold prompt: first turn — actively introduce yourself and ask.
    // Follow-up: subsequent turns — passively capture info if shared.
    //
    // The LLM MUST have memory tools available when these are active
    // (see the policy override in ProcessAsync). Without tools, the
    // model can't persist anything the user shares.

    private const string OnboardingColdPrompt =
        "\n\n[ONBOARDING]\n" +
        "No profile is loaded — you don't know who you're talking to yet.\n" +
        "Introduce yourself warmly (stay in character) and ask who they are.\n" +
        "If they share their name, IMMEDIATELY call memory_store_facts to save it:\n" +
        "  {\"subject\": \"user\", \"predicate\": \"name\", \"object\": \"<their name>\"}\n" +
        "Then ask 2-3 light questions to get to know them — what they work on, " +
        "a preference or two, how they like to be addressed.\n" +
        "Keep it casual and brief. If they say they'd rather not share or " +
        "want to skip, that is perfectly fine — just say something like " +
        "'No problem at all' and help them with whatever they need.\n" +
        "Do NOT ignore their original message — answer it too, " +
        "just weave the introduction in naturally.\n" +
        "[/ONBOARDING]\n";

    private const string OnboardingFollowUpPrompt =
        "\n\n[ONBOARDING]\n" +
        "You still don't know who this user is.\n" +
        "If they share personal details (name, preferences, etc.), " +
        "use memory_store_facts to save them.\n" +
        "Do NOT keep asking if they clearly want to move on — just help them.\n" +
        "[/ONBOARDING]\n";

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
    /// First principles thinking mode:
    ///   - off: disable guardrail reasoning pipeline
    ///   - auto: run only when detector flags likely goal-conflict prompt
    ///   - always: run guardrail reasoning pass on each non-utility turn
    /// </summary>
    public string ReasoningGuardrailsMode
    {
        get => _reasoningGuardrailsMode;
        set => _reasoningGuardrailsMode =
            SirThaddeus.Agent.Guardrails.ReasoningGuardrailsMode.Normalize(value);
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
        IUtilityIntentHandler? utilityIntentHandler = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _systemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));
        _timeProvider = timeProvider ?? TimeProvider.System;

        _searchOrchestrator = new SearchOrchestrator(llm, mcp, audit, systemPrompt);
        _dialogueStore = dialogueStateStore ?? new DialogueStateStore(_timeProvider);
        _slotExtract = slotExtract ?? new SlotExtract(llm, audit);
        _mergeSlots = mergeSlots ?? new MergeSlots();
        _validateSlots = validateSlots ?? new ValidateSlots(new ValidationOptions
        {
            GeocodeMismatchMode = geocodeMismatchMode
        });
        _toolPlanner = toolPlanner ?? new ToolPlanner();
        _reasoningGuardrailsPipeline = new ReasoningGuardrailsPipeline(llm, audit);
        _deterministicUtilityEngine = deterministicUtilityEngine ?? new DeterministicUtilityEngineAdapter();
        _router = router ?? new DefaultRouter(llm, _deterministicUtilityEngine);
        _memoryContextProvider = memoryContextProvider ?? new MemoryContextProvider(mcp, audit, _timeProvider);
        _toolLoopExecutor = toolLoopExecutor ?? new ToolLoopExecutor(llm, mcp);
        _guardrailsCoordinator = guardrailsCoordinator ?? new GuardrailsCoordinator(_reasoningGuardrailsPipeline);
        _toolDefinitionBuilder = new ToolDefinitionBuilder(mcp);
        _postProcessor = new DeterministicChatPostProcessor();
        _selfMemorySummarizer = selfMemorySummarizer ?? new SelfMemorySummarizer(mcp);
        _searchFallbackExecutor = searchFallbackExecutor ?? new SearchFallbackExecutor(_searchOrchestrator);
        _contextAnchoringService = contextAnchoringService ?? new ContextAnchoringService(
            _dialogueStore,
            _searchOrchestrator,
            _timeProvider);
        _utilityIntentHandler = utilityIntentHandler ?? new UtilityIntentHandler();

        // Seed the conversation with the system prompt
        _history.Add(ChatMessage.System(_systemPrompt));
    }

    /// <inheritdoc />
    public async Task<AgentResponse> ProcessAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return AttachContextSnapshot(AgentResponse.FromError("Empty message."));

        var lowerIncoming = userMessage.Trim().ToLowerInvariant();
        if (!LooksLikeReasoningFollowUp(lowerIncoming))
        {
            _lastFirstPrinciplesRationale = [];
            _lastFirstPrinciplesAt = default;
        }

        // ── Add user message to history ──────────────────────────────
        _history.Add(ChatMessage.User(userMessage));
        TrimHistory();
        LogEvent("AGENT_USER_MESSAGE", userMessage);

        var toolCallsMade = new List<ToolCallRecord>();
        var roundTrips = 0;

        // ── Route: classify intent + determine requirements ──────────
        var route = await _router.RouteAsync(
            new RouterRequest
            {
                UserMessage = userMessage,
                HasRecentFirstPrinciplesRationale = HasRecentFirstPrinciplesRationale(),
                HasRecentSearchResults = _searchOrchestrator.Session.HasRecentResults(_timeProvider.GetUtcNow())
            },
            cancellationToken);
        LogEvent("ROUTER_OUTPUT",
            $"intent={route.Intent}, confidence={route.Confidence:F2}, " +
            $"web={route.NeedsWeb}, screen={route.NeedsScreenRead}, " +
            $"file={route.NeedsFileAccess}, memory_w={route.NeedsMemoryWrite}, " +
            $"system={route.NeedsSystemExecute}, risk={route.RiskLevel}, " +
            $"capabilities=[{string.Join(", ", route.RequiredCapabilities)}]");

        // ── Policy: determine which tools the executor may see ───────
        var policy = PolicyGate.Evaluate(route);
        LogEvent("POLICY_DECISION",
            $"allowedCaps=[{string.Join(", ", policy.AllowedCapabilities)}], " +
            $"forbiddenCaps=[{string.Join(", ", policy.ForbiddenCapabilities)}], " +
            $"forbiddenTools=[{string.Join(", ", policy.ForbiddenTools)}], " +
            $"permissions=[{string.Join(", ", policy.RequiredPermissions)}], " +
            $"useToolLoop={policy.UseToolLoop}");

        // Keep the old intent for the WebLookup deterministic path
        var intent = MapRouteToLegacyIntent(route);

        // ── Retrieve memory context (best-effort, hard timeout) ──
        // Called after classification but before the main LLM call.
        // The pack text is injected into the system prompt so the
        // model has relevant facts/events/chunks for its answer.
        // If retrieval takes longer than the timeout, we skip it
        // entirely rather than stalling the user's conversation.
        //
        // When MemoryEnabled is false (master memory off), skip
        // retrieval entirely — no MCP call, no timeout wait.
        var memoryPackText = "";
        var onboardingNeeded = false;
        var memoryError = "";
        try
        {
            var memoryContext = await _memoryContextProvider.GetContextAsync(
                new MemoryContextRequest
                {
                    UserMessage = userMessage,
                    MemoryEnabled = MemoryEnabled,
                    IsColdGreeting = IsColdGreeting(userMessage),
                    ActiveProfileId = ActiveProfileId,
                    Timeout = MemoryRetrievalTimeout
                },
                cancellationToken);

            memoryPackText = memoryContext.PackText;
            onboardingNeeded = memoryContext.OnboardingNeeded;
            memoryError = memoryContext.Error ?? "";

            if (!MemoryEnabled)
            {
                LogEvent("MEMORY_DISABLED", "Memory is off — skipping retrieval.");
            }
            else if (memoryContext.Provenance.TimedOut)
            {
                LogEvent("MEMORY_TIMEOUT", "Memory retrieval exceeded timeout — skipped.");
            }

            if (MemoryEnabled)
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
        catch
        {
            // Swallow — memory is best-effort
        }

        // ── Onboarding injection ──────────────────────────────────────
        // When no active profile is loaded and this is the first turn,
        // inject the "get to know you" prompt. On subsequent turns,
        // inject a lighter reminder that passively captures info.
        //
        // Also force the tool loop on so the LLM can call
        // memory_store_facts when the user shares their name.
        //
        // Suppressed when memory is off — the model can't store facts anyway.
        if (onboardingNeeded && MemoryEnabled)
        {
            var isFirstTurn = _history.Count(m => m.Role == "user") <= 1;
            memoryPackText = isFirstTurn
                ? OnboardingColdPrompt
                : OnboardingFollowUpPrompt;

            // Upgrade chat-only → memory_write so the LLM has access
            // to memory tools. Without this, the model is told to call
            // memory_store_facts but has no tools available.
            //
            // IMPORTANT: only override Casual intent. Do NOT touch
            // WebLookup, Tooling, or other intents — their routing
            // takes priority over onboarding. The onboarding prompt
            // is still injected into the system context regardless,
            // so the LLM can passively capture info during any flow.
            if (intent == ChatIntent.Casual)
            {
                route = DefaultRouter.MakeRoute(Intents.MemoryWrite, confidence: 0.9,
                    needsMemoryWrite: true);
                policy = PolicyGate.Evaluate(route);
                intent = MapRouteToLegacyIntent(route);
            }

            LogEvent("ONBOARDING_INJECTED",
                isFirstTurn ? "First turn — introducing and asking who the user is."
                            : "Follow-up — passively capturing info.");
        }

        var stateBefore = _dialogueStore.Get();
        var extractedSlots = await _slotExtract.RunAsync(userMessage, stateBefore, cancellationToken);
        var mergedSlots = _mergeSlots.Run(stateBefore, extractedSlots, _timeProvider.GetUtcNow());
        var validatedSlots = _validateSlots.Run(stateBefore, mergedSlots);
        UpdateDialogueStateFromValidatedSlots(validatedSlots);
        var toolPlan = _toolPlanner.Plan(validatedSlots, stateBefore);

        var contextualUserMessage = string.IsNullOrWhiteSpace(validatedSlots.NormalizedMessage)
            ? userMessage
            : validatedSlots.NormalizedMessage;
        contextualUserMessage = _contextAnchoringService.ApplyPlaceContextIfHelpful(contextualUserMessage);

        if (!string.Equals(contextualUserMessage, userMessage, StringComparison.Ordinal))
        {
            LogEvent("PLACE_CONTEXT_INFERRED",
                $"{Truncate(userMessage, 80)} -> {Truncate(contextualUserMessage, 120)}");
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
                _history.Add(ChatMessage.Assistant(firstPrinciplesFollowUp.Text));
                LogEvent("FIRST_PRINCIPLES_FOLLOWUP", firstPrinciplesFollowUp.Text);
                return AttachContextSnapshot(firstPrinciplesFollowUp);
            }

            var deterministicSpecialCase = _guardrailsCoordinator.TryRunDeterministicSpecialCase(
                contextualUserMessage,
                ReasoningGuardrailsMode);
            if (deterministicSpecialCase is not null)
            {
                var specialCaseText = deterministicSpecialCase.AnswerText;
                _lastFirstPrinciplesRationale = deterministicSpecialCase.RationaleLines.Take(3).ToArray();
                _lastFirstPrinciplesAt = _timeProvider.GetUtcNow();
                _history.Add(ChatMessage.Assistant(specialCaseText));
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
                });
            }

            var utilityResponse = await _utilityIntentHandler.TryHandleAsync(
                new UtilityIntentExecutionRequest
                {
                    UserMessage = contextualUserMessage,
                    Route = route,
                    ToolPlan = toolPlan,
                    ValidatedSlots = validatedSlots,
                    ToolCallsMade = toolCallsMade,
                    RoundTrips = roundTrips,
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
                    _history.Add(ChatMessage.Assistant(utilityResponse.Text));
                    LogEvent("AGENT_RESPONSE", utilityResponse.Text);
                }

                return AttachContextSnapshot(
                    _contextAnchoringService.AddLocationInferenceDisclosure(utilityResponse, validatedSlots));
            }

            var guardrailsResult = await _guardrailsCoordinator.TryRunAsync(
                route,
                contextualUserMessage,
                ReasoningGuardrailsMode,
                cancellationToken);

            if (guardrailsResult is not null)
            {
                roundTrips += guardrailsResult.LlmRoundTrips;

                var guardedText = guardrailsResult.AnswerText;
                _lastFirstPrinciplesRationale = guardrailsResult.RationaleLines.Take(3).ToArray();
                _lastFirstPrinciplesAt = _timeProvider.GetUtcNow();
                _history.Add(ChatMessage.Assistant(guardedText));
                LogEvent("GUARDRAILS_RESPONSE",
                    $"risk={guardrailsResult.TriggerRisk}, source={guardrailsResult.TriggerSource}, why={guardrailsResult.TriggerWhy}");
                LogEvent("AGENT_RESPONSE", guardedText);

                return AttachContextSnapshot(new AgentResponse
                {
                    Text = guardedText,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = roundTrips,
                    GuardrailsUsed = true,
                    GuardrailsRationale = guardrailsResult.RationaleLines
                });
            }

            if (MemoryEnabled && _selfMemorySummarizer.IsSelfMemoryKnowledgeRequest(contextualUserMessage))
            {
                var memorySummary = await _selfMemorySummarizer.BuildSummaryResponseAsync(
                    ActiveProfileId,
                    toolCallsMade,
                    roundTrips,
                    cancellationToken);
                _history.Add(ChatMessage.Assistant(memorySummary.Text));
                LogEvent("AGENT_RESPONSE", memorySummary.Text);
                return AttachContextSnapshot(memorySummary);
            }

            // ── Web lookup: delegate to SearchOrchestrator ─────────────
            if (intent == ChatIntent.WebLookup)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Inject memory context before search pipeline
                if (!string.IsNullOrWhiteSpace(memoryPackText))
                    InjectMemoryIntoHistoryInPlace(_history, memoryPackText);

                var searchResponse = await _searchOrchestrator.ExecuteAsync(
                    contextualUserMessage, memoryPackText, _history, toolCallsMade, cancellationToken);

                // Add the assistant's response to conversation history
                if (searchResponse.Success)
                    _history.Add(ChatMessage.Assistant(searchResponse.Text));

                LogEvent("AGENT_RESPONSE", searchResponse.Text);
                return AttachContextSnapshot(
                    _contextAnchoringService.AddLocationInferenceDisclosure(searchResponse, validatedSlots));
            }

            // ── Inject memory context ─────────────────────────────────
            if (!string.IsNullOrWhiteSpace(memoryPackText))
                InjectMemoryIntoHistoryInPlace(_history, memoryPackText);

            // ── Chat-only: skip tool loop entirely ───────────────────
            // No tools, no function-calling grammar. The LLM just
            // responds with text. Fastest path for casual conversation.
            if (!policy.UseToolLoop)
            {
                cancellationToken.ThrowIfCancellationRequested();
                roundTrips++;

                var messages = _history.ToList();
                var response = await CallLlmWithRetrySafe(
                    messages, roundTrips, MaxTokensCasual, cancellationToken);

                var text = _postProcessor.ProcessChatOnlyDraft(
                    response.Content ?? "[No response]",
                    contextualUserMessage,
                    toolCallsMade,
                    LogEvent);

                // ── Fallback: if template tokens ate the whole response,
                // the user probably asked a follow-up about something
                // the model can't answer from memory alone (e.g., a
                // person or event from a previous web search). Try a
                // web search before giving up. ──────────────────────────
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (IsDeterministicInlineRoute(route))
                    {
                        const string deterministicFallback =
                            "I could not finish that deterministic conversion. " +
                            "Try a direct format like \"350F in C\".";
                        LogEvent("DETERMINISTIC_NO_WEB_ENFORCED",
                            "Suppressed chat fallback web search for deterministic route.");
                        _history.Add(ChatMessage.Assistant(deterministicFallback));
                        LogEvent("AGENT_RESPONSE", deterministicFallback);
                        return AttachContextSnapshot(new AgentResponse
                        {
                            Text = deterministicFallback,
                            Success = true,
                            ToolCallsMade = toolCallsMade,
                            LlmRoundTrips = roundTrips,
                            SuppressSourceCardsUi = true,
                            SuppressToolActivityUi = true
                        });
                    }

                    LogEvent("CHAT_FALLBACK_TO_SEARCH",
                        "Response was all template garbage — " +
                        "falling back to web search.");

                    return AttachContextSnapshot(await _searchFallbackExecutor.ExecuteAsync(
                        new SearchFallbackRequest
                        {
                            UserMessage = contextualUserMessage,
                            MemoryPackText = memoryPackText,
                            History = _history,
                            ToolCallsMade = toolCallsMade,
                            RoundTrips = roundTrips,
                            LogEvent = LogEvent
                        },
                        cancellationToken));
                }

                _history.Add(ChatMessage.Assistant(text));
                LogEvent("AGENT_RESPONSE", text);

                return AttachContextSnapshot(new AgentResponse
                {
                    Text = text,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = roundTrips
                });
            }

            // ── Policy-filtered tool loop ────────────────────────────
            // Build the full tool set, then filter through the policy
            // gate. The executor only sees what the policy allows.
            var allTools = await _toolDefinitionBuilder.BuildAsync(
                MemoryEnabled,
                LogEvent,
                cancellationToken);
            var tools = PolicyGate.FilterTools(allTools, policy);

            LogEvent("AGENT_TOOLS_POLICY_FILTERED",
                $"{tools.Count} tool(s) from {allTools.Count} total: " +
                $"[{string.Join(", ", tools.Select(t => t.Function.Name))}]");

            return AttachContextSnapshot(await RunToolLoopAsync(
                tools, toolCallsMade, roundTrips, cancellationToken));
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
            });
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
            });
        }
    }
}
