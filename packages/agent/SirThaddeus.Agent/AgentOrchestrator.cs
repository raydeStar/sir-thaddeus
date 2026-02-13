using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Search;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent;

/// <summary>
/// Implements the agent loop: user message -> LLM -> tool calls -> LLM -> ... -> final text.
/// Holds in-memory conversation history and delegates tool execution to the MCP client.
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
        string geocodeMismatchMode = "fallback_previous")
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
        var route = await RouteMessageAsync(userMessage, cancellationToken);
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
        if (!MemoryEnabled)
        {
            LogEvent("MEMORY_DISABLED", "Memory is off — skipping retrieval.");
        }
        else try
        {
            var memoryTask = RetrieveMemoryContextAsync(userMessage, cancellationToken);
            if (await Task.WhenAny(memoryTask, Task.Delay(MemoryRetrievalTimeout, cancellationToken)) == memoryTask)
            {
                (memoryPackText, onboardingNeeded, memoryError) = await memoryTask;

                // Surface the retrieval in the activity log so the user
                // can see what was recalled (or that nothing was found).
                var summary = !string.IsNullOrWhiteSpace(memoryError)
                    ? $"Memory retrieve error: {memoryError}"
                    : string.IsNullOrWhiteSpace(memoryPackText)
                        ? "No relevant memories found."
                        : memoryPackText.Replace("\n", " ").Trim();
                if (summary.Length > 200) summary = summary[..200] + "\u2026";

                toolCallsMade.Add(new ToolCallRecord
                {
                    ToolName  = "MemoryRetrieve",
                    Arguments = $"{{\"query\":\"{Truncate(userMessage, 80)}\"}}",
                    Result    = summary,
                    Success   = string.IsNullOrWhiteSpace(memoryError)
                });
            }
            else
            {
                LogEvent("MEMORY_TIMEOUT", "Memory retrieval exceeded timeout — skipped.");
                toolCallsMade.Add(new ToolCallRecord
                {
                    ToolName  = "MemoryRetrieve",
                    Arguments = $"{{\"query\":\"{Truncate(userMessage, 80)}\"}}",
                    Result    = "Timeout — skipped.",
                    Success   = false
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
                route = MakeRoute(Intents.MemoryWrite, confidence: 0.9,
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
        contextualUserMessage = ApplyPlaceContextIfHelpful(contextualUserMessage);

        if (!string.Equals(contextualUserMessage, userMessage, StringComparison.Ordinal))
        {
            LogEvent("PLACE_CONTEXT_INFERRED",
                $"{Truncate(userMessage, 80)} -> {Truncate(contextualUserMessage, 120)}");
        }

        var hasLoadedProfileContext =
            !string.IsNullOrWhiteSpace(ActiveProfileId) ||
            memoryPackText.Contains("[PROFILE]", StringComparison.OrdinalIgnoreCase);

        if (MemoryEnabled &&
            !IsSelfMemoryKnowledgeRequest(contextualUserMessage) &&
            IsPersonalizedUsingKnownSelfContextRequest(contextualUserMessage, hasLoadedProfileContext))
        {
            var personalizedFactsContext = await BuildSelfMemoryContextBlockAsync(
                toolCallsMade, cancellationToken);
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

            var guardrailsEnabled =
                SirThaddeus.Agent.Guardrails.ReasoningGuardrailsMode.IsEnabled(
                    SirThaddeus.Agent.Guardrails.ReasoningGuardrailsMode.Normalize(ReasoningGuardrailsMode));
            if (guardrailsEnabled)
            {
                var deterministicSpecialCase =
                    _reasoningGuardrailsPipeline.TryRunDeterministicSpecialCase(contextualUserMessage);
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
            }

            // ── Utility bypass: weather, time, calc, conversion ────────
            // Intercepted BEFORE the search pipeline to avoid overhead.
            var deterministicRouteRequested = IsDeterministicInlineRoute(route);
            UtilityRouter.UtilityResult? utilityResult = null;

            // Deterministic utility engine always runs first so this path
            // stays model-free and web-free for obvious conversion/math asks.
            var deterministicMatch = DeterministicPreRouter.TryRoute(contextualUserMessage);
            if (deterministicMatch is not null)
            {
                utilityResult = ToUtilityResult(deterministicMatch);
                LogEvent(
                    "DETERMINISTIC_INLINE_ROUTE",
                    $"confidence={deterministicMatch.Confidence}, category={deterministicMatch.Result.Category}");
            }

            utilityResult ??= BuildUtilityResultFromToolPlan(toolPlan, contextualUserMessage);
            utilityResult ??= TryHandleUtilityFollowUpWithContext(contextualUserMessage)
                ?? UtilityRouter.TryHandle(contextualUserMessage);

            if (utilityResult is null && !deterministicRouteRequested)
            {
                utilityResult = await TryInferUtilityRouteWithLlmAsync(
                    contextualUserMessage, cancellationToken);
            }

            if (utilityResult is null && deterministicRouteRequested)
            {
                LogEvent(
                    "DETERMINISTIC_INLINE_MISS",
                    "Pre-router selected deterministic path, but utility parse failed at execution.");
            }
            if (utilityResult is not null)
            {
                LogEvent("UTILITY_BYPASS", $"category={utilityResult.Category}");
                RememberUtilityContext(utilityResult);

                // Weather uses a dedicated two-step MCP flow:
                //   weather_geocode -> weather_forecast
                // This keeps routing deterministic and avoids brittle string search.
                if (string.Equals(utilityResult.Category, "weather", StringComparison.OrdinalIgnoreCase))
                {
                    var weatherResponse = await ExecuteWeatherUtilityAsync(
                        contextualUserMessage, utilityResult, toolCallsMade, roundTrips, cancellationToken, validatedSlots);
                    return AttachContextSnapshot(
                        AddLocationInferenceDisclosure(weatherResponse, validatedSlots));
                }

                if (string.Equals(utilityResult.Category, "time", StringComparison.OrdinalIgnoreCase))
                {
                    var timeResponse = await ExecuteTimeUtilityAsync(
                        contextualUserMessage, utilityResult, toolCallsMade, roundTrips, cancellationToken, validatedSlots);
                    return AttachContextSnapshot(
                        AddLocationInferenceDisclosure(timeResponse, validatedSlots));
                }

                if (string.Equals(utilityResult.Category, "holiday", StringComparison.OrdinalIgnoreCase))
                {
                    return AttachContextSnapshot(await ExecuteHolidayUtilityAsync(
                        utilityResult, toolCallsMade, roundTrips, cancellationToken));
                }

                if (string.Equals(utilityResult.Category, "feed", StringComparison.OrdinalIgnoreCase))
                {
                    return AttachContextSnapshot(await ExecuteFeedUtilityAsync(
                        utilityResult, toolCallsMade, roundTrips, cancellationToken));
                }

                if (string.Equals(utilityResult.Category, "status", StringComparison.OrdinalIgnoreCase))
                {
                    return AttachContextSnapshot(await ExecuteStatusUtilityAsync(
                        utilityResult, toolCallsMade, roundTrips, cancellationToken));
                }

                // If the utility wants an MCP tool call, do it
                if (utilityResult.McpToolName is not null && utilityResult.McpToolArgs is not null)
                {
                    try
                    {
                        var toolResult = await _mcp.CallToolAsync(
                            utilityResult.McpToolName, utilityResult.McpToolArgs, cancellationToken);
                        toolCallsMade.Add(new ToolCallRecord
                        {
                            ToolName  = utilityResult.McpToolName,
                            Arguments = utilityResult.McpToolArgs,
                            Result    = toolResult,
                            Success   = true
                        });
                    }
                    catch
                    {
                        // Utility MCP call failed — fall through to normal pipeline
                    }
                }
                else
                {
                    // Inline utility answer (time, calc, conversion, fact)
                    var text = BuildInlineUtilityResponse(utilityResult);
                    var suppressUiArtifacts = ShouldSuppressUtilityUiArtifacts(utilityResult.Category);
                    _history.Add(ChatMessage.Assistant(text));
                    LogEvent("AGENT_RESPONSE", text);
                    var inlineResponse = new AgentResponse
                    {
                        Text          = text,
                        Success       = true,
                        ToolCallsMade = toolCallsMade,
                        LlmRoundTrips = 0,
                        SuppressSourceCardsUi = suppressUiArtifacts,
                        SuppressToolActivityUi = suppressUiArtifacts
                    };
                    return AttachContextSnapshot(
                        AddLocationInferenceDisclosure(inlineResponse, validatedSlots));
                }
            }

            if (MemoryEnabled && IsSelfNameRecallRequest(contextualUserMessage))
            {
                var nameRecall = await ExecuteSelfNameRecallAsync(
                    contextualUserMessage,
                    toolCallsMade,
                    roundTrips,
                    cancellationToken);
                _history.Add(ChatMessage.Assistant(nameRecall.Text));
                LogEvent("AGENT_RESPONSE", nameRecall.Text);
                return AttachContextSnapshot(nameRecall);
            }

            if (ShouldAttemptReasoningGuardrails(route, contextualUserMessage))
            {
                var guardrailsResult = await _reasoningGuardrailsPipeline.TryRunAsync(
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
            }

            if (MemoryEnabled && IsSelfMemoryKnowledgeRequest(contextualUserMessage))
            {
                var memorySummary = await ExecuteSelfMemorySummaryAsync(
                    toolCallsMade, roundTrips, cancellationToken);
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
                    AddLocationInferenceDisclosure(searchResponse, validatedSlots));
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

                var text = StripThinkingScaffold(response.Content ?? "[No response]");
                text = TruncateSelfDialogue(text);
                text = StripRawTemplateTokens(text);
                text = TrimDanglingIncompleteEnding(text);

                // Thinking models occasionally return internal scratchpad text
                // as the assistant message. Detect and rewrite once into a
                // direct, user-facing answer.
                if (LooksLikeThinkingLeak(text))
                {
                    LogEvent("AGENT_THINKING_LEAK",
                        $"finish_reason={response.FinishReason ?? "unknown"}");

                    var rewriteMessages = new List<ChatMessage>
                    {
                        ChatMessage.System(
                            _systemPrompt + " " +
                            "Rewrite the draft into the final assistant reply. " +
                            "Output ONLY the user-facing answer. " +
                            "No chain-of-thought. No references to instructions. " +
                            "Do not narrate your internal reasoning."),
                        ChatMessage.User(
                            "User message:\n" +
                            $"{contextualUserMessage}\n\n" +
                            "Leaked draft:\n" +
                            $"{text}")
                    };

                    roundTrips++;
                    var rewritten = await CallLlmWithRetrySafe(
                        rewriteMessages,
                        roundTrips,
                        Math.Max(MaxTokensCasual, 256),
                        cancellationToken);

                    var rewrittenText = StripThinkingScaffold(rewritten.Content ?? "");
                    rewrittenText = TruncateSelfDialogue(rewrittenText);
                    rewrittenText = StripRawTemplateTokens(rewrittenText);
                    rewrittenText = TrimDanglingIncompleteEnding(rewrittenText);

                    if (!string.IsNullOrWhiteSpace(rewrittenText) &&
                        !LooksLikeThinkingLeak(rewrittenText))
                    {
                        text = rewrittenText;
                    }
                }

                // Small models occasionally ignore the latest user turn and
                // continue a previous math thread. If the user did not ask for
                // math, rewrite once to force a direct response.
                if (LooksLikeUnsolicitedCalculation(contextualUserMessage, text))
                {
                    LogEvent("AGENT_OFFTOPIC_CALC_REWRITE",
                        "Detected calculation-style reply for non-math user turn.");

                    var rewriteMessages = new List<ChatMessage>
                    {
                        ChatMessage.System(
                            _systemPrompt + " " +
                            "Rewrite the draft into a direct answer to the user's latest message. " +
                            "Do not continue prior calculations unless the user explicitly asked for math. " +
                            "Keep it concise and user-facing."),
                        ChatMessage.User(
                            "User message:\n" +
                            $"{contextualUserMessage}\n\n" +
                            "Off-topic draft:\n" +
                            $"{text}")
                    };

                    roundTrips++;
                    var rewritten = await CallLlmWithRetrySafe(
                        rewriteMessages,
                        roundTrips,
                        Math.Max(MaxTokensCasual, 192),
                        cancellationToken);

                    var rewrittenText = StripThinkingScaffold(rewritten.Content ?? "");
                    rewrittenText = TruncateSelfDialogue(rewrittenText);
                    rewrittenText = StripRawTemplateTokens(rewrittenText);
                    rewrittenText = TrimDanglingIncompleteEnding(rewrittenText);

                    if (!string.IsNullOrWhiteSpace(rewrittenText) &&
                        !LooksLikeUnsolicitedCalculation(contextualUserMessage, rewrittenText))
                    {
                        text = rewrittenText;
                    }
                    else
                    {
                        text = "I got off track there. Ask that again and I'll answer your latest message directly.";
                    }
                }

                // Another drift mode on small models: the assistant starts
                // role-playing as the user and asks them to solve math.
                if (LooksLikeRoleConfusedMathAsk(contextualUserMessage, text))
                {
                    LogEvent("AGENT_ROLE_CONFUSION_REWRITE",
                        "Detected assistant asking user to solve math on a non-math turn.");

                    var rewriteMessages = new List<ChatMessage>
                    {
                        ChatMessage.System(
                            _systemPrompt + " " +
                            "Rewrite the draft into a direct reply to the user's latest message. " +
                            "Stay in assistant role. Do not ask the user to solve math for you. " +
                            "Keep it brief and respectful."),
                        ChatMessage.User(
                            "User message:\n" +
                            $"{contextualUserMessage}\n\n" +
                            "Role-confused draft:\n" +
                            $"{text}")
                    };

                    roundTrips++;
                    var rewritten = await CallLlmWithRetrySafe(
                        rewriteMessages,
                        roundTrips,
                        Math.Max(MaxTokensCasual, 192),
                        cancellationToken);

                    var rewrittenText = StripThinkingScaffold(rewritten.Content ?? "");
                    rewrittenText = TruncateSelfDialogue(rewrittenText);
                    rewrittenText = StripRawTemplateTokens(rewrittenText);
                    rewrittenText = TrimDanglingIncompleteEnding(rewrittenText);

                    if (!string.IsNullOrWhiteSpace(rewrittenText) &&
                        !LooksLikeRoleConfusedMathAsk(contextualUserMessage, rewrittenText))
                    {
                        text = rewrittenText;
                    }
                    else
                    {
                        text = BuildRespectfulResetReply();
                    }
                }

                // Hard clamp for unsafe mirrored phrases from low-quality model
                // outputs. We never want to speak this content back to the user.
                if (LooksLikeUnsafeMirroringResponse(contextualUserMessage, text))
                {
                    LogEvent("AGENT_SAFETY_OVERRIDE",
                        "Detected unsafe mirrored language in assistant draft.");
                    text = BuildRespectfulResetReply();
                }

                // If the user turn is abusive, keep the boundary concise.
                // This avoids instruction leakage and keeps the reply stable.
                if (LooksLikeAbusiveUserTurn(contextualUserMessage))
                {
                    LogEvent("AGENT_ABUSIVE_USER_BOUNDARY",
                        "Detected abusive user turn; returning boundary response.");
                    text = BuildRespectfulResetReply();
                }

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

                    return AttachContextSnapshot(await FallbackToWebSearchAsync(
                        contextualUserMessage, memoryPackText,
                        toolCallsMade, roundTrips, cancellationToken));
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
            var allTools = await BuildToolDefinitionsAsync(cancellationToken);
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

    // ─────────────────────────────────────────────────────────────────
    // Tool Loop
    //
    // Shared by both casual (memory-only tools) and tooling (all tools)
    // paths. Iterates until the LLM produces a final text answer or
    // we hit the safety cap.
    // ─────────────────────────────────────────────────────────────────

    private async Task<AgentResponse> RunToolLoopAsync(
        IReadOnlyList<ToolDefinition> tools,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        var allowedToolNames = new HashSet<string>(
            tools.Select(t => t.Function.Name),
            StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            roundTrips++;

            // ── Call the LLM ─────────────────────────────────────
            LogEvent("AGENT_LLM_CALL", $"Round trip #{roundTrips}");
            LlmResponse response;
            try
            {
                response = await _llm.ChatAsync(_history, tools, cancellationToken);
            }
            catch (HttpRequestException ex) when (IsLmStudioRegexFailure(ex) && tools is { Count: > 0 })
            {
                // LM Studio sometimes fails to compile the function-calling grammar
                // ("Failed to process regex") on follow-up calls after tool results.
                // Retrying *without tools* avoids schema/grammar compilation entirely
                // and allows the model to summarize the tool outputs normally.
                LogEvent("AGENT_LLM_REGEX_RETRY", "LM Studio regex failure — retrying without tools");
                response = await _llm.ChatAsync(_history, tools: null, cancellationToken);
            }

            // ── If the model produced a final answer, we're done ─
            if (response.IsComplete || response.ToolCalls is not { Count: > 0 })
            {
                var text = StripThinkingScaffold(response.Content ?? "[No response]");
                text = TruncateSelfDialogue(text);
                text = StripRawTemplateTokens(text);
                text = TrimDanglingIncompleteEnding(text);
                _history.Add(ChatMessage.Assistant(text));
                LogEvent("AGENT_RESPONSE", text);

                return new AgentResponse
                {
                    Text = text,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = roundTrips
                };
            }

            // ── Model wants to call tools ────────────────────────
            _history.Add(ChatMessage.AssistantToolCalls(response.ToolCalls));

            // Resolve policy violations and conflicts before any MCP call
            // executes so skipped tools cannot produce side effects.
            var conflictResolution = ToolConflictMatrix.ResolveTurn(
                response.ToolCalls,
                allowedToolNames);

            foreach (var skipped in conflictResolution.Skipped)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reasonCode = ToolConflictMatrix.ToReasonCode(skipped.Reason);
                var isPolicyForbid = skipped.Reason == ToolConflictReason.PolicyForbid;
                var result = JsonSerializer.Serialize(new
                {
                    error = isPolicyForbid ? "tool_not_permitted" : "tool_conflict_skipped",
                    tool = skipped.ToolCall.Function.Name,
                    winner = skipped.WinnerTool,
                    reason = reasonCode,
                    detail = skipped.Detail
                });
                var success = false;

                LogEvent(
                    isPolicyForbid ? "AGENT_TOOL_BLOCKED" : "AGENT_TOOL_CONFLICT",
                    $"tool={skipped.ToolCall.Function.Name}, winner={skipped.WinnerTool ?? "none"}, reason={reasonCode}, detail={skipped.Detail}");

                toolCallsMade.Add(new ToolCallRecord
                {
                    ToolName = skipped.ToolCall.Function.Name,
                    Arguments = skipped.ToolCall.Function.Arguments,
                    Result = result,
                    Success = success
                });

                _history.Add(ChatMessage.ToolResult(skipped.ToolCall.Id, result));
                LogEvent("AGENT_TOOL_RESULT", $"{skipped.ToolCall.Function.Name} -> skipped");
            }

            foreach (var toolCall in conflictResolution.Winners)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LogEvent("AGENT_TOOL_CALL", $"{toolCall.Function.Name}({toolCall.Function.Arguments})");

                string result;
                bool success;

                try
                {
                    result = await _mcp.CallToolAsync(
                        toolCall.Function.Name,
                        toolCall.Function.Arguments,
                        cancellationToken);
                    success = true;
                }
                catch (Exception ex)
                {
                    result = $"Tool error: {ex.Message}";
                    success = false;
                }

                toolCallsMade.Add(new ToolCallRecord
                {
                    ToolName = toolCall.Function.Name,
                    Arguments = toolCall.Function.Arguments,
                    Result = result,
                    Success = success
                });

                _history.Add(ChatMessage.ToolResult(toolCall.Id, result));
                LogEvent("AGENT_TOOL_RESULT", $"{toolCall.Function.Name} -> {(success ? "ok" : "error")}");
            }

            // Safety: prevent infinite loops
            if (roundTrips >= MaxToolRoundTrips)
            {
                var bailMsg = "Reached maximum tool round-trips. Returning partial result.";
                _history.Add(ChatMessage.Assistant(bailMsg));
                LogEvent("AGENT_MAX_ROUNDS", bailMsg);
                return new AgentResponse
                {
                    Text = bailMsg,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = roundTrips
                };
            }
        }
    }

    private static bool IsLmStudioRegexFailure(HttpRequestException ex)
    {
        var msg = ex.Message ?? "";
        return msg.Contains("Failed to process regex", StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────
    // Web Search Execution (shared pipeline)
    //
    // Single implementation of the extract → search → summarize flow.
    // Called from the primary WebLookup intent path and from the
    // chat-only fallback. Keeps all tool-name negotiation, raw-dump
    // rewriting, and template stripping in one place.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Core web search pipeline: extracts a query from the user message,
    /// calls the <c>web_search</c> MCP tool, and summarizes the results.
    /// </summary>
    /// <param name="logPrefix">
    /// Prefix for audit log events — distinguishes primary ("AGENT")
    /// from fallback ("FALLBACK") search paths.
    /// </param>
    private async Task<AgentResponse> ExecuteWebSearchAsync(
        string userMessage,
        string memoryPackText,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        string logPrefix,
        CancellationToken cancellationToken)
    {
        // ── Follow-up deep dive: reuse prior sources ─────────────────
        // If the user is clearly asking "more details on that story",
        // prefer fetching the articles we already discovered last turn
        // rather than issuing a vague re-search ("more X news").
        var followUp = await TryAnswerFollowUpFromLastSourcesAsync(
            userMessage, memoryPackText, toolCallsMade, roundTrips, cancellationToken);
        if (followUp is not null)
            return followUp;

        // ── 1. Extract search query via structured tool call ─────────
        var (searchQuery, recency) = await ExtractSearchViaToolCallAsync(
            userMessage, memoryPackText, cancellationToken);
        LogEvent($"{logPrefix}_SEARCH_QUERY",
            $"\"{userMessage}\" → \"{searchQuery}\" (recency={recency})");
        _searchOrchestrator.Session.LastRecency = recency;

        // ── 2. Execute web_search MCP tool ───────────────────────────
        var webSearchArgs = JsonSerializer.Serialize(new
        {
            query      = searchQuery,
            maxResults = DefaultWebSearchMaxResults,
            recency
        });

        var toolName = WebSearchToolName;
        string toolResult;
        var toolOk = false;

        try
        {
            LogEvent("AGENT_TOOL_CALL", $"{toolName}({webSearchArgs})");
            toolResult = await _mcp.CallToolAsync(toolName, webSearchArgs, cancellationToken);
            toolOk = true;
        }
        catch (Exception ex)
        {
            // Back-compat: some MCP stacks register PascalCase tool names.
            try
            {
                toolName = WebSearchToolNameAlt;
                LogEvent("AGENT_TOOL_CALL", $"{toolName}({webSearchArgs})");
                toolResult = await _mcp.CallToolAsync(toolName, webSearchArgs, cancellationToken);
                toolOk = true;
            }
            catch
            {
                toolResult = $"Tool error: {ex.Message}";
            }
        }

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName  = toolName,
            Arguments = webSearchArgs,
            Result    = toolResult,
            Success   = toolOk
        });
        LogEvent("AGENT_TOOL_RESULT", $"{toolName} -> {(toolOk ? "ok" : "error")}");

        // ── 2.5. Cache source URLs for follow-ups ────────────────────
        /* Sources now tracked in SearchOrchestrator.Session */

        // ── 3. Summarize results ─────────────────────────────────────
        // Feed tool output as a User message (not a ToolResult).
        // Mistral's Jinja template requires tool_call_ids to be exactly
        // 9-char alphanumeric, and ToolResult roles must follow an
        // assistant message with matching tool_calls. A plain User
        // message sidesteps the whole template minefield.
        roundTrips++;

        var summaryInput = "[Search results — reference only, do not display to user]\n" +
                           toolResult;

        var messagesForSummary = InjectModeIntoSystemPrompt(
            _history, memoryPackText + WebSummaryInstruction);
        messagesForSummary.Add(ChatMessage.User(summaryInput));

        var response = await CallLlmWithRetrySafe(
            messagesForSummary, roundTrips, MaxTokensWebSummary, cancellationToken);

        string text;

        if (response.FinishReason == "error")
        {
            // The LLM couldn't summarize (e.g., LM Studio regex engine
            // choked on the search result payload). Don't throw away the
            // results — build a brief extractive response instead.
            LogEvent("AGENT_SUMMARY_FALLBACK",
                "LLM summary failed — building extractive fallback");
            text = BuildExtractiveSummary(toolResult, searchQuery);
        }
        else
        {
            text = StripThinkingScaffold(response.Content ?? "[No response]");
            text = TruncateSelfDialogue(text);

            // ── 4. Raw dump → rewrite ────────────────────────────────
            if (LooksLikeRawDump(text))
            {
                LogEvent("AGENT_REWRITE", "Response looked like a raw dump — rewriting");
                var rewriteMessages = new List<ChatMessage>
                {
                    ChatMessage.System(
                        _systemPrompt + " " +
                        "Rewrite the draft into the final answer. " +
                        "Casual tone. Bottom line first. 2-3 short paragraphs. " +
                        "No markdown tables. No URLs. No lists of sources. No copied excerpts. " +
                        "Do NOT add facts not present in the draft."),
                    ChatMessage.User(text)
                };

                roundTrips++;
                var rewritten = await CallLlmWithRetrySafe(
                    rewriteMessages, roundTrips, MaxTokensWebSummary, cancellationToken);
                if (!string.IsNullOrWhiteSpace(rewritten.Content) &&
                    rewritten.FinishReason != "error")
                    text = StripThinkingScaffold(rewritten.Content!);
            }
        }

        // ── 5. Strip template garbage ────────────────────────────────
        text = StripThinkingScaffold(text);
        text = StripRawTemplateTokens(text);
        text = TrimDanglingIncompleteEnding(text);
        if (string.IsNullOrWhiteSpace(text))
            text = "I wasn't able to generate a clean answer for that. " +
                   "Could you try asking a different way?";

        _history.Add(ChatMessage.Assistant(text));
        LogEvent("AGENT_RESPONSE", text);

        return new AgentResponse
        {
            Text          = text,
            Success       = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    /// <summary>
    /// Executes the weather utility flow using dedicated MCP tools:
    /// weather_geocode -> weather_forecast. Returns a short deterministic
    /// weather summary without re-entering the web search pipeline.
    /// </summary>
    private async Task<AgentResponse> ExecuteWeatherUtilityAsync(
        string userMessage,
        UtilityRouter.UtilityResult utilityResult,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken,
        ValidatedSlots? validatedSlots = null)
    {
        if (utilityResult.McpToolName is null || utilityResult.McpToolArgs is null)
            return AgentResponse.FromError("Weather utility is missing required geocode args.");

        var geocodeCall = await CallToolWithAliasAsync(
            WeatherGeocodeToolName, WeatherGeocodeToolNameAlt,
            utilityResult.McpToolArgs, cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = geocodeCall.ToolName,
            Arguments = utilityResult.McpToolArgs,
            Result = geocodeCall.Result,
            Success = geocodeCall.Success
        });

        if (!geocodeCall.Success)
        {
            var errorText = "I couldn't resolve that location for weather lookup. " +
                            "Try a city and region like \"Rexburg, ID\".";
            _history.Add(ChatMessage.Assistant(errorText));
            return new AgentResponse
            {
                Text = errorText,
                Success = false,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        if (!TryParseBestGeocodeCandidate(geocodeCall.Result, out var geo))
        {
            var noLocationText = "I couldn't find coordinates for that location. " +
                                 "Try a more specific place name.";
            _history.Add(ChatMessage.Assistant(noLocationText));
            return new AgentResponse
            {
                Text = noLocationText,
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var activeState = _dialogueStore.Get();
        var explicitLocationChange = validatedSlots?.ExplicitLocationChange ?? false;
        var mismatchReason = "";
        var geocodeMismatch = false;
        if (!explicitLocationChange)
        {
            geocodeMismatch = ValidateSlots.IsStronglyDivergent(
                activeState,
                geo.CountryCode,
                geo.RegionCode,
                geo.Latitude,
                geo.Longitude,
                out mismatchReason);
        }
        var mismatchWarning = "";

        if (geocodeMismatch)
        {
            if (_validateSlots.ShouldRequireConfirm())
            {
                var confirmText =
                    $"I found **{geo.Name}**, but that conflicts with your current location context " +
                    $"(**{activeState.LocationName ?? "unknown"}**). Please confirm if you want me to switch.";

                _history.Add(ChatMessage.Assistant(confirmText));
                _dialogueStore.Update(activeState with { GeocodeMismatch = true });
                return new AgentResponse
                {
                    Text = confirmText,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = roundTrips
                };
            }

            if (!activeState.ContextLocked &&
                activeState.Latitude.HasValue &&
                activeState.Longitude.HasValue &&
                !string.IsNullOrWhiteSpace(activeState.LocationName))
            {
                mismatchWarning =
                    $"I detected a location mismatch ({mismatchReason.Replace('_', ' ')}), " +
                    $"so I kept your prior location context: **{activeState.LocationName}**.";

                geo = (
                    activeState.LocationName!,
                    activeState.CountryCode ?? geo.CountryCode,
                    activeState.RegionCode ?? geo.RegionCode,
                    activeState.Latitude.Value,
                    activeState.Longitude.Value
                );
            }
        }

        RememberPlaceContext(
            geo.Name,
            geo.CountryCode,
            geo.RegionCode,
            geo.Latitude,
            geo.Longitude,
            locationInferred: validatedSlots?.LocationInferred ?? false,
            geocodeMismatch: geocodeMismatch,
            explicitLocationChange: validatedSlots?.ExplicitLocationChange ?? false);

        var forecastArgs = JsonSerializer.Serialize(new
        {
            latitude = geo.Latitude,
            longitude = geo.Longitude,
            placeHint = geo.Name,
            countryCode = geo.CountryCode,
            days = 7
        });

        var forecastCall = await CallToolWithAliasAsync(
            WeatherForecastToolName, WeatherForecastToolNameAlt,
            forecastArgs, cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = forecastCall.ToolName,
            Arguments = forecastArgs,
            Result = forecastCall.Result,
            Success = forecastCall.Success
        });

        if (!forecastCall.Success)
        {
            var errorText = "I couldn't fetch the weather details right now. " +
                            "Please try again in a moment.";
            _history.Add(ChatMessage.Assistant(errorText));
            return new AgentResponse
            {
                Text = errorText,
                Success = false,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var weatherBrief = TryBuildWeatherBriefFromForecastJson(
            forecastCall.Result, userMessage, geo.Name);

        if (string.IsNullOrWhiteSpace(weatherBrief))
        {
            weatherBrief = "I found weather data, but couldn't extract a clean snapshot yet. " +
                           "Try asking again and I'll refresh it.";
        }

        if (!string.IsNullOrWhiteSpace(mismatchWarning))
            weatherBrief = $"{mismatchWarning}\n\n{weatherBrief}";

        _history.Add(ChatMessage.Assistant(weatherBrief));
        LogEvent("AGENT_RESPONSE", weatherBrief);

        return new AgentResponse
        {
            Text = weatherBrief,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<AgentResponse> ExecuteTimeUtilityAsync(
        string userMessage,
        UtilityRouter.UtilityResult utilityResult,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken,
        ValidatedSlots? validatedSlots = null)
    {
        if (utilityResult.McpToolName is null || utilityResult.McpToolArgs is null)
            return AgentResponse.FromError("Time utility is missing required geocode args.");

        var geocodeCall = await CallToolWithAliasAsync(
            WeatherGeocodeToolName, WeatherGeocodeToolNameAlt,
            utilityResult.McpToolArgs, cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = geocodeCall.ToolName,
            Arguments = utilityResult.McpToolArgs,
            Result = geocodeCall.Result,
            Success = geocodeCall.Success
        });

        if (!geocodeCall.Success)
        {
            var errorText = "I couldn't resolve that location for a timezone lookup.";
            _history.Add(ChatMessage.Assistant(errorText));
            return new AgentResponse
            {
                Text = errorText,
                Success = false,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        if (!TryParseBestGeocodeCandidate(geocodeCall.Result, out var geo))
        {
            var noLocationText = "I couldn't find coordinates for that location. Try a more specific city/country.";
            _history.Add(ChatMessage.Assistant(noLocationText));
            return new AgentResponse
            {
                Text = noLocationText,
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var activeState = _dialogueStore.Get();
        var explicitLocationChange = validatedSlots?.ExplicitLocationChange ?? false;
        var mismatchReason = "";
        var geocodeMismatch = false;
        if (!explicitLocationChange)
        {
            geocodeMismatch = ValidateSlots.IsStronglyDivergent(
                activeState,
                geo.CountryCode,
                geo.RegionCode,
                geo.Latitude,
                geo.Longitude,
                out mismatchReason);
        }
        var mismatchWarning = "";

        if (geocodeMismatch)
        {
            if (_validateSlots.ShouldRequireConfirm())
            {
                var confirmText =
                    $"I found **{geo.Name}**, but that conflicts with your current location context " +
                    $"(**{activeState.LocationName ?? "unknown"}**). Please confirm if you want me to switch.";

                _history.Add(ChatMessage.Assistant(confirmText));
                _dialogueStore.Update(activeState with { GeocodeMismatch = true });
                return new AgentResponse
                {
                    Text = confirmText,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = roundTrips
                };
            }

            if (!activeState.ContextLocked &&
                activeState.Latitude.HasValue &&
                activeState.Longitude.HasValue &&
                !string.IsNullOrWhiteSpace(activeState.LocationName))
            {
                mismatchWarning =
                    $"I detected a location mismatch ({mismatchReason.Replace('_', ' ')}), " +
                    $"so I kept your prior location context: **{activeState.LocationName}**.";

                geo = (
                    activeState.LocationName!,
                    activeState.CountryCode ?? geo.CountryCode,
                    activeState.RegionCode ?? geo.RegionCode,
                    activeState.Latitude.Value,
                    activeState.Longitude.Value
                );
            }
        }

        RememberPlaceContext(
            geo.Name,
            geo.CountryCode,
            geo.RegionCode,
            geo.Latitude,
            geo.Longitude,
            locationInferred: validatedSlots?.LocationInferred ?? false,
            geocodeMismatch: geocodeMismatch,
            explicitLocationChange: validatedSlots?.ExplicitLocationChange ?? false);

        var timezoneArgs = JsonSerializer.Serialize(new
        {
            latitude = geo.Latitude,
            longitude = geo.Longitude,
            countryCode = geo.CountryCode
        });

        var timezoneCall = await CallToolWithAliasAsync(
            ResolveTimezoneToolName, ResolveTimezoneToolNameAlt,
            timezoneArgs, cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = timezoneCall.ToolName,
            Arguments = timezoneArgs,
            Result = timezoneCall.Result,
            Success = timezoneCall.Success
        });

        if (!timezoneCall.Success)
        {
            var errorText = "I couldn't resolve the timezone for that location right now.";
            _history.Add(ChatMessage.Assistant(errorText));
            return new AgentResponse
            {
                Text = errorText,
                Success = false,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var timeBrief = TryBuildTimeBriefFromTimezoneJson(
            timezoneCall.Result, geo.Name, userMessage);

        if (string.IsNullOrWhiteSpace(timeBrief))
        {
            timeBrief = $"I found the location for **{geo.Name}**, but couldn't build a clean time answer yet.";
        }

        if (!string.IsNullOrWhiteSpace(mismatchWarning))
            timeBrief = $"{mismatchWarning}\n\n{timeBrief}";

        _history.Add(ChatMessage.Assistant(timeBrief));
        LogEvent("AGENT_RESPONSE", timeBrief);

        return new AgentResponse
        {
            Text = timeBrief,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<AgentResponse> ExecuteHolidayUtilityAsync(
        UtilityRouter.UtilityResult utilityResult,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        if (utilityResult.McpToolName is null || utilityResult.McpToolArgs is null)
            return AgentResponse.FromError("Holiday utility is missing tool args.");

        var holidayCall = await CallUtilityToolWithAliasAsync(
            utilityResult.McpToolName,
            utilityResult.McpToolArgs,
            cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = holidayCall.ToolName,
            Arguments = utilityResult.McpToolArgs,
            Result = holidayCall.Result,
            Success = holidayCall.Success
        });

        if (!holidayCall.Success)
        {
            var errorText = "I couldn't fetch holiday data right now. Please try again in a moment.";
            _history.Add(ChatMessage.Assistant(errorText));
            return new AgentResponse
            {
                Text = errorText,
                Success = false,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var holidayText = BuildHolidayUtilityResponse(
            utilityResult.McpToolName, holidayCall.Result);

        _history.Add(ChatMessage.Assistant(holidayText));
        LogEvent("AGENT_RESPONSE", holidayText);

        return new AgentResponse
        {
            Text = holidayText,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<AgentResponse> ExecuteFeedUtilityAsync(
        UtilityRouter.UtilityResult utilityResult,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        if (utilityResult.McpToolName is null || utilityResult.McpToolArgs is null)
            return AgentResponse.FromError("Feed utility is missing tool args.");

        var feedCall = await CallUtilityToolWithAliasAsync(
            utilityResult.McpToolName,
            utilityResult.McpToolArgs,
            cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = feedCall.ToolName,
            Arguments = utilityResult.McpToolArgs,
            Result = feedCall.Result,
            Success = feedCall.Success
        });

        if (!feedCall.Success)
        {
            var errorText = "I couldn't fetch that feed right now.";
            _history.Add(ChatMessage.Assistant(errorText));
            return new AgentResponse
            {
                Text = errorText,
                Success = false,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var feedText = BuildFeedUtilityResponse(feedCall.Result);
        _history.Add(ChatMessage.Assistant(feedText));
        LogEvent("AGENT_RESPONSE", feedText);

        return new AgentResponse
        {
            Text = feedText,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<AgentResponse> ExecuteStatusUtilityAsync(
        UtilityRouter.UtilityResult utilityResult,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        if (utilityResult.McpToolName is null || utilityResult.McpToolArgs is null)
            return AgentResponse.FromError("Status utility is missing tool args.");

        var statusCall = await CallUtilityToolWithAliasAsync(
            utilityResult.McpToolName,
            utilityResult.McpToolArgs,
            cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = statusCall.ToolName,
            Arguments = utilityResult.McpToolArgs,
            Result = statusCall.Result,
            Success = statusCall.Success
        });

        if (!statusCall.Success)
        {
            var errorText = "I couldn't complete that reachability check right now.";
            _history.Add(ChatMessage.Assistant(errorText));
            return new AgentResponse
            {
                Text = errorText,
                Success = false,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var statusText = BuildStatusUtilityResponse(statusCall.Result);
        _history.Add(ChatMessage.Assistant(statusText));
        LogEvent("AGENT_RESPONSE", statusText);

        return new AgentResponse
        {
            Text = statusText,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<(string ToolName, string Result, bool Success)> CallUtilityToolWithAliasAsync(
        string toolName,
        string argsJson,
        CancellationToken cancellationToken)
    {
        return toolName.ToLowerInvariant() switch
        {
            HolidaysGetToolName => await CallToolWithAliasAsync(
                HolidaysGetToolName, HolidaysGetToolNameAlt, argsJson, cancellationToken),
            HolidaysNextToolName => await CallToolWithAliasAsync(
                HolidaysNextToolName, HolidaysNextToolNameAlt, argsJson, cancellationToken),
            HolidaysIsTodayToolName => await CallToolWithAliasAsync(
                HolidaysIsTodayToolName, HolidaysIsTodayToolNameAlt, argsJson, cancellationToken),
            FeedFetchToolName => await CallToolWithAliasAsync(
                FeedFetchToolName, FeedFetchToolNameAlt, argsJson, cancellationToken),
            StatusCheckToolName => await CallToolWithAliasAsync(
                StatusCheckToolName, StatusCheckToolNameAlt, argsJson, cancellationToken),
            ResolveTimezoneToolName => await CallToolWithAliasAsync(
                ResolveTimezoneToolName, ResolveTimezoneToolNameAlt, argsJson, cancellationToken),
            _ => await CallToolWithAliasAsync(
                toolName, ToPascalCaseToolAlias(toolName), argsJson, cancellationToken)
        };
    }

    private static string ToPascalCaseToolAlias(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return toolName;

        var parts = toolName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0)
                continue;
            sb.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
                sb.Append(part[1..]);
        }

        return sb.Length == 0 ? toolName : sb.ToString();
    }

    private async Task<(string ToolName, string Result, bool Success)> CallToolWithAliasAsync(
        string primaryToolName,
        string alternateToolName,
        string argsJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mcp.CallToolAsync(primaryToolName, argsJson, cancellationToken);
            if (IsUnknownToolError(result, primaryToolName))
            {
                try
                {
                    var altResult = await _mcp.CallToolAsync(alternateToolName, argsJson, cancellationToken);
                    return (alternateToolName, altResult, true);
                }
                catch (Exception alternateError)
                {
                    var errorText = $"Error: {result}; fallback failed: {alternateError.Message}";
                    return (primaryToolName, errorText, false);
                }
            }

            return (primaryToolName, result, true);
        }
        catch (Exception primaryError)
        {
            try
            {
                var result = await _mcp.CallToolAsync(alternateToolName, argsJson, cancellationToken);
                return (alternateToolName, result, true);
            }
            catch (Exception alternateError)
            {
                var errorText = $"Error: {primaryError.Message}; fallback failed: {alternateError.Message}";
                return (primaryToolName, errorText, false);
            }
        }
    }

    private static bool TryParseBestGeocodeCandidate(
        string geocodeJson,
        out (string Name, string CountryCode, string RegionCode, double Latitude, double Longitude) candidate)
    {
        candidate = default;
        if (string.IsNullOrWhiteSpace(geocodeJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(geocodeJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return false;

            JsonElement? best = null;
            double bestConfidence = double.NegativeInfinity;
            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("latitude", out var latProbeEl) || !latProbeEl.TryGetDouble(out _))
                    continue;
                if (!item.TryGetProperty("longitude", out var lonProbeEl) || !lonProbeEl.TryGetDouble(out _))
                    continue;

                var confidence = item.TryGetProperty("confidence", out var confEl) &&
                                 confEl.ValueKind == JsonValueKind.Number &&
                                 confEl.TryGetDouble(out var conf)
                    ? conf
                    : 0.0;

                if (best is null || confidence > bestConfidence)
                {
                    best = item;
                    bestConfidence = confidence;
                }
            }

            if (best is null)
                return false;

            var r = best.Value;
            if (!r.TryGetProperty("latitude", out var latEl) || !latEl.TryGetDouble(out var lat))
                return false;
            if (!r.TryGetProperty("longitude", out var lonEl) || !lonEl.TryGetDouble(out var lon))
                return false;

            var name = r.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "") : "";
            var countryCode = r.TryGetProperty("countryCode", out var ccEl) ? (ccEl.GetString() ?? "") : "";
            var regionCode =
                r.TryGetProperty("regionCode", out var rcEl) ? (rcEl.GetString() ?? "") :
                (r.TryGetProperty("region", out var regionEl) ? (regionEl.GetString() ?? "") : "");

            candidate = (name, countryCode, regionCode, lat, lon);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds a short deterministic weather response from the normalized
    /// weather_forecast MCP JSON output.
    /// </summary>
    private static string? TryBuildWeatherBriefFromForecastJson(
        string forecastJson,
        string userMessage,
        string fallbackLocation)
    {
        if (string.IsNullOrWhiteSpace(forecastJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(forecastJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(err.GetString()))
            {
                return null;
            }

            var location = fallbackLocation;
            if (root.TryGetProperty("location", out var loc) &&
                loc.ValueKind == JsonValueKind.Object &&
                loc.TryGetProperty("name", out var ln) &&
                !string.IsNullOrWhiteSpace(ln.GetString()))
            {
                location = ln.GetString()!;
            }
            else
            {
                var fromMessage = ExtractLocationFromWeatherMessage(userMessage);
                if (!string.IsNullOrWhiteSpace(fromMessage))
                    location = fromMessage!;
            }

            int? currentTemp = null;
            string unit = "";
            string condition = "";

            if (root.TryGetProperty("current", out var current) &&
                current.ValueKind == JsonValueKind.Object)
            {
                if (current.TryGetProperty("temperature", out var t) && t.TryGetInt32(out var ti))
                    currentTemp = ti;
                if (current.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String)
                    unit = u.GetString() ?? "";
                if (current.TryGetProperty("condition", out var c) && c.ValueKind == JsonValueKind.String)
                    condition = c.GetString() ?? "";
            }

            int? avgTemp = null;
            if (root.TryGetProperty("daily", out var daily) &&
                daily.ValueKind == JsonValueKind.Array)
            {
                foreach (var day in daily.EnumerateArray())
                {
                    if (day.TryGetProperty("avgTemp", out var avg) && avg.TryGetInt32(out var av))
                    {
                        avgTemp = av;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(location))
                location = "there";

            var unitSuffix = string.IsNullOrWhiteSpace(unit) ? "" : unit.ToUpperInvariant();
            var avgSuffix = string.IsNullOrWhiteSpace(unitSuffix) ? "" : unitSuffix;
            if (LooksLikeWeatherActivityAdviceRequest(userMessage))
            {
                return BuildWeatherActivityAdvice(
                    location,
                    currentTemp,
                    unitSuffix,
                    condition,
                    avgTemp,
                    avgSuffix);
            }

            if (currentTemp.HasValue && !string.IsNullOrWhiteSpace(condition))
            {
                var line = $"In {location}, it's about **{currentTemp}{unitSuffix}** and **{condition}** right now.";
                return avgTemp.HasValue
                    ? $"{line} Avg temp: **{avgTemp}{avgSuffix}**."
                    : line;
            }

            if (currentTemp.HasValue)
            {
                var line = $"In {location}, it's about **{currentTemp}{unitSuffix}** right now.";
                return avgTemp.HasValue
                    ? $"{line} Avg temp: **{avgTemp}{avgSuffix}**."
                    : line;
            }

            if (!string.IsNullOrWhiteSpace(condition))
            {
                var line = $"In {location}, conditions are **{condition}** right now.";
                return avgTemp.HasValue
                    ? $"{line} Avg temp: **{avgTemp}{avgSuffix}**."
                    : line;
            }

            if (avgTemp.HasValue)
                return $"In {location}, avg temp is **{avgTemp}{avgSuffix}**.";

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildWeatherActivityAdvice(
        string location,
        int? currentTemp,
        string unitSuffix,
        string? condition,
        int? avgTemp,
        string avgSuffix)
    {
        var conditionLower = (condition ?? "").ToLowerInvariant();
        var tempForHeuristic = ToFahrenheit(currentTemp ?? avgTemp, unitSuffix);

        var isWet =
            conditionLower.Contains("rain", StringComparison.Ordinal) ||
            conditionLower.Contains("snow", StringComparison.Ordinal) ||
            conditionLower.Contains("sleet", StringComparison.Ordinal) ||
            conditionLower.Contains("drizzle", StringComparison.Ordinal) ||
            conditionLower.Contains("shower", StringComparison.Ordinal) ||
            conditionLower.Contains("storm", StringComparison.Ordinal);

        var isIcy =
            conditionLower.Contains("ice", StringComparison.Ordinal) ||
            conditionLower.Contains("freez", StringComparison.Ordinal);

        var isWindy =
            conditionLower.Contains("wind", StringComparison.Ordinal) ||
            conditionLower.Contains("gust", StringComparison.Ordinal);

        var isCold = tempForHeuristic is <= 45;
        var isHot = tempForHeuristic is >= 85;

        var snapshot = BuildWeatherSnapshot(location, currentTemp, unitSuffix, condition, avgTemp, avgSuffix);
        var plan = "Good options: a short walk, errands on foot, or light outdoor activity.";
        var caution = "Bring a layer and check conditions before heading out.";

        if (isWet || isIcy || isCold)
        {
            plan = "Best fit right now: mostly indoor plans (gym/rec center, cafe + reading, movie/museum).";
            caution = "If you go outside, keep it short and use warm waterproof layers plus good traction.";
        }
        else if (isHot)
        {
            plan = "Best fit right now: early/late outdoor time, shaded spots, or indoor options with AC.";
            caution = "Bring water and avoid long midday exposure.";
        }
        else if (isWindy)
        {
            plan = "Good options: low-exposure outdoor plans or indoor activities with easy fallback.";
            caution = "Avoid long exposed routes if gusts pick up.";
        }

        return $"{snapshot} {plan} {caution}";
    }

    private static string BuildWeatherSnapshot(
        string location,
        int? currentTemp,
        string unitSuffix,
        string? condition,
        int? avgTemp,
        string avgSuffix)
    {
        if (currentTemp.HasValue && !string.IsNullOrWhiteSpace(condition))
            return $"In {location}, it's about {currentTemp}{unitSuffix} with {condition.ToLowerInvariant()} right now.";

        if (currentTemp.HasValue)
            return $"In {location}, it's about {currentTemp}{unitSuffix} right now.";

        if (!string.IsNullOrWhiteSpace(condition))
            return $"In {location}, conditions are {condition.ToLowerInvariant()} right now.";

        if (avgTemp.HasValue)
            return $"In {location}, average temp is around {avgTemp}{avgSuffix}.";

        return $"In {location}, weather conditions are available.";
    }

    private static double? ToFahrenheit(int? temp, string unitSuffix)
    {
        if (!temp.HasValue)
            return null;

        if (string.Equals(unitSuffix, "C", StringComparison.OrdinalIgnoreCase))
            return (temp.Value * 9.0 / 5.0) + 32.0;

        return temp.Value;
    }

    private string? TryBuildTimeBriefFromTimezoneJson(
        string timezoneJson,
        string fallbackLocation,
        string userMessage)
    {
        if (string.IsNullOrWhiteSpace(timezoneJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(timezoneJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(err.GetString()))
            {
                return null;
            }

            var timezone = root.TryGetProperty("timezone", out var tzEl)
                ? (tzEl.GetString() ?? "")
                : "";
            if (string.IsNullOrWhiteSpace(timezone))
                return null;

            var location = fallbackLocation;
            var fromMessage = ExtractLocationFromWeatherMessage(userMessage);
            if (!string.IsNullOrWhiteSpace(fromMessage))
                location = fromMessage!;

            if (TryResolveTimeZoneInfo(timezone, out var tzInfo))
            {
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tzInfo);
                var formatted = local.ToString("h:mm tt on dddd, MMM d");
                return $"It's currently **{formatted}** in {location} ({timezone}).\n\nNeed another city checked too?";
            }

            return $"The timezone for {location} is **{timezone}**.\n\nWant local time there as well?";
        }
        catch
        {
            return null;
        }
    }

    private void RememberUtilityContext(UtilityRouter.UtilityResult utilityResult)
    {
        if (utilityResult is null || string.IsNullOrWhiteSpace(utilityResult.ContextKey))
            return;

        _lastUtilityContextKey = utilityResult.ContextKey.Trim();
        _lastUtilityContextAt = _timeProvider.GetUtcNow();

        var state = _dialogueStore.Get();
        _dialogueStore.Update(state with
        {
            Topic = utilityResult.ContextKey.Trim()
        });
    }

    private bool TryGetActiveUtilityContext(out string contextKey)
    {
        contextKey = "";
        if (string.IsNullOrWhiteSpace(_lastUtilityContextKey))
            return false;

        var now = _timeProvider.GetUtcNow();
        if ((now - _lastUtilityContextAt) > UtilityContextTtl)
            return false;

        contextKey = _lastUtilityContextKey!;
        return true;
    }

    private UtilityRouter.UtilityResult? TryHandleUtilityFollowUpWithContext(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return null;

        if (!TryGetActiveUtilityContext(out var contextKey))
            return null;

        var lower = userMessage.Trim().ToLowerInvariant();
        if (contextKey.Equals("moon_distance", StringComparison.OrdinalIgnoreCase) &&
            TryResolveMoonFollowUpUnit(lower, out var requestedUnit))
        {
            return BuildMoonUnitFollowUpResult(requestedUnit);
        }

        if (contextKey.Equals("moon_distance", StringComparison.OrdinalIgnoreCase) &&
            LooksLikePrecisionFollowUp(lower))
        {
            return BuildMoonPrecisionFollowUpResult(lower);
        }

        return null;
    }

    private static bool LooksLikePrecisionFollowUp(string lowerMessage)
    {
        if (string.IsNullOrWhiteSpace(lowerMessage))
            return false;

        return lowerMessage.Contains("more precise", StringComparison.Ordinal) ||
               lowerMessage.Contains("precise figure", StringComparison.Ordinal) ||
               lowerMessage.Contains("more exact", StringComparison.Ordinal) ||
               lowerMessage.Contains("exact figure", StringComparison.Ordinal) ||
               lowerMessage.Contains("exact value", StringComparison.Ordinal) ||
               lowerMessage.Contains("higher precision", StringComparison.Ordinal) ||
               lowerMessage.Contains("more accurate", StringComparison.Ordinal) ||
               lowerMessage.Contains("to the decimal", StringComparison.Ordinal) ||
               lowerMessage.Contains("more digits", StringComparison.Ordinal) ||
               lowerMessage.Contains("significant digit", StringComparison.Ordinal) ||
               lowerMessage.Contains("more detail", StringComparison.Ordinal) ||
               lowerMessage.Equals("i need a more precise figure!", StringComparison.Ordinal) ||
               lowerMessage.Equals("more precise", StringComparison.Ordinal) ||
               lowerMessage.Equals("exactly", StringComparison.Ordinal);
    }

    private static bool TryResolveMoonFollowUpUnit(string lowerMessage, out string unit)
    {
        unit = "";
        if (string.IsNullOrWhiteSpace(lowerMessage))
            return false;

        var tokens = lowerMessage
            .Split(
                [' ', '\t', '\r', '\n', '?', '!', ',', '.', ';', ':', '(', ')', '/', '\\', '-'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        static bool ContainsAnyToken(string[] source, params string[] candidates)
        {
            foreach (var token in source)
            {
                for (var i = 0; i < candidates.Length; i++)
                {
                    if (token.Equals(candidates[i], StringComparison.Ordinal))
                        return true;
                }
            }

            return false;
        }

        // Use token matches (not substring matches) so words like
        // "velocity" don't accidentally trip the "it" referential check.
        var referencesPreviousValue = ContainsAnyToken(tokens, "that", "it", "distance", "moon");
        if (!referencesPreviousValue)
            return false;

        if (ContainsAnyToken(tokens, "feet", "foot", "ft"))
        {
            unit = "feet";
            return true;
        }

        if (ContainsAnyToken(tokens, "mile", "miles", "mi"))
        {
            unit = "miles";
            return true;
        }

        if (ContainsAnyToken(tokens, "kilometer", "kilometers", "km"))
        {
            unit = "kilometers";
            return true;
        }

        if (ContainsAnyToken(tokens, "meter", "meters", "m"))
        {
            unit = "meters";
            return true;
        }

        return false;
    }

    private static UtilityRouter.UtilityResult BuildMoonPrecisionFollowUpResult(string lowerMessage)
    {
        const double averageKm = 384_400.0;
        const double perigeeKm = 363_300.0;
        const double apogeeKm = 405_500.0;
        const double kmToMiles = 0.621371;

        var averageMiles = averageKm * kmToMiles;
        var perigeeMiles = perigeeKm * kmToMiles;
        var apogeeMiles = apogeeKm * kmToMiles;

        string answer;
        if (lowerMessage.Contains("mile", StringComparison.Ordinal))
        {
            answer =
                $"More precise numbers: average Earth-Moon distance is **{averageMiles:N1} miles**. " +
                $"Because the orbit is elliptical, it ranges from about **{perigeeMiles:N0} miles** " +
                $"(perigee) to **{apogeeMiles:N0} miles** (apogee).";
        }
        else if (lowerMessage.Contains("km", StringComparison.Ordinal) ||
                 lowerMessage.Contains("kilometer", StringComparison.Ordinal))
        {
            answer =
                $"More precise numbers: average Earth-Moon distance is **{averageKm:N1} km**. " +
                $"It ranges from about **{perigeeKm:N0} km** (perigee) to **{apogeeKm:N0} km** (apogee).";
        }
        else
        {
            answer =
                $"More precise numbers: average Earth-Moon distance is **{averageKm:N1} km** " +
                $"(**{averageMiles:N1} miles**). The orbit varies between about **{perigeeKm:N0} km** " +
                $"(**{perigeeMiles:N0} miles**) and **{apogeeKm:N0} km** (**{apogeeMiles:N0} miles**).";
        }

        return new UtilityRouter.UtilityResult
        {
            Category = "fact",
            Answer = answer,
            ContextKey = "moon_distance"
        };
    }

    private static UtilityRouter.UtilityResult BuildMoonUnitFollowUpResult(string unit)
    {
        const double averageKm = 384_400.0;
        const double kmToMiles = 0.621371;
        const double milesToFeet = 5_280.0;

        var averageMilesRounded = Math.Round(averageKm * kmToMiles);
        var averageFeetFromMiles = averageMilesRounded * milesToFeet;
        var averageMeters = averageKm * 1_000.0;

        var normalizedUnit = unit.Trim().ToLowerInvariant();
        var answer = normalizedUnit switch
        {
            "feet" =>
                $"That is about **{averageFeetFromMiles:N0} feet** " +
                $"(using **{averageMilesRounded:N0} miles * 5,280 ft/mile**, average Earth-Moon distance).",
            "meters" =>
                $"That is about **{averageMeters:N0} meters** (average Earth-Moon distance).",
            "miles" =>
                $"That is about **{averageMilesRounded:N0} miles** (average Earth-Moon distance).",
            _ =>
                $"That is about **{averageKm:N0} kilometers** (average Earth-Moon distance)."
        };

        return new UtilityRouter.UtilityResult
        {
            Category = "fact",
            Answer = answer,
            ContextKey = "moon_distance"
        };
    }

    private static bool TryResolveTimeZoneInfo(string timezoneId, out TimeZoneInfo tzInfo)
    {
        try
        {
            tzInfo = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
            return true;
        }
        catch
        {
            // Windows often needs a Windows timezone ID; convert if IANA provided.
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timezoneId, out var windowsId))
            {
                try
                {
                    tzInfo = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                    return true;
                }
                catch
                {
                    // Fall through.
                }
            }
        }

        tzInfo = TimeZoneInfo.Utc;
        return false;
    }

    private static string BuildHolidayUtilityResponse(string toolName, string toolJson)
    {
        if (string.IsNullOrWhiteSpace(toolJson))
            return "I couldn't get holiday data from that tool call.";

        try
        {
            using var doc = JsonDocument.Parse(toolJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(err.GetString()))
            {
                return $"Holiday lookup failed: {err.GetString()}";
            }

            var country = root.TryGetProperty("countryCode", out var ccEl)
                ? (ccEl.GetString() ?? "that country")
                : "that country";
            var region = root.TryGetProperty("regionCode", out var rcEl)
                ? (rcEl.GetString() ?? "")
                : "";
            var scope = string.IsNullOrWhiteSpace(region) ? country : region;

            if (toolName.Equals(HolidaysIsTodayToolName, StringComparison.OrdinalIgnoreCase))
            {
                var isTodayHoliday = root.TryGetProperty("isPublicHoliday", out var isEl) &&
                                     isEl.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                     isEl.GetBoolean();

                var todayNames = new List<string>();
                if (root.TryGetProperty("holidaysToday", out var todayArr) &&
                    todayArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in todayArr.EnumerateArray())
                    {
                        if (item.TryGetProperty("name", out var nameEl) &&
                            nameEl.ValueKind == JsonValueKind.String)
                        {
                            var name = (nameEl.GetString() ?? "").Trim();
                            if (!string.IsNullOrWhiteSpace(name))
                                todayNames.Add(name);
                        }
                    }
                }

                string firstLine;
                if (isTodayHoliday)
                {
                    var names = todayNames.Count > 0
                        ? string.Join(", ", todayNames.Distinct(StringComparer.OrdinalIgnoreCase))
                        : "a listed public holiday";
                    firstLine = $"Yes — today is a public holiday in **{scope}**: **{names}**.";
                }
                else
                {
                    firstLine = $"No — today is not a public holiday in **{scope}**.";
                }

                if (root.TryGetProperty("nextHoliday", out var nextHoliday) &&
                    nextHoliday.ValueKind == JsonValueKind.Object)
                {
                    var nextName = nextHoliday.TryGetProperty("name", out var nn) ? (nn.GetString() ?? "") : "";
                    var nextDate = nextHoliday.TryGetProperty("date", out var nd) ? (nd.GetString() ?? "") : "";
                    if (!string.IsNullOrWhiteSpace(nextName) && !string.IsNullOrWhiteSpace(nextDate))
                    {
                        firstLine += $" Next up: **{nextName}** on **{nextDate}**.";
                    }
                }

                return $"{firstLine}\n\nWant the full holiday calendar for the year?";
            }

            if (toolName.Equals(HolidaysNextToolName, StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("holidays", out var holidays) &&
                    holidays.ValueKind == JsonValueKind.Array &&
                    holidays.GetArrayLength() > 0)
                {
                    var first = holidays.EnumerateArray().First();
                    var name = first.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "the next holiday") : "the next holiday";
                    var date = first.TryGetProperty("date", out var dateEl) ? (dateEl.GetString() ?? "an upcoming date") : "an upcoming date";
                    return $"The next public holiday in **{scope}** is **{name}** on **{date}**.\n\nWant the next few after that?";
                }

                return $"I couldn't find upcoming public holidays for **{scope}**.";
            }

            // holidays_get
            var year = root.TryGetProperty("year", out var yEl) && yEl.TryGetInt32(out var y)
                ? y
                : DateTime.UtcNow.Year;
            var entries = new List<string>();
            var count = 0;
            if (root.TryGetProperty("holidays", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                count = arr.GetArrayLength();
                foreach (var item in arr.EnumerateArray().Take(4))
                {
                    var name = item.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "";
                    var date = item.TryGetProperty("date", out var dEl) ? (dEl.GetString() ?? "") : "";
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(date))
                        entries.Add($"{name} ({date})");
                }
            }

            if (count == 0)
                return $"I couldn't find public holidays for **{scope}** in **{year}**.";

            var preview = entries.Count > 0 ? string.Join(", ", entries) : "no preview available";
            return $"I found **{count}** public holidays in **{scope}** for **{year}**. First entries: {preview}.\n\nWant this narrowed to a specific region?";
        }
        catch
        {
            return "I fetched holiday data, but couldn't parse a clean answer.";
        }
    }

    private static string BuildFeedUtilityResponse(string toolJson)
    {
        if (string.IsNullOrWhiteSpace(toolJson))
            return "I couldn't read any feed data from that request.";

        try
        {
            using var doc = JsonDocument.Parse(toolJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(err.GetString()))
            {
                return $"Feed fetch failed: {err.GetString()}";
            }

            var title = root.TryGetProperty("feedTitle", out var titleEl) ? (titleEl.GetString() ?? "") : "";
            var host = root.TryGetProperty("sourceHost", out var hostEl) ? (hostEl.GetString() ?? "") : "";
            var label = !string.IsNullOrWhiteSpace(title) ? title : host;

            var items = new List<string>();
            var count = 0;
            if (root.TryGetProperty("items", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                count = arr.GetArrayLength();
                foreach (var item in arr.EnumerateArray().Take(3))
                {
                    if (item.TryGetProperty("title", out var itemTitleEl) &&
                        itemTitleEl.ValueKind == JsonValueKind.String)
                    {
                        var itemTitle = (itemTitleEl.GetString() ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(itemTitle))
                            items.Add(itemTitle);
                    }
                }
            }

            if (count == 0)
            {
                return $"I reached **{label}**, but there were no recent feed items to show.\n\nWant a retry or a different feed URL?";
            }

            var headlineList = items.Count > 0
                ? string.Join("; ", items.Select((t, i) => $"{i + 1}) {t}"))
                : "recent items were returned";

            return $"I fetched **{count}** recent feed item(s) from **{label}**. Latest: {headlineList}\n\nPick one and I'll summarize it.";
        }
        catch
        {
            return "I fetched feed data, but couldn't parse it into a clean summary.";
        }
    }

    private static string BuildStatusUtilityResponse(string toolJson)
    {
        if (string.IsNullOrWhiteSpace(toolJson))
            return "I couldn't get a status payload from that check.";

        try
        {
            using var doc = JsonDocument.Parse(toolJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(err.GetString()))
            {
                return $"Status check failed: {err.GetString()}";
            }

            var url = root.TryGetProperty("url", out var urlEl) ? (urlEl.GetString() ?? "") : "";
            var reachable = root.TryGetProperty("reachable", out var reachEl) &&
                            reachEl.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                            reachEl.GetBoolean();
            var code = root.TryGetProperty("httpStatus", out var codeEl) && codeEl.TryGetInt32(out var status)
                ? status
                : (int?)null;
            var method = root.TryGetProperty("method", out var methodEl) ? (methodEl.GetString() ?? "probe") : "probe";
            var latency = root.TryGetProperty("latencyMs", out var latencyEl) && latencyEl.TryGetInt32(out var ms)
                ? ms
                : 0;
            var error = root.TryGetProperty("error", out var errEl) ? (errEl.GetString() ?? "") : "";

            var hostLabel = url;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                hostLabel = uri.Host;

            if (reachable)
            {
                var statusText = code.HasValue ? $"HTTP {code.Value}" : "a network response";
                return $"**{hostLabel}** is reachable ({statusText} via {method} in {latency} ms).\n\nNeed a quick re-check in a few seconds?";
            }

            var reason = string.IsNullOrWhiteSpace(error) ? "no response" : error;
            return $"I couldn't reach **{hostLabel}** ({reason}).\n\nWant a retry or a different URL variant?";
        }
        catch
        {
            return "I ran the status check, but couldn't parse the response cleanly.";
        }
    }

    private static string? ExtractLocationFromWeatherMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var match = WeatherLocationRegex().Match(message);
        if (!match.Success)
            return null;

        var location = match.Groups["location"].Value
            .Trim()
            .TrimEnd('?', '.', '!', ',');

        return string.IsNullOrWhiteSpace(location) ? null : location;
    }

    private void RememberPlaceContext(
        string placeName,
        string countryCode,
        string? regionCode = null,
        double? latitude = null,
        double? longitude = null,
        bool locationInferred = false,
        bool geocodeMismatch = false,
        bool explicitLocationChange = true)
    {
        if (string.IsNullOrWhiteSpace(placeName))
            return;

        var normalizedName = placeName.Trim();
        var normalizedCountryCode = string.IsNullOrWhiteSpace(countryCode)
            ? ""
            : countryCode.Trim().ToUpperInvariant();
        var normalizedRegionCode = string.IsNullOrWhiteSpace(regionCode)
            ? null
            : regionCode.Trim().ToUpperInvariant();

        var now = _timeProvider.GetUtcNow();

        var current = _dialogueStore.Get();
        if (!current.ContextLocked || explicitLocationChange)
        {
            _dialogueStore.Update(current with
            {
                Topic = string.IsNullOrWhiteSpace(current.Topic) ? "location" : current.Topic,
                LocationName = normalizedName,
                CountryCode = string.IsNullOrWhiteSpace(normalizedCountryCode) ? null : normalizedCountryCode,
                RegionCode = normalizedRegionCode,
                Latitude = latitude ?? current.Latitude,
                Longitude = longitude ?? current.Longitude,
                LocationInferred = locationInferred,
                GeocodeMismatch = geocodeMismatch
            });
        }

        _lastPlaceContextName = normalizedName;
        _lastPlaceContextCountryCode = normalizedCountryCode;
        _lastPlaceContextAt = now;

        // Also mirror into the search session so entity-aware query building
        // can reuse place context on short follow-up turns.
        _searchOrchestrator.Session.LastEntityCanonical = normalizedName;
        _searchOrchestrator.Session.LastEntityType = "Place";
        _searchOrchestrator.Session.LastEntityDisambiguation =
            string.IsNullOrWhiteSpace(normalizedCountryCode) ? "Place" : normalizedCountryCode;
        _searchOrchestrator.Session.UpdatedAt = now;
    }

    private bool TryGetActivePlaceContext(out string placeName)
    {
        placeName = "";
        var state = _dialogueStore.Get();
        if (!string.IsNullOrWhiteSpace(state.LocationName))
        {
            placeName = state.LocationName!;
            return true;
        }

        if (string.IsNullOrWhiteSpace(_lastPlaceContextName))
            return false;

        var now = _timeProvider.GetUtcNow();
        if ((now - _lastPlaceContextAt) > PlaceContextTtl)
            return false;

        placeName = _lastPlaceContextName!;
        return true;
    }

    private string ApplyPlaceContextIfHelpful(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return userMessage;

        if (!TryGetActivePlaceContext(out var place))
            return userMessage;

        var trimmed = userMessage.Trim();
        var lower = trimmed.ToLowerInvariant();

        // Do not inject when the user already scoped to a topic/location.
        if (HasExplicitNonTemporalScope(lower))
            return userMessage;

        var weatherFollowUp = LooksLikeWeatherFollowUp(lower);
        var genericNewsFollowUp = SearchModeRouter.LooksLikeNewsIntent(lower);
        if (!weatherFollowUp && !genericNewsFollowUp)
            return userMessage;

        if (weatherFollowUp)
        {
            if (LooksLikeWeatherActivityAdviceRequest(lower))
                return $"{trimmed.TrimEnd('?', '.', '!')} in {place}";

            return $"weather in {place}";
        }

        return $"{trimmed.TrimEnd('?', '.', '!')} in {place}";
    }

    private static bool LooksLikeWeatherFollowUp(string lowerMessage)
    {
        if (string.IsNullOrWhiteSpace(lowerMessage))
            return false;

        // "forecast" alone can be non-weather ("stock forecast"), so guard.
        if (lowerMessage.Contains("stock forecast", StringComparison.Ordinal) ||
            lowerMessage.Contains("earnings forecast", StringComparison.Ordinal))
            return false;

        return lowerMessage.Contains("weather", StringComparison.Ordinal) ||
               lowerMessage.Contains("forecast", StringComparison.Ordinal) ||
               lowerMessage.Contains("temperature", StringComparison.Ordinal) ||
               lowerMessage.Contains("temp", StringComparison.Ordinal) ||
               lowerMessage.Contains("humidity", StringComparison.Ordinal) ||
               lowerMessage.Contains("rain", StringComparison.Ordinal) ||
               lowerMessage.Contains("snow", StringComparison.Ordinal);
    }

    private static bool LooksLikeWeatherActivityAdviceRequest(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var lowerMessage = message.ToLowerInvariant();

        var hasWeatherCue =
            lowerMessage.Contains("weather", StringComparison.Ordinal) ||
            lowerMessage.Contains("forecast", StringComparison.Ordinal) ||
            lowerMessage.Contains("temperature", StringComparison.Ordinal) ||
            lowerMessage.Contains("temp", StringComparison.Ordinal) ||
            lowerMessage.Contains("rain", StringComparison.Ordinal) ||
            lowerMessage.Contains("snow", StringComparison.Ordinal);

        if (!hasWeatherCue)
            return false;

        return lowerMessage.Contains("activity", StringComparison.Ordinal) ||
               lowerMessage.Contains("activities", StringComparison.Ordinal) ||
               lowerMessage.Contains("what can i do", StringComparison.Ordinal) ||
               lowerMessage.Contains("could i do", StringComparison.Ordinal) ||
               lowerMessage.Contains("what should i do", StringComparison.Ordinal) ||
               lowerMessage.Contains("kind of things", StringComparison.Ordinal) ||
               lowerMessage.Contains("things to do", StringComparison.Ordinal) ||
               lowerMessage.Contains("ideas", StringComparison.Ordinal) ||
               lowerMessage.Contains("recommend", StringComparison.Ordinal) ||
               lowerMessage.Contains("suggest", StringComparison.Ordinal);
    }

    private static bool HasExplicitNonTemporalScope(string lowerMessage)
    {
        if (string.IsNullOrWhiteSpace(lowerMessage))
            return false;

        var match = ContextScopeRegex().Match(lowerMessage);
        if (!match.Success)
            return false;

        var scope = match.Groups["scope"].Value
            .Trim()
            .TrimEnd('?', '.', '!', ',');

        if (string.IsNullOrWhiteSpace(scope))
            return false;

        var scopeLower = scope.ToLowerInvariant();
        if (scopeLower.Contains("this weather", StringComparison.Ordinal) ||
            scopeLower.Contains("that weather", StringComparison.Ordinal) ||
            scopeLower.Contains("this kind of weather", StringComparison.Ordinal) ||
            scopeLower.Contains("that kind of weather", StringComparison.Ordinal) ||
            scopeLower.Contains("kind of weather", StringComparison.Ordinal) ||
            scopeLower.Contains("current weather", StringComparison.Ordinal) ||
            scopeLower.Contains("these conditions", StringComparison.Ordinal) ||
            scopeLower.Contains("those conditions", StringComparison.Ordinal))
        {
            return false;
        }

        return !TemporalScopeRegex().IsMatch(scope);
    }

    [GeneratedRegex(@"\b(?:in|for|at|near)\s+(?<location>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WeatherLocationRegex();

    [GeneratedRegex(
        @"\b(?:in|for|at|near|about|on|regarding)\s+(?<scope>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ContextScopeRegex();

    [GeneratedRegex(
        @"^(?:for\s+)?(?:today|tomorrow|tonight|now|right now|currently|this\s+(?:morning|afternoon|evening|week|weekend)|last\s+(?:week|month)|next\s+week|yesterday)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TemporalScopeRegex();

    private static string TrimDanglingIncompleteEnding(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var cleaned = text.Trim();
        var lines = new List<string>(cleaned.Split('\n'));
        while (lines.Count > 0)
        {
            var last = lines[^1].Trim();
            if (last.Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
                continue;
            }

            // Token-limited outputs often end in half-built markdown tables.
            if (last.StartsWith("|", StringComparison.Ordinal))
            {
                lines.RemoveAt(lines.Count - 1);
                continue;
            }

            break;
        }

        cleaned = string.Join("\n", lines).Trim();
        if (cleaned.Length == 0)
            return text.Trim();

        var lastChar = cleaned[^1];
        if (lastChar is '.' or '!' or '?' or '"' or '\'' or ')' or ']')
            return cleaned;

        var sentenceEnd = cleaned.LastIndexOfAny(['.', '!', '?']);
        if (sentenceEnd >= 40)
            return cleaned[..(sentenceEnd + 1)].Trim();

        return cleaned.TrimEnd(',', ';', ':', '-', '—').Trim();
    }

    // ─────────────────────────────────────────────────────────────────
    // Web Search Fallback
    //
    // When the chat-only path produces garbage (template tokens, empty
    // response), the user likely asked a follow-up about something the
    // model can't answer from memory alone. Rather than returning a
    // useless "something went sideways" message, we try a web search.
    // This handles the common pattern:
    //   Turn 1: "pull up the news"  → web search → great summary
    //   Turn 2: "whats with X?"     → chat-only  → garbage
    //                               → fallback   → web search for X
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the web search pipeline as a fallback when the chat-only
    /// path fails. Delegates to <see cref="SearchOrchestrator"/> with
    /// error handling so a search failure doesn't crash the turn.
    /// </summary>
    private async Task<AgentResponse> FallbackToWebSearchAsync(
        string userMessage,
        string memoryPackText,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _searchOrchestrator.ExecuteAsync(
                userMessage, memoryPackText, _history, toolCallsMade, cancellationToken);

            if (response.Success)
                _history.Add(ChatMessage.Assistant(response.Text));

            return response;
        }
        catch (Exception ex)
        {
            LogEvent("FALLBACK_SEARCH_FAIL", ex.Message);

            var fallbackMsg = "I wasn't able to generate a clean answer for that. " +
                              "Could you try asking a different way?";
            _history.Add(ChatMessage.Assistant(fallbackMsg));

            return new AgentResponse
            {
                Text          = fallbackMsg,
                Success       = false,
                Error         = ex.Message,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }
    }

    /// <summary>
    /// Builds a readable extractive summary from raw search results
    /// when the LLM can't summarize (regex engine failure, timeout,
    /// etc.). Includes both the source title and the article excerpt
    /// so the user gets actual content, not just homepage headlines.
    ///
    /// Tool output format:
    ///   1. "Title" — source.com
    ///      Excerpt text up to ~1000 chars...
    ///
    /// The excerpts are the real value — article content already
    /// fetched by ContentExtractor. Truncated to ~300 chars each
    /// here to keep the fallback response a reasonable length.
    /// </summary>
    private static string BuildExtractiveSummary(string toolResult, string query)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
            return $"I found some results for \"{query}\" but couldn't generate a summary. " +
                   "The source links should be visible below.";

        // Strip the SOURCES_JSON section (UI-only metadata).
        var jsonIdx = toolResult.IndexOf(
            "<!-- SOURCES_JSON -->", StringComparison.Ordinal);
        var contentPart = jsonIdx > 0 ? toolResult[..jsonIdx] : toolResult;

        // Parse numbered entries with their indented excerpts.
        // Format:
        //   1. "Title" — source
        //      Excerpt paragraph...
        var lines = contentPart.Split('\n');
        var entries = new List<(string Title, string Source, string Excerpt)>();
        string? currentTitle = null;
        string? currentSource = null;
        var excerptBuilder = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Skip instruction lines baked into the tool output.
            if (IsInstructionLine(trimmed)) continue;

            // Numbered entry: "1. "Title" — source"
            if (trimmed.Length > 3 && char.IsDigit(trimmed[0]) &&
                (trimmed[1] == '.' || (char.IsDigit(trimmed[1]) && trimmed[2] == '.')))
            {
                // Save previous entry
                if (currentTitle != null)
                    entries.Add((currentTitle, currentSource ?? "", excerptBuilder.ToString().Trim()));

                excerptBuilder.Clear();

                // Parse: remove number prefix, extract title and source
                var dotIdx = trimmed.IndexOf('.');
                var body = trimmed[(dotIdx + 1)..].Trim();

                var dashIdx = body.IndexOf(" — ", StringComparison.Ordinal);
                if (dashIdx > 0)
                {
                    currentTitle  = body[..dashIdx].Trim().Trim('"');
                    currentSource = body[(dashIdx + 3)..].Trim();
                }
                else
                {
                    currentTitle  = body.Trim('"');
                    currentSource = "";
                }
            }
            else if (currentTitle != null && line.StartsWith("   "))
            {
                // Indented excerpt line — append to current entry
                if (excerptBuilder.Length < 300)
                {
                    if (excerptBuilder.Length > 0) excerptBuilder.Append(' ');
                    excerptBuilder.Append(trimmed);
                }
            }
        }

        // Don't forget the last entry
        if (currentTitle != null)
            entries.Add((currentTitle, currentSource ?? "", excerptBuilder.ToString().Trim()));

        if (entries.Count == 0)
            return $"I found some results for \"{query}\" but couldn't generate a summary. " +
                   "The source links should be visible below.";

        var sb = new StringBuilder();
        sb.AppendLine($"Here's what I found for \"{query}\":");
        sb.AppendLine();

        foreach (var (title, source, excerpt) in entries.Take(5))
        {
            var attribution = string.IsNullOrWhiteSpace(source) ? "" : $" ({source})";
            sb.AppendLine($"**{title}**{attribution}");

            if (!string.IsNullOrWhiteSpace(excerpt))
            {
                // Trim to a clean sentence boundary if possible
                var trimmedExcerpt = TrimToSentence(excerpt, 280);
                sb.AppendLine(trimmedExcerpt);
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Returns true if the line is a prompt instruction baked into
    /// the search tool output (not actual search content).
    /// </summary>
    private static bool IsInstructionLine(string trimmed) =>
        trimmed.StartsWith("Synthesize", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Summarize", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Cross-reference", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Lead with", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("No URLs", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("ONLY state", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("If a detail", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Trims text to approximately <paramref name="maxChars"/> at a
    /// sentence boundary (period, question mark, exclamation mark).
    /// Falls back to a word boundary if no sentence end is found.
    /// </summary>
    private static string TrimToSentence(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;

        // Look for the last sentence-ending punctuation before maxChars
        var window = text[..maxChars];
        var lastEnd = Math.Max(
            Math.Max(window.LastIndexOf(". "), window.LastIndexOf("? ")),
            window.LastIndexOf("! "));

        if (lastEnd > maxChars / 2)
            return text[..(lastEnd + 1)];

        // No good sentence boundary — break at a word boundary
        var lastSpace = window.LastIndexOf(' ');
        return lastSpace > maxChars / 2
            ? text[..lastSpace] + "..."
            : text[..maxChars] + "...";
    }

    // ─────────────────────────────────────────────────────────────────
    // Follow-Up Enrichment
    //
    // When the user asks to go deeper on a topic from a previous search,
    // fetch full article content from the URLs we already know about.
    // This avoids the shallow-search-again pattern and lets the LLM
    // cross-reference sources with actual article text, not snippets.
    // ─────────────────────────────────────────────────────────────────

    private const string SourcesJsonDelimiter  = "<!-- SOURCES_JSON -->";
    private const string BrowseToolName        = "browser_navigate";
    private const string BrowseToolNameAlt     = "BrowserNavigate";
    private const int    MaxFollowUpUrls       = 2;
    private const int    MaxArticleChars       = 3000;

    /// <summary>
    /// Extracts source URLs and titles from a web search tool result
    /// that contains a <c>&lt;!-- SOURCES_JSON --&gt;</c> section.
    /// Returns an empty list if the delimiter is missing or the JSON
    /// is malformed.
    /// </summary>
    private static List<(string Url, string Title)> ParseSourceUrls(string toolResult)
    {
        var sources = new List<(string Url, string Title)>();
        if (string.IsNullOrWhiteSpace(toolResult))
            return sources;

        var delimIdx = toolResult.IndexOf(
            SourcesJsonDelimiter, StringComparison.Ordinal);
        if (delimIdx < 0)
            return sources;

        var jsonPart = toolResult[(delimIdx + SourcesJsonDelimiter.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(jsonPart))
            return sources;

        try
        {
            using var doc = JsonDocument.Parse(jsonPart);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return sources;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var url   = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                var title = item.TryGetProperty("title", out var t) ? t.GetString() : "";
                if (!string.IsNullOrWhiteSpace(url))
                    sources.Add((url!, title ?? ""));
            }
        }
        catch
        {
            // Malformed JSON — not worth crashing over. Return what we have.
        }

        return sources;
    }

    private static string StripSourcesJsonSection(string toolResult)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
            return "";

        var idx = toolResult.IndexOf(SourcesJsonDelimiter, StringComparison.Ordinal);
        return idx >= 0
            ? toolResult[..idx].TrimEnd()
            : toolResult.TrimEnd();
    }

    private static bool LooksLikeFollowUpDepthRequest(string userMessage)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var asksForMore =
            lower.Contains("tell me more") ||
            lower.Contains("more info") ||
            lower.Contains("more information") ||
            lower.Contains("more detail") ||
            lower.Contains("more details") ||
            lower.Contains("more about") ||
            lower.Contains("more on") ||
            lower.Contains("go deeper") ||
            lower.Contains("dig into") ||
            lower.Contains("elaborate") ||
            lower.Contains("expand on") ||
            lower.StartsWith("more ");

        if (!asksForMore)
            return false;

        // Prefer strong follow-up signals so we don't hijack legitimate
        // standalone searches like "more efficient sorting algorithms".
        var pointsAtPriorContext =
            lower.Contains("this ") ||
            lower.Contains("that ") ||
            lower.Contains("it ") ||
            lower.Contains("these ") ||
            lower.Contains("those ");

        return pointsAtPriorContext || lower.Contains("tell me more") || lower.StartsWith("more ");
    }

    private static IReadOnlyList<string> ExtractFollowUpKeywords(string text)
    {
        var normalized = NormalizeQueryText(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(Math.Min(tokens.Length, 8));

        foreach (var t in tokens)
        {
            var lower = t.ToLowerInvariant();
            if (IsBannedSearchToken(lower))
                continue;

            // Follow-up boilerplate (keep topical nouns, drop meta)
            if (lower is
                "more" or "info" or "information" or "detail" or "details" or
                "news" or "headline" or "headlines" or
                "story" or "article" or "source" or "sources" or
                "today" or "week" or "month" or "year" or
                "latest" or "recent" or "recently" or "breaking")
                continue;

            kept.Add(lower);
            if (kept.Count >= 6)
                break;
        }

        return kept;
    }

    private static List<(string Url, string Title)> PickRelevantSources(
        string userMessage,
        IReadOnlyList<(string Url, string Title)> sources,
        int maxUrls)
    {
        if (sources.Count == 0)
            return [];

        var keywords = ExtractFollowUpKeywords(userMessage);
        if (keywords.Count == 0)
            return [];

        int Score(string title)
        {
            var tl = (title ?? "").ToLowerInvariant();
            var score = 0;
            foreach (var k in keywords)
            {
                if (tl.Contains(k, StringComparison.OrdinalIgnoreCase))
                    score++;
            }
            return score;
        }

        return sources
            .Select(s => (Source: s, Score: Score(s.Title)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Source.Title.Length) // shorter titles tend to be cleaner queries
            .Take(Math.Max(1, maxUrls))
            .Select(x => x.Source)
            .ToList();
    }

    private async Task<string> TryCallWebSearchAsync(
        string query,
        string recency,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken cancellationToken)
    {
        var args = JsonSerializer.Serialize(new
        {
            query,
            maxResults = DefaultWebSearchMaxResults,
            recency
        });

        var toolName = WebSearchToolName;
        var toolOk = false;
        string toolResult;

        try
        {
            LogEvent("AGENT_TOOL_CALL", $"{toolName}({args})");
            toolResult = await _mcp.CallToolAsync(toolName, args, cancellationToken);
            toolOk = true;
        }
        catch (Exception ex)
        {
            // Back-compat: some MCP stacks register PascalCase tool names.
            try
            {
                toolName = WebSearchToolNameAlt;
                LogEvent("AGENT_TOOL_CALL", $"{toolName}({args})");
                toolResult = await _mcp.CallToolAsync(toolName, args, cancellationToken);
                toolOk = true;
            }
            catch
            {
                toolResult = $"Tool error: {ex.Message}";
            }
        }

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName  = toolName,
            Arguments = args,
            Result    = toolResult,
            Success   = toolOk
        });
        LogEvent("AGENT_TOOL_RESULT", $"{toolName} -> {(toolOk ? "ok" : "error")}");

        return toolResult;
    }

    private async Task<AgentResponse?> TryAnswerFollowUpFromLastSourcesAsync(
        string userMessage,
        string memoryPackText,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        if (!LooksLikeFollowUpDepthRequest(userMessage))
            return null;
        if (_searchOrchestrator.Session.LastResults.Count == 0)
            return null;

        var sourcesToFetch = PickRelevantSources(userMessage, _searchOrchestrator.Session.LastResults.Select(r => (r.Url, r.Title)).ToList(), MaxFollowUpUrls);
        if (sourcesToFetch.Count == 0)
            return null;

        LogEvent("AGENT_FOLLOWUP_START",
            $"Fetching {sourcesToFetch.Count} prior source(s) for follow-up");

        var fullText = await FetchArticleContentAsync(
            sourcesToFetch, toolCallsMade, cancellationToken);
        if (string.IsNullOrWhiteSpace(fullText))
            return null;

        // ── Related coverage search ───────────────────────────────────
        // After pulling the primary article(s), do a targeted search by
        // story title to find additional coverage. This helps answer
        // follow-up questions when one source is thin or paywalled.
        var relatedQuery = (sourcesToFetch[0].Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(relatedQuery))
            relatedQuery = TryParseFirstBrowserNavigateTitle(fullText) ?? "";

        relatedQuery = relatedQuery.Trim().Trim('"');
        var relatedRecency = (_searchOrchestrator.Session.LastRecency ?? "any") != "any"
            ? _searchOrchestrator.Session.LastRecency!
            : DetectRecencyFallback(userMessage);

        string? relatedToolResult = null;
        if (!string.IsNullOrWhiteSpace(relatedQuery) && relatedQuery.Length >= 8)
        {
            relatedToolResult = await TryCallWebSearchAsync(
                relatedQuery, relatedRecency, toolCallsMade, cancellationToken);

            if (!string.IsNullOrWhiteSpace(relatedToolResult))
            {
                /* Sources now tracked in SearchOrchestrator.Session */
                _searchOrchestrator.Session.LastRecency = relatedRecency;
            }
        }

        roundTrips++;
        var summaryInput = "[Primary article content — reference only, do not display to user]\n" +
                           fullText;

        if (!string.IsNullOrWhiteSpace(relatedToolResult))
        {
            summaryInput += "\n\n[Related coverage search results — reference only, do not display to user]\n" +
                            StripSourcesJsonSection(relatedToolResult);
        }

        var instruction = !string.IsNullOrWhiteSpace(relatedToolResult)
            ? WebFollowUpWithRelatedInstruction
            : WebFollowUpInstruction;

        var messagesForSummary = InjectModeIntoSystemPrompt(
            _history, memoryPackText + instruction);
        messagesForSummary.Add(ChatMessage.User(summaryInput));

        var response = await CallLlmWithRetrySafe(
            messagesForSummary, roundTrips, MaxTokensWebSummary, cancellationToken);

        string text;
        if (response.FinishReason == "error")
        {
            LogEvent("AGENT_SUMMARY_FOLLOWUP_FALLBACK",
                "LLM summary failed — building extractive fallback");
            text = BuildExtractiveSummaryFromContent(fullText);
        }
        else
        {
            text = StripThinkingScaffold(response.Content ?? "[No response]");
            text = TruncateSelfDialogue(text);

            // Raw dump → rewrite
            if (LooksLikeRawDump(text))
            {
                LogEvent("AGENT_REWRITE", "Follow-up response looked like a raw dump — rewriting");
                var rewriteMessages = new List<ChatMessage>
                {
                    ChatMessage.System(
                        _systemPrompt + " " +
                        "Rewrite the draft into the final answer. " +
                        "Casual tone. Bottom line first. 2-3 short paragraphs. " +
                        "No markdown tables. No URLs. No copied excerpts. " +
                        "Do NOT add facts not present in the draft."),
                    ChatMessage.User(text)
                };

                roundTrips++;
                var rewritten = await CallLlmWithRetrySafe(
                    rewriteMessages, roundTrips, MaxTokensWebSummary, cancellationToken);
                if (!string.IsNullOrWhiteSpace(rewritten.Content) &&
                    rewritten.FinishReason != "error")
                    text = StripThinkingScaffold(rewritten.Content!);
            }
        }

        text = StripThinkingScaffold(text);
        text = StripRawTemplateTokens(text);
        text = TrimDanglingIncompleteEnding(text);
        if (string.IsNullOrWhiteSpace(text))
            text = "I wasn't able to generate a clean answer for that. " +
                   "Could you try asking a different way?";

        _history.Add(ChatMessage.Assistant(text));
        LogEvent("AGENT_RESPONSE", text);

        return new AgentResponse
        {
            Text          = text,
            Success       = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<string?> FetchArticleContentAsync(
        IReadOnlyList<(string Url, string Title)> sourcesToFetch,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken cancellationToken)
    {
        if (sourcesToFetch.Count == 0)
            return null;

        // Fetch articles in parallel via MCP browser_navigate / BrowserNavigate.
        // Try snake_case first (MCP SDK default), fall back to PascalCase.
        var fetchTasks = sourcesToFetch.Select(async source =>
        {
            var args = JsonSerializer.Serialize(new { url = source.Url });
            string? content = null;
            var resolvedToolName = BrowseToolName;

            try
            {
                LogEvent("AGENT_TOOL_CALL", $"{BrowseToolName}({args})");
                content = await _mcp.CallToolAsync(BrowseToolName, args, cancellationToken);
            }
            catch
            {
                // snake_case not found — try PascalCase variant
                try
                {
                    resolvedToolName = BrowseToolNameAlt;
                    LogEvent("AGENT_TOOL_CALL", $"{BrowseToolNameAlt}({args})");
                    content = await _mcp.CallToolAsync(BrowseToolNameAlt, args, cancellationToken);
                }
                catch (Exception ex)
                {
                    LogEvent("AGENT_FOLLOWUP_FETCH_FAIL",
                        $"browser_navigate failed for {source.Url}: {ex.Message}");

                    toolCallsMade.Add(new ToolCallRecord
                    {
                        ToolName  = resolvedToolName,
                        Arguments = args,
                        Result    = $"Error: {ex.Message}",
                        Success   = false
                    });

                    return (source.Title, Content: (string?)null, Ok: false);
                }
            }

            toolCallsMade.Add(new ToolCallRecord
            {
                ToolName  = resolvedToolName,
                Arguments = args,
                Result    = content!.Length > 200
                    ? content[..200] + "…"
                    : content,
                Success   = true
            });

            // Truncate each article to keep the total context bounded.
            if (content!.Length > MaxArticleChars)
                content = content[..MaxArticleChars] + "\n[…truncated]";

            return (source.Title, Content: content, Ok: true);
        });

        var results = await Task.WhenAll(fetchTasks);

        var sb = new StringBuilder();
        foreach (var (title, content, ok) in results)
        {
            if (!ok || string.IsNullOrWhiteSpace(content))
                continue;

            // If BrowserNavigate returned a thin wrapper page (common with
            // Google News / RSS redirects), don't pretend we have "full
            // article content" — let the caller fall back to re-searching.
            if (IsLowSignalBrowserNavigateContent(content))
                continue;

            sb.AppendLine($"=== {title} ===");
            sb.AppendLine(content);
            sb.AppendLine();
        }

        var combined = sb.ToString().TrimEnd();
        if (string.IsNullOrWhiteSpace(combined))
            return null;

        LogEvent("AGENT_FOLLOWUP_FETCH_DONE",
            $"Fetched {results.Count(r => r.Ok)} article(s), {combined.Length} chars total");

        return combined;
    }

    private static bool IsLowSignalBrowserNavigateContent(string? content)
    {
        var lower = (content ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return true;

        // If the tool explicitly says it's a basic non-article extraction,
        // require a meaningful word count to treat it as usable.
        var isBasic = lower.Contains("extraction: basic (non-article page)");
        var wc = TryParseBrowserNavigateWordCount(content) ?? 0;

        if (isBasic && wc < 120)
            return true;

        // Google News wrapper pages are usually tiny and useless.
        if (lower.Contains("source: news.google.com") && wc < 300)
            return true;

        return false;
    }

    private static string? TryParseFirstBrowserNavigateTitle(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
                continue;

            var raw = trimmed["Title:".Length..].Trim();
            raw = raw.Trim();

            // BrowserNavigate formats as: Title: "..."
            if (raw.StartsWith('"') && raw.EndsWith('"') && raw.Length >= 2)
                raw = raw[1..^1].Trim();

            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }

        return null;
    }

    private static int? TryParseBrowserNavigateWordCount(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Word Count:", StringComparison.OrdinalIgnoreCase))
                continue;

            var raw = trimmed["Word Count:".Length..].Trim();
            raw = raw.Replace(",", "");

            if (int.TryParse(raw, out var wc))
                return wc;
        }

        return null;
    }

    private static string BuildExtractiveSummaryFromContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "I fetched the source, but couldn't extract usable content.";

        var lines = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0)
            return "I fetched the source, but couldn't extract usable content.";

        var bottomLine = lines[0];
        var details = string.Join('\n', lines.Skip(1).Take(4));

        return string.IsNullOrWhiteSpace(details)
            ? $"Bottom line:\n{bottomLine}"
            : $"Bottom line:\n{bottomLine}\n\nDetails:\n{details}";
    }

    /// <summary>
    /// Calls the LLM with escalating retry for the "Failed to process regex"
    /// error that LM Studio throws when its grammar engine chokes.
    ///
    /// Strategy:
    ///   1. Try the full message list as-is.
    ///   2. On regex failure, wait briefly and retry the same call.
    ///   3. If that also fails, fall back to a minimal message set
    ///      (system prompt + last user message only) to eliminate
    ///      any message structure the template can't handle.
    /// </summary>
    private Task<LlmResponse> CallLlmWithRetrySafe(
        IReadOnlyList<ChatMessage> messages,
        int roundTrip,
        CancellationToken cancellationToken)
        => CallLlmWithRetrySafe(messages, roundTrip, maxTokens: null, cancellationToken);

    private async Task<LlmResponse> CallLlmWithRetrySafe(
        IReadOnlyList<ChatMessage> messages,
        int roundTrip,
        int? maxTokens,
        CancellationToken cancellationToken)
    {
        LogEvent("AGENT_LLM_CALL", $"Round trip #{roundTrip}" +
            (maxTokens.HasValue ? $" (max_tokens={maxTokens})" : ""));

        Task<LlmResponse> Call(IReadOnlyList<ChatMessage> msgs) =>
            maxTokens.HasValue
                ? _llm.ChatAsync(msgs, tools: null, maxTokens.Value, cancellationToken)
                : _llm.ChatAsync(msgs, tools: null, cancellationToken);

        // ── Attempt 1: full message list ─────────────────────────────
        try
        {
            return await Call(messages);
        }
        catch (HttpRequestException ex) when (IsLmStudioRegexFailure(ex))
        {
            LogEvent("AGENT_LLM_REGEX_RETRY",
                "Regex failure — retrying same call after 500 ms");
        }

        await Task.Delay(500, cancellationToken);

        // ── Attempt 2: same messages, second chance ──────────────────
        try
        {
            return await Call(messages);
        }
        catch (HttpRequestException ex) when (IsLmStudioRegexFailure(ex))
        {
            LogEvent("AGENT_LLM_REGEX_RETRY",
                "Regex failure persisted — falling back to minimal message set");
        }

        // ── Attempt 3: minimal messages (system + last user only) ────
        var minimal = new List<ChatMessage>();
        var sysMsg = messages.FirstOrDefault(m => m.Role == "system");
        var lastUser = messages.LastOrDefault(m => m.Role == "user");

        if (sysMsg is not null) minimal.Add(sysMsg);
        if (lastUser is not null) minimal.Add(lastUser);

        try
        {
            return minimal.Count > 0
                ? await Call(minimal)
                : await Call(messages);
        }
        catch (HttpRequestException ex) when (IsLmStudioRegexFailure(ex))
        {
            // All three attempts failed. Rather than crashing the
            // entire conversation, return a graceful error message
            // so the user can retry or switch models.
            LogEvent("AGENT_LLM_REGEX_EXHAUSTED",
                "All retry attempts failed — LM Studio grammar engine is " +
                "unresponsive for this model. The user should retry or " +
                "check the model configuration.");

            return new LlmResponse
            {
                IsComplete   = true,
                Content      = "I'm having trouble with the language model right now — " +
                               "it keeps rejecting my requests. Try sending your " +
                               "message again, or check if the model needs a reload " +
                               "in LM Studio.",
                FinishReason = "error"
            };
        }
    }

    /// <summary>
    /// Keeps the history within a sliding window so small models don't
    /// lose coherence as the context fills up. The system prompt
    /// (message[0]) is always preserved; older turns are evicted FIFO.
    /// </summary>
    private void TrimHistory()
    {
        // Count non-system messages
        var turnMessages = _history.Count(m => m.Role != "system");
        if (turnMessages <= MaxHistoryTurns)
            return;

        var excess = turnMessages - MaxHistoryTurns;
        var removed = 0;
        for (var i = _history.Count - 1; i >= 0 && removed < excess; i--)
        {
            // Walk backwards through the list but remove from the FRONT
            // (oldest non-system messages). Easier to just rebuild:
        }

        // Rebuild: keep system prompt + last N messages
        var sysPrompt = _history.FirstOrDefault(m => m.Role == "system");
        var recent = _history.Where(m => m.Role != "system")
                             .TakeLast(MaxHistoryTurns)
                             .ToList();

        _history.Clear();
        if (sysPrompt is not null) _history.Add(sysPrompt);
        _history.AddRange(recent);

        LogEvent("AGENT_HISTORY_TRIM",
            $"Trimmed to {_history.Count} messages ({MaxHistoryTurns} turns)");
    }

    /// <inheritdoc />
    public void ResetConversation()
    {
        var preserveLock = _dialogueStore.Get().ContextLocked;
        _history.Clear();
        _history.Add(ChatMessage.System(_systemPrompt));
        _searchOrchestrator.Session.Clear();
        _dialogueStore.Reset();
        if (preserveLock)
            _dialogueStore.Update(_dialogueStore.Get() with { ContextLocked = true });
        _lastPlaceContextName = null;
        _lastPlaceContextCountryCode = null;
        _lastPlaceContextAt = default;
        _lastUtilityContextKey = null;
        _lastUtilityContextAt = default;
        _lastFirstPrinciplesRationale = [];
        _lastFirstPrinciplesAt = default;
        LogEvent("AGENT_RESET", "Conversation history and search session cleared.");
    }

    /// <inheritdoc />
    public void SeedDialogueState(DialogueState state)
    {
        _dialogueStore.Seed(state);
    }

    /// <inheritdoc />
    public DialogueContextSnapshot GetContextSnapshot() =>
        _dialogueStore.Get().ToSnapshot();

    /// <inheritdoc />
    public async Task<int> GetAvailableToolCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var tools = await _mcp.ListToolsAsync(cancellationToken);
            return tools.Count;
        }
        catch
        {
            return 0;
        }
    }

    private void UpdateDialogueStateFromValidatedSlots(ValidatedSlots slots)
    {
        var current = _dialogueStore.Get();

        var locationName = current.LocationName;
        var countryCode = current.CountryCode;
        var regionCode = current.RegionCode;
        if (!current.ContextLocked || slots.ExplicitLocationChange)
        {
            if (!string.IsNullOrWhiteSpace(slots.LocationText))
                locationName = slots.LocationText;
            if (!string.IsNullOrWhiteSpace(slots.CountryCode))
                countryCode = slots.CountryCode;
            if (!string.IsNullOrWhiteSpace(slots.RegionCode))
                regionCode = slots.RegionCode;
        }

        _dialogueStore.Update(current with
        {
            Topic = string.IsNullOrWhiteSpace(slots.Topic) ? current.Topic : slots.Topic!,
            LocationName = locationName,
            CountryCode = countryCode,
            RegionCode = regionCode,
            TimeScope = string.IsNullOrWhiteSpace(slots.TimeScope) ? current.TimeScope : slots.TimeScope,
            LocationInferred = slots.LocationInferred,
            GeocodeMismatch = slots.GeocodeMismatch
        });
    }

    private UtilityRouter.UtilityResult? BuildUtilityResultFromToolPlan(
        ToolPlanDecision plan,
        string normalizedMessage)
    {
        if (plan is null || string.Equals(plan.Category, "none", StringComparison.OrdinalIgnoreCase))
            return null;

        var utility = UtilityRouter.TryHandle(normalizedMessage);
        if (utility is null)
        {
            utility = new UtilityRouter.UtilityResult
            {
                Category = plan.Category,
                Answer = plan.InlineAnswer ?? $"[{plan.Category}]"
            };
        }

        if (!string.IsNullOrWhiteSpace(plan.InlineAnswer))
            utility = utility with { Answer = plan.InlineAnswer };

        if (plan.ToolCalls.Count > 0)
        {
            var first = plan.ToolCalls[0];
            utility = utility with
            {
                McpToolName = first.ToolName,
                McpToolArgs = first.ArgumentsJson
            };
        }

        return utility;
    }

    private async Task<AgentResponse> ExecuteSelfMemorySummaryAsync(
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        var (facts, error) = await LoadSelfScopedFactsAsync(toolCallsMade, cancellationToken);
        if (!string.IsNullOrWhiteSpace(error))
        {
            return new AgentResponse
            {
                Text = $"I couldn't read your stored facts right now ({error}).",
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var text = BuildSelfMemorySummaryText(facts);
        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<AgentResponse> ExecuteSelfNameRecallAsync(
        string userMessage,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        var (name, error) = await TryResolveSelfNameAsync(
            userMessage,
            toolCallsMade,
            cancellationToken);

        var text = !string.IsNullOrWhiteSpace(name)
            ? $"Your name is {name}."
            : !string.IsNullOrWhiteSpace(error)
                ? "I couldn't load your profile right now. Ask again in a moment and I'll retry."
                : "I couldn't find a stored name yet. Tell me what name you want me to use, and I'll remember it.";

        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<(string? Name, string? Error)> TryResolveSelfNameAsync(
        string userMessage,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken cancellationToken)
    {
        var argsObj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["query"] = userMessage
        };
        if (!string.IsNullOrWhiteSpace(ActiveProfileId))
            argsObj["activeProfileId"] = ActiveProfileId;

        var argsJson = JsonSerializer.Serialize(argsObj);
        var retrieveCall = await CallToolWithAliasAsync(
            MemoryRetrieveToolName,
            MemoryRetrieveToolNameAlt,
            argsJson,
            cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = retrieveCall.ToolName,
            Arguments = argsJson,
            Result = retrieveCall.Result,
            Success = retrieveCall.Success
        });

        if (retrieveCall.Success && !string.IsNullOrWhiteSpace(retrieveCall.Result))
        {
            var fromPack = TryExtractNameFromMemoryRetrievePayload(retrieveCall.Result);
            if (!string.IsNullOrWhiteSpace(fromPack))
                return (fromPack, null);
        }

        var (facts, factsError) = await LoadSelfScopedFactsAsync(toolCallsMade, cancellationToken);
        var fromFacts = TryExtractNameFromSelfFacts(facts);
        if (!string.IsNullOrWhiteSpace(fromFacts))
            return (fromFacts, null);

        if (!retrieveCall.Success)
            return (null, "memory retrieve unavailable");

        return (null, factsError);
    }

    private async Task<string> BuildSelfMemoryContextBlockAsync(
        List<ToolCallRecord> toolCallsMade,
        CancellationToken cancellationToken)
    {
        var (facts, error) = await LoadSelfScopedFactsAsync(toolCallsMade, cancellationToken);
        if (!string.IsNullOrWhiteSpace(error))
        {
            LogEvent("SELF_MEMORY_CONTEXT_SKIP", error);
            return "";
        }

        return BuildSelfMemoryContextBlock(facts);
    }

    private async Task<(IReadOnlyList<SelfMemoryFact> Facts, string? Error)> LoadSelfScopedFactsAsync(
        List<ToolCallRecord> toolCallsMade,
        CancellationToken cancellationToken)
    {
        var argsJson = JsonSerializer.Serialize(new
        {
            skip = 0,
            limit = 50
        });

        var listCall = await CallToolWithAliasAsync(
            MemoryListFactsToolName, MemoryListFactsToolNameAlt, argsJson, cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = listCall.ToolName,
            Arguments = argsJson,
            Result = listCall.Result,
            Success = listCall.Success
        });

        if (!listCall.Success)
            return ([], "memory list facts unavailable");

        try
        {
            using var doc = JsonDocument.Parse(listCall.Result);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errEl) &&
                errEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(errEl.GetString()))
            {
                return ([], errEl.GetString());
            }

            var facts = ExtractSelfScopedFacts(root, ActiveProfileId)
                .OrderByDescending(f => f.UpdatedAtUtc)
                .Take(12)
                .ToList();
            return (facts, null);
        }
        catch
        {
            return ([], "could not parse memory facts");
        }
    }

    private sealed record SelfMemoryFact(
        string Subject,
        string Predicate,
        string Object,
        string? ProfileId,
        DateTimeOffset UpdatedAtUtc);

    private static IReadOnlyList<SelfMemoryFact> ExtractSelfScopedFacts(
        JsonElement root,
        string? activeProfileId)
    {
        var output = new List<SelfMemoryFact>();
        if (!root.TryGetProperty("facts", out var factsEl) ||
            factsEl.ValueKind != JsonValueKind.Array)
        {
            return output;
        }

        var hasActiveProfile = !string.IsNullOrWhiteSpace(activeProfileId);
        foreach (var item in factsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var subject = GetString(item, "subject");
            var predicate = GetString(item, "predicate");
            var obj = GetString(item, "object");
            var profileId = GetString(item, "profile_id");

            if (string.IsNullOrWhiteSpace(predicate) || string.IsNullOrWhiteSpace(obj))
                continue;

            var include = hasActiveProfile
                ? string.Equals(profileId, activeProfileId, StringComparison.OrdinalIgnoreCase) ||
                  (string.IsNullOrWhiteSpace(profileId) && IsSelfSubject(subject))
                : IsSelfSubject(subject);

            if (!include)
                continue;

            var updatedAtUtc = DateTimeOffset.MinValue;
            var updatedRaw = GetString(item, "updated_at");
            if (!string.IsNullOrWhiteSpace(updatedRaw) &&
                DateTimeOffset.TryParse(updatedRaw, out var parsedUpdated))
            {
                updatedAtUtc = parsedUpdated;
            }

            output.Add(new SelfMemoryFact(
                subject ?? "user",
                predicate!,
                obj!,
                profileId,
                updatedAtUtc));
        }

        return output;
    }

    private static string BuildSelfMemorySummaryText(IReadOnlyList<SelfMemoryFact> facts)
    {
        if (facts.Count == 0)
        {
            return "I pulled your profile, but I don't yet have many detailed saved facts about you. " +
                   "If you want, tell me your preferences, projects, goals, or routines and I'll keep them handy.";
        }

        var rendered = facts
            .Select(RenderSelfMemoryFact)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (rendered.Count == 0)
        {
            return "I pulled your profile, but there aren't any clear fact entries to summarize yet.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Here's what I currently know about you:");
        foreach (var line in rendered)
            sb.AppendLine($"- {line}");
        sb.AppendLine();
        sb.Append("If you'd like, I can update or forget any of these.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildSelfMemoryContextBlock(IReadOnlyList<SelfMemoryFact> facts)
    {
        if (facts.Count == 0)
            return "";

        var rendered = facts
            .Select(RenderSelfMemoryFact)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        if (rendered.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("[USER_MEMORY_FACTS]");
        foreach (var line in rendered)
            sb.AppendLine($"- {line}");
        sb.AppendLine("[/USER_MEMORY_FACTS]");
        sb.AppendLine("Use these known user facts when the user asks for personalized advice.");
        return sb.ToString().TrimEnd();
    }

    private static string? TryExtractNameFromSelfFacts(IReadOnlyList<SelfMemoryFact> facts)
    {
        foreach (var fact in facts)
        {
            if (string.IsNullOrWhiteSpace(fact.Object))
                continue;

            var predicate = (fact.Predicate ?? "")
                .Replace('_', ' ')
                .Trim()
                .ToLowerInvariant();

            if (predicate is "name" or "name is" or "preferred name" or "first name" or "goes by")
                return fact.Object.Trim();
        }

        return null;
    }

    private static string? TryExtractNameFromMemoryRetrievePayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("packText", out var packTextEl) ||
                packTextEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var packText = packTextEl.GetString() ?? "";
            return TryExtractNameFromPackText(packText);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractNameFromPackText(string packText)
    {
        if (string.IsNullOrWhiteSpace(packText))
            return null;

        var lines = packText
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line["Name:".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        var marker = "You know this user as \"";
        var markerIndex = packText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;

        var start = markerIndex + marker.Length;
        var end = packText.IndexOf('"', start);
        if (end <= start)
            return null;

        var extracted = packText[start..end].Trim();
        return string.IsNullOrWhiteSpace(extracted) ? null : extracted;
    }

    private static string RenderSelfMemoryFact(SelfMemoryFact fact)
    {
        var subject = (fact.Subject ?? "user").Trim();
        var predicate = (fact.Predicate ?? "").Trim();
        var obj = (fact.Object ?? "").Trim();

        if (string.IsNullOrWhiteSpace(predicate) || string.IsNullOrWhiteSpace(obj))
            return "";

        var pred = predicate.Replace('_', ' ').Trim();
        var predLower = pred.ToLowerInvariant();

        if (IsSelfSubject(subject))
        {
            return predLower switch
            {
                "name" or "name is" => $"Your name is {obj}.",
                "likes" => $"You like {obj}.",
                "prefers" => $"You prefer {obj}.",
                "works on" => $"You work on {obj}.",
                "is" => $"You are {obj}.",
                _ => $"You {predLower} {obj}."
            };
        }

        return $"{subject} {predLower} {obj}.";
    }

    private static bool IsSelfMemoryKnowledgeRequest(string message)
    {
        var lower = (message ?? "").ToLowerInvariant().Trim();
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var hasAboutMe = lower.Contains("about me", StringComparison.Ordinal) ||
                         lower.Contains("know me", StringComparison.Ordinal) ||
                         lower.Contains("remember me", StringComparison.Ordinal);
        if (!hasAboutMe)
            return false;

        return lower.Contains("what do you know", StringComparison.Ordinal) ||
               lower.Contains("what kind of things", StringComparison.Ordinal) ||
               lower.Contains("what do you remember", StringComparison.Ordinal) ||
               lower.Contains("what have you learned", StringComparison.Ordinal) ||
               lower.Contains("what information", StringComparison.Ordinal) ||
               lower.Contains("what info", StringComparison.Ordinal);
    }

    private static bool IsSelfNameRecallRequest(string message)
    {
        var lower = (message ?? "")
            .ToLowerInvariant()
            .Replace('’', '\'')
            .Trim();

        if (string.IsNullOrWhiteSpace(lower))
            return false;

        return lower.Contains("what is my name", StringComparison.Ordinal) ||
               lower.Contains("what's my name", StringComparison.Ordinal) ||
               lower.Contains("whats my name", StringComparison.Ordinal) ||
               lower.Contains("do you know my name", StringComparison.Ordinal) ||
               lower.Contains("who am i", StringComparison.Ordinal) ||
               lower.Contains("who i am", StringComparison.Ordinal);
    }

    private static bool IsPersonalizedUsingKnownSelfContextRequest(
        string message,
        bool hasLoadedProfileContext)
    {
        var lower = (message ?? "").ToLowerInvariant().Trim();
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var hasAdviceCue =
            lower.Contains("recommend", StringComparison.Ordinal) ||
            lower.Contains("suggest", StringComparison.Ordinal) ||
            lower.Contains("plan", StringComparison.Ordinal) ||
            lower.Contains("best", StringComparison.Ordinal) ||
            lower.Contains("perfect", StringComparison.Ordinal) ||
            lower.Contains("help me choose", StringComparison.Ordinal) ||
            lower.Contains("what should i", StringComparison.Ordinal) ||
            lower.Contains("should i", StringComparison.Ordinal) ||
            lower.Contains("tailor", StringComparison.Ordinal) ||
            lower.Contains("personalized", StringComparison.Ordinal);

        if (!hasAdviceCue)
            return false;

        var referencesKnownSelf =
            lower.Contains("what you know about me", StringComparison.Ordinal) ||
            lower.Contains("based on what you know about me", StringComparison.Ordinal) ||
            lower.Contains("given what you know about me", StringComparison.Ordinal) ||
            lower.Contains("from what you know about me", StringComparison.Ordinal) ||
            lower.Contains("considering what you know about me", StringComparison.Ordinal);

        if (referencesKnownSelf)
            return true;

        if (!hasLoadedProfileContext)
            return false;

        var hasPersonalDomainCue =
            lower.Contains("diet", StringComparison.Ordinal) ||
            lower.Contains("meal", StringComparison.Ordinal) ||
            lower.Contains("nutrition", StringComparison.Ordinal) ||
            lower.Contains("food", StringComparison.Ordinal) ||
            lower.Contains("eat", StringComparison.Ordinal) ||
            lower.Contains("recipe", StringComparison.Ordinal) ||
            lower.Contains("workout", StringComparison.Ordinal) ||
            lower.Contains("exercise", StringComparison.Ordinal) ||
            lower.Contains("training", StringComparison.Ordinal) ||
            lower.Contains("routine", StringComparison.Ordinal) ||
            lower.Contains("habit", StringComparison.Ordinal) ||
            lower.Contains("sleep", StringComparison.Ordinal) ||
            lower.Contains("focus", StringComparison.Ordinal) ||
            lower.Contains("productivity", StringComparison.Ordinal);

        if (!hasPersonalDomainCue)
            return false;

        return lower.Contains("for me", StringComparison.Ordinal) ||
               lower.Contains(" me ", StringComparison.Ordinal) ||
               lower.StartsWith("me ", StringComparison.Ordinal) ||
               lower.EndsWith(" me", StringComparison.Ordinal) ||
               lower.StartsWith("i ", StringComparison.Ordinal) ||
               lower.Contains(" i ", StringComparison.Ordinal) ||
               lower.StartsWith("i'm ", StringComparison.Ordinal) ||
               lower.StartsWith("i'd ", StringComparison.Ordinal) ||
               lower.StartsWith("my ", StringComparison.Ordinal) ||
               lower.Contains(" my ", StringComparison.Ordinal);
    }

    private static bool IsSelfSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return true;

        var token = subject.Trim().ToLowerInvariant();
        return token is "user" or "me" or "i" or "myself";
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var node))
            return null;
        if (node.ValueKind == JsonValueKind.Null)
            return null;
        if (node.ValueKind != JsonValueKind.String)
            return null;
        return node.GetString();
    }

    private AgentResponse AddLocationInferenceDisclosure(
        AgentResponse response,
        ValidatedSlots? validatedSlots)
    {
        if (validatedSlots is null ||
            !validatedSlots.LocationInferred ||
            string.IsNullOrWhiteSpace(validatedSlots.LocationText))
        {
            return response;
        }

        var note = $"Using your previous location context (**{validatedSlots.LocationText}**).";
        if (response.Text.Contains(note, StringComparison.OrdinalIgnoreCase))
            return response;

        return response with
        {
            Text = $"{note}\n\n{response.Text}"
        };
    }

    private AgentResponse AttachContextSnapshot(AgentResponse response)
    {
        var latestUserMessage = _history.LastOrDefault(m => m.Role == "user")?.Content;
        var sanitizedText = SanitizeAssistantResponseText(
            response.Text, response.ToolCallsMade, latestUserMessage);
        if (!string.Equals(sanitizedText, response.Text, StringComparison.Ordinal))
        {
            LogEvent("RESPONSE_SANITIZED",
                "Removed leaked markers or unsupported capability claims.");
            response = response with { Text = sanitizedText };
        }

        var current = _dialogueStore.Get();
        var summaryText = BuildRollingSummary(response.Text);
        _dialogueStore.Update(current with { RollingSummary = summaryText });
        return response with { ContextSnapshot = _dialogueStore.Get().ToSnapshot() };
    }

    private static string BuildRollingSummary(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var compact = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (compact.Length > 180)
            compact = compact[..180].TrimEnd() + "...";
        return compact;
    }

    private static string SanitizeAssistantResponseText(
        string text,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        string? latestUserMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? "";

        var hasEmailToolEvidence = toolCallsMade.Any(
            t => LooksLikeEmailToolName(t.ToolName));
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var filtered = new List<string>(lines.Length);
        var removedUnsupportedDispatch = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (IsInternalMarkerLine(trimmed))
                continue;

            if (!hasEmailToolEvidence &&
                LooksLikeUnsupportedEmailDispatchLine(trimmed))
            {
                removedUnsupportedDispatch = true;
                continue;
            }

            if (!hasEmailToolEvidence &&
                removedUnsupportedDispatch &&
                LooksLikeFollowUpDispatchPromptLine(trimmed))
            {
                continue;
            }

            filtered.Add(line);
        }

        if (filtered.Count == 0)
            return text.Trim();

        var compact = new List<string>(filtered.Count);
        var previousBlank = false;

        foreach (var line in filtered)
        {
            var isBlank = string.IsNullOrWhiteSpace(line);
            if (isBlank && previousBlank)
                continue;

            compact.Add(line);
            previousBlank = isBlank;
        }

        var sanitized = string.Join('\n', compact).Trim();
        sanitized = TruncateSelfDialogue(sanitized);
        sanitized = TrimHallucinatedConversationTail(sanitized, latestUserMessage);
        if (LooksLikeUnsafeMirroringResponse(userMessage: null, assistantText: sanitized))
            return BuildRespectfulResetReply();

        return sanitized;
    }

    private static bool IsInternalMarkerLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        if (!line.StartsWith("[", StringComparison.Ordinal) ||
            !line.EndsWith("]", StringComparison.Ordinal))
        {
            return false;
        }

        var marker = line[1..^1].Trim();
        if (marker.StartsWith("/", StringComparison.Ordinal))
            marker = marker[1..].Trim();

        if (string.IsNullOrWhiteSpace(marker))
            return false;

        if (marker.Contains("TOOL", StringComparison.OrdinalIgnoreCase) ||
            marker.Contains("INSTRUCTION", StringComparison.OrdinalIgnoreCase) ||
            marker.Contains("REFERENCE", StringComparison.OrdinalIgnoreCase) ||
            marker.Contains("ASSISTANT RESPONSE", StringComparison.OrdinalIgnoreCase) ||
            marker.Contains("PROFILE", StringComparison.OrdinalIgnoreCase) ||
            marker.Contains("MEMORY", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var markerChars = marker.Replace("/", "", StringComparison.Ordinal).Trim();
        if (markerChars.Length == 0)
            return false;

        return markerChars.All(c =>
            char.IsUpper(c) ||
            char.IsDigit(c) ||
            c == '_' ||
            c == '-' ||
            c == ' ');
    }

    private static bool LooksLikeUnsupportedEmailDispatchLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var lower = line.ToLowerInvariant();
        if (!lower.Contains("email", StringComparison.Ordinal) &&
            !lower.Contains("e-mail", StringComparison.Ordinal))
        {
            return false;
        }

        return lower.Contains("i can", StringComparison.Ordinal) ||
               lower.Contains("i'll", StringComparison.Ordinal) ||
               lower.Contains("i will", StringComparison.Ordinal) ||
               lower.Contains("just say", StringComparison.Ordinal) ||
               lower.Contains("send", StringComparison.Ordinal) ||
               lower.Contains("mail", StringComparison.Ordinal);
    }

    private static bool LooksLikeFollowUpDispatchPromptLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var lower = line.ToLowerInvariant();
        return lower.StartsWith("want me to send", StringComparison.Ordinal) ||
               lower.Contains("say \"send", StringComparison.Ordinal) ||
               lower.Contains("say 'send", StringComparison.Ordinal) ||
               (lower.Contains("send it", StringComparison.Ordinal) &&
                (lower.Contains("over", StringComparison.Ordinal) ||
                 lower.Contains("to you", StringComparison.Ordinal)));
    }

    private static bool LooksLikeEmailToolName(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        var lower = toolName.ToLowerInvariant();
        return lower.Contains("email", StringComparison.Ordinal) ||
               lower.Contains("mail_", StringComparison.Ordinal) ||
               lower.Contains("_mail", StringComparison.Ordinal) ||
               lower.Contains("smtp", StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static string BuildInlineUtilityResponse(UtilityRouter.UtilityResult utilityResult)
    {
        var primary = (utilityResult.Answer ?? "").Trim();
        if (string.IsNullOrWhiteSpace(primary))
            primary = "Done.";

        var personalityLine = utilityResult.Category.ToLowerInvariant() switch
        {
            "calculator" =>
                "Need another quick one? Toss over the next math step.",
            "conversion" =>
                "Need another unit converted?",
            "fact" =>
                "Want a quick benchmark comparison next?",
            _ => ""
        };

        return string.IsNullOrWhiteSpace(personalityLine)
            ? primary
            : $"{primary}\n\n{personalityLine}";
    }

    private static bool ShouldSuppressUtilityUiArtifacts(string category) =>
        category.Equals("calculator", StringComparison.OrdinalIgnoreCase) ||
        category.Equals("conversion", StringComparison.OrdinalIgnoreCase) ||
        category.Equals("fact", StringComparison.OrdinalIgnoreCase) ||
        category.Equals("text", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<ToolDefinition>> BuildToolDefinitionsAsync(
        CancellationToken ct)
    {
        try
        {
            var mcpTools = await _mcp.ListToolsAsync(ct);

            // When memory is disabled, filter out memory_* tools entirely
            // so the LLM never sees them and can't attempt to call them.
            var filteredTools = MemoryEnabled
                ? mcpTools
                : mcpTools.Where(t =>
                    !t.Name.StartsWith("memory_", StringComparison.OrdinalIgnoreCase) &&
                    !t.Name.StartsWith("Memory",  StringComparison.Ordinal))
                  .ToList();

            var definitions = filteredTools.Select(t => new ToolDefinition
            {
                Function = new FunctionDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = SanitizeSchemaForLocalLlm(t.InputSchema)
                }
            }).ToList();

            LogEvent("AGENT_TOOLS_LOADED", $"{definitions.Count} tool(s): {string.Join(", ", definitions.Select(d => d.Function.Name))}");

            // Log sanitized schemas for debugging "Failed to process regex" errors
            foreach (var def in definitions)
            {
                var schemaJson = JsonSerializer.Serialize(def.Function.Parameters, new JsonSerializerOptions { WriteIndented = false });
                LogEvent("AGENT_TOOL_SCHEMA", $"{def.Function.Name}: {schemaJson}");
            }

            return definitions;
        }
        catch (Exception ex)
        {
            // Don't silently swallow this — log it clearly
            LogEvent("AGENT_TOOLS_FAILED", $"MCP tool discovery failed: {ex.Message}");
            return [];
        }
    }

    // BuildMemoryOnlyToolsAsync removed — replaced by PolicyGate.FilterTools

    // ─────────────────────────────────────────────────────────────────
    // Schema Sanitization
    //
    // The ModelContextProtocol library (via System.Text.Json's
    // JsonSchemaExporter) generates JSON Schemas that may contain
    // constructs local LLMs can't handle — $schema, $defs, $ref,
    // anyOf/oneOf, additionalProperties, etc. LM Studio's grammar
    // engine compiles schemas into regex patterns, and these advanced
    // constructs cause "Failed to process regex" errors.
    //
    // Common offenders:
    //   - Optional params become: anyOf: [{type: "integer"}, {type: "null"}]
    //   - CancellationToken might leak into the schema
    //   - Default values, $ref chains, additionalProperties
    //
    // This sanitizer strips schemas to what LM Studio can handle:
    //   type, properties, required, description, enum, items
    // ─────────────────────────────────────────────────────────────────

    private static object SanitizeSchemaForLocalLlm(object rawSchema)
    {
        try
        {
            var json = JsonSerializer.Serialize(rawSchema);
            using var doc = JsonDocument.Parse(json);
            var clean = CleanSchemaNode(doc.RootElement);
            return clean;
        }
        catch
        {
            return new { type = "object", properties = new { } };
        }
    }

    private static Dictionary<string, object> CleanSchemaNode(JsonElement node)
    {
        var result = new Dictionary<string, object>();

        // ── Handle anyOf / oneOf (optional params, union types) ──────
        // MCP SDK generates these for C# optional/nullable parameters:
        //   "anyOf": [{"type": "integer"}, {"type": "null"}]
        // Extract the first non-null type and merge it into this node.
        if (TryResolveUnionType(node, out var resolvedType, out var resolvedNode))
        {
            result["type"] = resolvedType;

            // If the resolved node has sub-properties (e.g. a complex type),
            // recurse into it
            if (resolvedNode.HasValue && resolvedNode.Value.ValueKind == JsonValueKind.Object)
            {
                var inner = CleanSchemaNode(resolvedNode.Value);
                foreach (var kv in inner)
                {
                    if (kv.Key != "type") // already set above
                        result[kv.Key] = kv.Value;
                }
            }
        }

        foreach (var prop in node.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "type":
                    // Handle type arrays: ["string", "null"] → "string"
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        var types = prop.Value.EnumerateArray()
                            .Where(v => v.ValueKind == JsonValueKind.String)
                            .Select(v => v.GetString()!)
                            .Where(t => t != "null")
                            .ToList();
                        result["type"] = types.Count > 0 ? types[0] : "string";
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        result["type"] = prop.Value.GetString()!;
                    }
                    break;

                case "description":
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        // Strip characters that may break LM Studio's regex
                        var desc = prop.Value.GetString()!
                            .Replace("(", "")
                            .Replace(")", "")
                            .Replace("[", "")
                            .Replace("]", "")
                            .Replace("{", "")
                            .Replace("}", "");
                        result["description"] = desc;
                    }
                    break;

                case "required":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        result["required"] = prop.Value.EnumerateArray()
                            .Where(v => v.ValueKind == JsonValueKind.String)
                            .Select(v => v.GetString()!)
                            .ToArray();
                    }
                    break;

                case "enum":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        result["enum"] = prop.Value.EnumerateArray()
                            .Select(v => v.ToString())
                            .ToArray();
                    }
                    break;

                case "properties":
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        var props = new Dictionary<string, object>();
                        foreach (var p in prop.Value.EnumerateObject())
                        {
                            // Skip CancellationToken if it leaks into schema
                            if (p.Name.Equals("cancellationToken", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (p.Value.ValueKind == JsonValueKind.Object)
                                props[p.Name] = CleanSchemaNode(p.Value);
                        }
                        result["properties"] = props;
                    }
                    break;

                case "items":
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                        result["items"] = CleanSchemaNode(prop.Value);
                    break;

                // Everything else ($schema, $defs, $ref, anyOf, oneOf,
                // allOf, additionalProperties, default, const, etc.)
                // intentionally dropped.
            }
        }

        // Ensure type is always present
        if (!result.ContainsKey("type"))
            result["type"] = "object";

        return result;
    }

    /// <summary>
    /// Resolves anyOf/oneOf union types by extracting the first non-null type.
    /// MCP SDK generates these for optional C# parameters:
    ///   "anyOf": [{"type": "integer"}, {"type": "null"}]
    /// We extract "integer" as the resolved type.
    /// </summary>
    private static bool TryResolveUnionType(
        JsonElement node,
        out string resolvedType,
        out JsonElement? resolvedNode)
    {
        resolvedType = "string";
        resolvedNode = null;

        // Check for anyOf or oneOf arrays
        foreach (var keyword in new[] { "anyOf", "oneOf" })
        {
            if (!node.TryGetProperty(keyword, out var unionArray) ||
                unionArray.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var alt in unionArray.EnumerateArray())
            {
                if (alt.ValueKind != JsonValueKind.Object)
                    continue;

                if (!alt.TryGetProperty("type", out var typeEl) ||
                    typeEl.ValueKind != JsonValueKind.String)
                    continue;

                var typeName = typeEl.GetString();
                if (typeName == "null")
                    continue; // Skip the null alternative

                resolvedType = typeName!;
                resolvedNode = alt;
                return true;
            }

            // Had a union but couldn't resolve — still return true to prevent
            // the node from being treated as a plain object
            return true;
        }

        return false;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "\u2026";

    private void LogEvent(string action, string detail)
    {
        try
        {
            _audit.Append(new AuditEvent
            {
                Actor = "agent",
                Action = action,
                Result = "ok",
                Details = new Dictionary<string, object>
                {
                    ["detail"] = detail
                }
            });
        }
        catch
        {
            // Agent logic must proceed even if audit I/O temporarily fails.
        }
    }

    /// <summary>
    /// Creates a copy of the message history with the mode instruction
    /// merged into the first System message. Avoids adding a second
    /// System message, which breaks Mistral's strict Jinja template.
    /// </summary>
    // ─────────────────────────────────────────────────────────────────
    // Memory Retrieval
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Calls the MemoryRetrieve MCP tool and returns the packText for
    /// system prompt injection plus an onboarding flag. Returns empty
    /// string on any failure — memory retrieval is best-effort and must
    /// never block the main flow.
    ///
    /// When the conversation is brand-new (system prompt + this one user
    /// message only) and the message looks like a greeting, we pass
    /// <c>mode = "greet"</c> to keep retrieval shallow (profile + 1-2
    /// nuggets, no deep fact/event/chunk digging).
    /// </summary>
    private async Task<(string PackText, bool OnboardingNeeded, string? Error)> RetrieveMemoryContextAsync(
        string userMessage, CancellationToken cancellationToken)
    {
        try
        {
            // Cold-greeting detection: first user turn after reset
            // + message looks like a greeting → shallow retrieval mode.
            var isColdGreet = IsColdGreeting(userMessage);
            if (isColdGreet)
                LogEvent("COLD_GREET_DETECTED",
                    "First user message is a greeting — using shallow retrieval.");

            // Include activeProfileId only when explicitly selected.
            // Passing an empty string means "no profile selected", which
            // suppresses legacy fallback profile loading.
            var argsObj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["query"] = userMessage
            };
            if (isColdGreet)
                argsObj["mode"] = "greet";
            if (!string.IsNullOrWhiteSpace(ActiveProfileId))
                argsObj["activeProfileId"] = ActiveProfileId;

            var args = JsonSerializer.Serialize(argsObj);
            var memoryCall = await CallToolWithAliasAsync(
                MemoryRetrieveToolName,
                MemoryRetrieveToolNameAlt,
                args,
                cancellationToken);
            if (!memoryCall.Success)
                return ("", false, memoryCall.Result);

            var result = memoryCall.Result;

            if (string.IsNullOrWhiteSpace(result))
                return ("", false, "Empty response from memory retrieval tool.");

            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errEl) &&
                errEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(errEl.GetString()))
            {
                var error = errEl.GetString()!;
                LogEvent("MEMORY_RETRIEVE_ERROR", error);
                return ("", false, error);
            }

            // Onboarding flag: true when no profile exists at all
            var onboarding = root.TryGetProperty("onboardingNeeded", out var ob)
                          && ob.ValueKind == JsonValueKind.True;

            if (root.TryGetProperty("packText", out var packTextEl) &&
                packTextEl.ValueKind == JsonValueKind.String)
            {
                var packText = packTextEl.GetString() ?? "";

                if (!string.IsNullOrWhiteSpace(packText))
                {
                    // Log the retrieval audit event
                    var facts   = root.TryGetProperty("facts", out var f)   ? f.GetInt32() : 0;
                    var events  = root.TryGetProperty("events", out var ev) ? ev.GetInt32() : 0;
                    var chunks  = root.TryGetProperty("chunks", out var ch) ? ch.GetInt32() : 0;
                    var nuggets = root.TryGetProperty("nuggets", out var ng) ? ng.GetInt32() : 0;
                    var hasProf = root.TryGetProperty("hasProfile", out var hp)
                                 && hp.ValueKind == JsonValueKind.True;

                    LogEvent("MEMORY_RETRIEVED",
                        $"Retrieved {facts} facts, {events} events, " +
                        $"{chunks} chunks, {nuggets} nuggets" +
                        $"{(hasProf ? " (profile loaded)" : "")} for this reply.");

                    return (packText, onboarding, null);
                }
            }

            return ("", onboarding, null);
        }
        catch
        {
            // Memory retrieval is best-effort — never block the main flow
            return ("", false, "Memory retrieval failed before parse.");
        }
    }

    /// <summary>
    /// Mutates history[0] in-place to append the memory pack text.
    /// Used for the tool loop where the same history list is reused
    /// across multiple LLM round-trips.
    /// </summary>
    private static void InjectMemoryIntoHistoryInPlace(
        List<ChatMessage> history, string memoryPackText)
    {
        if (string.IsNullOrWhiteSpace(memoryPackText))
            return;

        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Role == "system")
            {
                history[i] = ChatMessage.System(
                    (history[i].Content ?? "") + memoryPackText);
                return;
            }
        }
    }

    private static List<ChatMessage> InjectModeIntoSystemPrompt(
        List<ChatMessage> history, string modeSuffix)
    {
        var copy = new List<ChatMessage>(history.Count);
        var injected = false;

        foreach (var msg in history)
        {
            if (!injected && msg.Role == "system")
            {
                copy.Add(ChatMessage.System((msg.Content ?? "") + modeSuffix));
                injected = true;
            }
            else
            {
                copy.Add(msg);
            }
        }

        // If there was no system message (shouldn't happen), prepend one
        if (!injected)
            copy.Insert(0, ChatMessage.System(modeSuffix));

        return copy;
    }

    private static bool IsUnknownToolError(string payload, string requestedTool)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        if (!payload.Contains("Unknown tool", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(requestedTool))
            return true;

        if (payload.Contains(requestedTool, StringComparison.OrdinalIgnoreCase))
            return true;

        var pascalAlias = ToPascalCaseToolAlias(requestedTool);
        return !string.IsNullOrWhiteSpace(pascalAlias) &&
               payload.Contains(pascalAlias, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────
    // Search Query Extraction via Tool Call
    //
    // Rather than asking the LLM to follow a freeform "QUERY | RECENCY"
    // prompt (which small models routinely botch), we give it a single
    // web_search tool definition and let it fill in the structured args.
    // Models are far more reliable at producing constrained tool-call
    // arguments than parsing custom extraction formats.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Static tool definition used solely for search query extraction.
    /// Kept minimal so LM Studio's grammar engine compiles quickly.
    /// </summary>
    private static readonly IReadOnlyList<ToolDefinition> SearchExtractionTools =
    [
        new ToolDefinition
        {
            Function = new FunctionDefinition
            {
                Name = "web_search",
                Description = "Search the web for current information, news, or real-time data.",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["query"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] =
                                "Concise 2-6 keyword search query. " +
                                "Topic keywords ONLY — never include greetings, " +
                                "filler, or the assistant's name."
                        },
                        ["recency"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["enum"] = new[] { "day", "week", "month", "any" },
                            ["description"] =
                                "How recent the results should be. " +
                                "'day' = today/latest/breaking, " +
                                "'week' = this week, " +
                                "'month' = this month, " +
                                "'any' = no time constraint."
                        }
                    },
                    ["required"] = new[] { "query", "recency" }
                }
            }
        }
    ];

    private static string NormalizeQueryText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var input = text.Trim();
        var sb = new StringBuilder(input.Length);
        var lastWasSpace = false;

        foreach (var c in input)
        {
            // Keep letters/digits. Convert most punctuation to spaces so
            // tokens like "thadds!" become "thadds" for filtering.
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasSpace = false;
                continue;
            }

            // Keep a few token-internal characters.
            if (c is '\'' or '-' or '+')
            {
                sb.Append(c);
                lastWasSpace = false;
                continue;
            }

            if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static bool IsBannedSearchToken(string tokenLower)
    {
        if (string.IsNullOrWhiteSpace(tokenLower))
            return true;

        // Assistant name variants (common greetings / nicknames).
        if (tokenLower == "thaddeus" || tokenLower.StartsWith("thadd"))
            return true;

        // Greetings, casual filler, discourse markers, pronouns, and
        // request-framing verbs. Anything that isn't a real search topic.
        return tokenLower is
            // ── Greetings / salutations ───────────────────────────
            "sir" or "hey" or "hi" or "hello" or "yo" or "sup" or
            "homie" or "buddy" or "pal" or
            "good" or "morning" or "afternoon" or "evening" or
            // ── Discourse markers / interjections ─────────────────
            "well" or "ok" or "okay" or "alright" or "so" or
            "anyway" or "actually" or "basically" or "like" or
            "heck" or "hell" or "gosh" or "gee" or
            // ── Speech fillers ────────────────────────────────────
            "um" or "uh" or "hmm" or "huh" or "er" or "ah" or
            // ── Pronouns / contractions ───────────────────────────
            "i" or "im" or "i'm" or "we" or "our" or "us" or
            "you" or "me" or "my" or "he" or "she" or "it" or
            "its" or "it's" or "they" or "them" or "their" or
            // ── Modals / auxiliaries ──────────────────────────────
            "can" or "could" or "would" or "will" or "shall" or
            "should" or "might" or "may" or "do" or "does" or
            "did" or "is" or "are" or "was" or "were" or "been" or
            "being" or "have" or "has" or "had" or
            // ── Request framing verbs ─────────────────────────────
            "want" or "wanted" or "need" or "needed" or "check" or
            "look" or "up" or "search" or "find" or "pull" or
            "show" or "get" or "bring" or "grab" or "fetch" or
            "tell" or "give" or
            // ── Polite filler ─────────────────────────────────────
            "please" or "plz" or "thanks" or "thank" or
            "danke" or "dank" or
            // ── Prepositions / articles / connectors ──────────────
            "for" or "to" or "on" or "about" or "into" or "in" or
            "at" or "of" or "with" or "from" or "by" or "or" or
            "and" or "but" or "if" or "then" or "than" or
            "the" or "a" or "an" or "this" or "that" or
            "there" or "here" or "some" or "any" or
            // ── Other low-signal words ────────────────────────────
            "just" or "really" or "very" or "also" or "too" or
            "what" or "how" or "when" or "where" or "know" or
            "think" or "see" or "go" or "going" or "went";
    }

    private static bool LooksLikeLogicPuzzlePrompt(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var normalized = lower.Replace('’', '\'');
        var hasPhotoCue =
            normalized.Contains("who is in the photograph", StringComparison.Ordinal) ||
            normalized.Contains("who is in the photo", StringComparison.Ordinal) ||
            normalized.Contains("who is in the picture", StringComparison.Ordinal) ||
            normalized.Contains("who's in the photograph", StringComparison.Ordinal) ||
            normalized.Contains("who's in the photo", StringComparison.Ordinal) ||
            normalized.Contains("who's in the picture", StringComparison.Ordinal) ||
            normalized.Contains("whos in the photograph", StringComparison.Ordinal) ||
            normalized.Contains("whos in the photo", StringComparison.Ordinal) ||
            normalized.Contains("whos in the picture", StringComparison.Ordinal) ||
            normalized.Contains("looking at a photograph", StringComparison.Ordinal) ||
            normalized.Contains("pointing to a photograph", StringComparison.Ordinal);
        var hasOnlyChildCue =
            normalized.Contains("brothers and sisters, i have none", StringComparison.Ordinal) ||
            normalized.Contains("brothers and sisters i have none", StringComparison.Ordinal) ||
            normalized.Contains("i have no siblings", StringComparison.Ordinal) ||
            normalized.Contains("i don't have siblings", StringComparison.Ordinal) ||
            normalized.Contains("i do not have siblings", StringComparison.Ordinal) ||
            normalized.Contains("i am an only child", StringComparison.Ordinal) ||
            normalized.Contains("i'm an only child", StringComparison.Ordinal);

        var hasFamilyEquation =
            (normalized.Contains("that man's", StringComparison.Ordinal) ||
             normalized.Contains("that woman's", StringComparison.Ordinal) ||
             normalized.Contains("that person's", StringComparison.Ordinal) ||
             normalized.Contains("that boy's", StringComparison.Ordinal) ||
             normalized.Contains("that girl's", StringComparison.Ordinal)) &&
            normalized.Contains(" is my ", StringComparison.Ordinal) &&
            (normalized.Contains(" father's ", StringComparison.Ordinal) ||
             normalized.Contains(" mother's ", StringComparison.Ordinal)) &&
            (normalized.Contains(" son", StringComparison.Ordinal) ||
             normalized.Contains(" daughter", StringComparison.Ordinal));

        return hasPhotoCue && hasOnlyChildCue && hasFamilyEquation;
    }

    private static bool LooksLikeIdentityLookup(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        if (LooksLikeLogicPuzzlePrompt(lower))
            return false;

        // Chit-chat false positives
        if (lower.Contains("what's up") || lower.Contains("whats up"))
            return false;

        // Meta / assistant / self questions that should NOT trigger web search.
        if (lower.Contains("who are you") || lower.Contains("what are you") ||
            lower.Contains("who am i")   || lower.Contains("what is my name") ||
            lower.Contains("what's my name") || lower.Contains("whats my name") ||
            lower.Contains("what is your name") || lower.Contains("what's your name") ||
            lower.Contains("whats your name"))
            return false;

        // Identity / definition triggers (these usually need external lookup)
        return lower.Contains("who is ") ||
               lower.Contains("who's ") ||
               lower.Contains("whos ")  ||
               lower.Contains("who was ") ||
               lower.Contains("who the heck is") ||
               lower.Contains("who the hell is") ||
               lower.Contains("what is ") ||
               lower.Contains("what's ") ||
               lower.Contains("whats ")  ||
               lower.Contains("define ") ||
               lower.Contains("meaning of ") ||
               lower.Contains("what does ");
    }

    private static string IdentityPrefix(string lower)
    {
        // Default to "who is" unless the user clearly asked "what is".
        if (string.IsNullOrWhiteSpace(lower))
            return "who is";

        return (lower.Contains("what is ") || lower.Contains("what's ") || lower.Contains("whats ") ||
                lower.Contains("define ") || lower.Contains("meaning of ") || lower.Contains("what does "))
            ? "what is"
            : "who is";
    }

    private static bool TryExtractIdentitySubject(string userMessage, out string subject)
    {
        subject = "";
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var trimmed = userMessage.Trim();
        var lower = trimmed.ToLowerInvariant();

        string[] markers =
        [
            "who the heck is ",
            "who the hell is ",
            "who is ",
            "who's ",
            "whos ",
            "who was ",
            "what is ",
            "what's ",
            "whats ",
            "define ",
            "meaning of ",
            "what does "
        ];

        foreach (var marker in markers)
        {
            var idx = lower.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
                continue;

            var start = idx + marker.Length;
            if (start >= trimmed.Length)
                continue;

            subject = trimmed[start..].Trim(
                ' ', '?', '!', '.', ',', ':', ';', '"', '\'', '(', ')', '[', ']', '{', '}');

            if (subject.Length > 0)
                return true;
        }

        return false;
    }

    private static string CleanSearchQuery(string query)
    {
        var normalized = NormalizeQueryText(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(tokens.Length);

        foreach (var token in tokens)
        {
            var lower = token.ToLowerInvariant();
            if (IsBannedSearchToken(lower))
                continue;

            kept.Add(token);
            if (kept.Count >= 6) // enforce the 2–6 keyword guideline
                break;
        }

        return string.Join(' ', kept).Trim();
    }

    private static bool WantsUsRegion(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        // Prefer explicit punctuation/casing so we don't confuse pronoun "us"
        // (e.g. "look this up for us") with the region "US".
        if (userMessage.Contains("U.S.", StringComparison.Ordinal) ||
            userMessage.Contains("U.S", StringComparison.Ordinal))
            return true;

        if (userMessage.Contains(" US ", StringComparison.Ordinal) ||
            userMessage.EndsWith(" US", StringComparison.Ordinal) ||
            userMessage.StartsWith("US ", StringComparison.Ordinal))
            return true;

        var lower = userMessage.ToLowerInvariant();
        return lower.Contains("united states") ||
               lower.Contains("usa") ||
               lower.Contains("u.s") ||
               lower.Contains("u s");
    }

    private static bool IsGenericHeadlineQuery(string queryLower)
    {
        var q = (queryLower ?? "").Trim();
        return q is
            "headline" or "headlines" or
            "news" or "latest news" or "breaking news" or
            "latest headlines" or "breaking headlines" or
            "top headlines";
    }

    // ─────────────────────────────────────────────────────────────────
    // Vague follow-up query detection + topic resolution
    //
    // Small local models frequently fail to resolve conversational
    // references ("that", "it", "more") during search query extraction
    // and echo back the user's vague wording instead. These helpers
    // catch that case and pull the real topic from the last assistant
    // response — entirely deterministic, no extra LLM call.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the extracted query looks like an unresolved
    /// follow-up reference — words that only make sense in context
    /// but carry zero topical signal for a search engine.
    /// </summary>
    private static bool IsVagueFollowUpQuery(string query)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();

        // Very short queries that are clearly contextual
        if (q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3)
        {
            // Direct matches: the model literally echoed the filler
            string[] vaguePatterns =
            [
                "more info",
                "more information",
                "more on that",
                "more about that",
                "more about it",
                "more on it",
                "more details",
                "tell me more",
                "go deeper",
                "elaborate",
                "that topic",
                "the topic",
                "that story",
                "the story",
                "that article",
                "that",
                "it",
                "this"
            ];

            foreach (var pattern in vaguePatterns)
            {
                if (q == pattern || q.Contains(pattern))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts to extract a concrete topic from the most recent
    /// assistant message in history. Uses the first sentence (the
    /// "bottom line") which typically contains the core subject.
    /// Returns false if no usable topic can be extracted.
    /// </summary>
    private bool TryExtractTopicFromLastAssistant(out string topic)
    {
        topic = "";

        // Walk history backwards to find the last assistant message
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Role != "assistant")
                continue;

            var content = (_history[i].Content ?? "").Trim();
            if (string.IsNullOrWhiteSpace(content))
                continue;

            // Strip common lead-ins: "Bottom line:", "Here's what I found:", etc.
            var cleaned = content;
            string[] leadIns =
            [
                "bottom line:",
                "here's what i found:",
                "here is what i found:",
                "summary:",
                "in short:",
                "tl;dr:"
            ];
            var lower = cleaned.ToLowerInvariant();
            foreach (var lead in leadIns)
            {
                if (lower.StartsWith(lead))
                {
                    cleaned = cleaned[lead.Length..].TrimStart();
                    break;
                }
            }

            // Take the first sentence — that's the core topic.
            // Split on sentence terminators but not on abbreviations.
            var firstSentence = cleaned;
            var sentenceEnd = cleaned.IndexOfAny(['.', '!', '?', '\n']);
            if (sentenceEnd > 10) // require at least a meaningful chunk
                firstSentence = cleaned[..sentenceEnd].Trim();

            // Compress to a search-friendly query: take up to
            // the first ~80 chars and strip excessive whitespace.
            if (firstSentence.Length > 80)
                firstSentence = firstSentence[..80].Trim();

            // Ensure we have something meaningful (>= 5 chars)
            if (firstSentence.Length >= 5)
            {
                topic = firstSentence;
                return true;
            }

            break; // only check the most recent assistant message
        }

        return false;
    }

    /// <summary>
    /// Extracts a search query and recency by asking the LLM to produce
    /// a <c>web_search</c> tool call. The LLM receives the full
    /// conversation history so it can determine the actual topic from
    /// context rather than relying on brittle keyword filtering.
    ///
    /// Post-processing is minimal: strip assistant name references,
    /// apply identity/headline defaults, and validate bounds. Everything
    /// else is the model's job — it has the context to get it right.
    ///
    /// Falls back to deterministic cleanup only if the LLM fails to
    /// produce a tool call at all (e.g., model error, text-only response).
    /// </summary>
    private async Task<(string Query, string Recency)> ExtractSearchViaToolCallAsync(
        string userMessage, string memoryPackText, CancellationToken cancellationToken)
    {
        const string defaultRecency = "any";
        var lowerMsg = (userMessage ?? "").ToLowerInvariant();
        var useConversationContext =
            SearchModeRouter.IsFollowUpMessage(lowerMsg) ||
            SearchModeRouter.IsReferential(lowerMsg) ||
            LooksLikeFollowUpDepthRequest(userMessage ?? "");
        var wantsUs = WantsUsRegion(userMessage ?? "");
        var isIdentity = LooksLikeIdentityLookup(lowerMsg);
        var identityPrefix = IdentityPrefix(lowerMsg);

        try
        {
            var now = _timeProvider.GetUtcNow();

            // ── Build search extractor prompt ─────────────────────────
            // Tiny local models can over-anchor on prior turns when we
            // always send full history. Only do that for true follow-ups.
            var systemContent =
                "You are a search query extractor.\n" +
                (useConversationContext
                    ? "Read the FULL conversation history and determine what the user wants to search for.\n"
                    : "Treat this as a NEW standalone question. Use ONLY the latest user message as the topic source and ignore prior turns.\n") +
                "Call the web_search tool with the appropriate query and recency.\n\n" +
                $"Today's date is {now:yyyy-MM-dd} (UTC). The current year is {now.Year}.\n\n" +
                "Rules:\n" +
                "- Extract the TOPIC the user wants to look up — 2 to 6 keywords.\n" +
                "- CRITICAL: If the user uses pronouns or vague references like " +
                "'that', 'it', 'this', 'more info', 'tell me more', 'more about', " +
                "'go deeper', 'elaborate', you MUST resolve the reference.\n" +
                "  Look at the PREVIOUS assistant message to find the actual topic.\n" +
                "  Example: if the last answer was about 'UK Prime Minister staff " +
                "resignations' and the user says 'more on that', " +
                "the query is 'UK Prime Minister staff resignations', NOT 'more info'.\n" +
                "- NEVER use the user's vague wording as the query. " +
                "Always resolve to the concrete topic from the conversation.\n" +
                "- Ignore greetings, filler, discourse markers (well, ok, so...), " +
                "and the assistant's name. These are NEVER search terms.\n" +
                "- If the user asks for 'news', 'headlines', or 'latest', " +
                "set recency to 'day'.\n" +
                "- For generic news requests with no specific topic, " +
                "use query: \"top headlines\".\n" +
                "- If the user asks an evergreen fact question (e.g., \"who won X\"), " +
                "do NOT guess a year. Prefer queries like \"most recent X winner\".\n" +
                "- ALWAYS call the tool. Never reply with text.";

            if (!string.IsNullOrWhiteSpace(memoryPackText))
                systemContent += "\n\n" + memoryPackText;

            var messages = new List<ChatMessage> { ChatMessage.System(systemContent) };
            if (useConversationContext)
            {
                foreach (var msg in _history)
                {
                    if (msg.Role is "system") continue; // already have ours
                    messages.Add(msg);
                }

                // If the latest user message isn't already in history
                // (it can vary depending on caller timing), add it.
                if (_history.Count == 0 ||
                    _history[^1].Role != "user" ||
                    _history[^1].Content != userMessage)
                {
                    messages.Add(ChatMessage.User(userMessage ?? ""));
                }
            }
            else
            {
                messages.Add(ChatMessage.User(userMessage ?? ""));
            }

            LogEvent(
                "AGENT_QUERY_SCOPE",
                useConversationContext ? "context=full_history" : "context=latest_message_only");

            var response = await _llm.ChatAsync(
                messages, SearchExtractionTools, maxTokensOverride: 80, cancellationToken);

            // ── Parse the tool call response ──────────────────────────
            if (response.ToolCalls is { Count: > 0 })
            {
                var args = response.ToolCalls[0].Function.Arguments;
                using var doc = JsonDocument.Parse(args);
                var root = doc.RootElement;

                var query = root.TryGetProperty("query", out var q)
                    ? (q.GetString() ?? "").Trim()
                    : "";
                var recency = root.TryGetProperty("recency", out var r)
                    ? NormalizeRecency(r.GetString() ?? "")
                    : defaultRecency;

                // ── Minimal safety net: strip assistant name only ─────
                // The LLM has full context and should produce a clean
                // query. We only strip references to the assistant's
                // name (the one thing the model might echo back).
                var cleanedQuery = StripAssistantName(query);

                // Prefer explicit recency hints from the user message
                // (deterministic override — the LLM sometimes misses these).
                var recencyFromUser = DetectRecencyFallback(userMessage ?? "");
                if (recencyFromUser != "any" && recencyFromUser != recency)
                    recency = recencyFromUser;

                // Generic "headlines"/"news" → stable default.
                if (IsGenericHeadlineQuery(cleanedQuery.ToLowerInvariant()))
                    cleanedQuery = wantsUs ? "U.S. top headlines" : "top headlines";

                // Generic headlines with no recency specified should default to day.
                if (IsGenericHeadlineQuery(cleanedQuery.ToLowerInvariant()) && recency == "any")
                    recency = "day";

                // Follow-up: if we already have sources from the prior search,
                // prefer a concrete title over a generic query like "more X news".
                if (LooksLikeFollowUpDepthRequest(userMessage ?? "") &&
                    _searchOrchestrator.Session.LastResults.Count > 0)
                {
                    var candidates = PickRelevantSources(userMessage ?? "", _searchOrchestrator.Session.LastResults.Select(r => (r.Url, r.Title)).ToList(), maxUrls: 1);
                    if (candidates.Count > 0 && !string.IsNullOrWhiteSpace(candidates[0].Title))
                    {
                        var titleQuery = candidates[0].Title.Trim();
                        if (titleQuery.Length > 120) titleQuery = titleQuery[..120].Trim();

                        LogEvent("AGENT_QUERY_RESOLVE",
                            $"Follow-up query resolved from prior sources: \"{cleanedQuery}\" → \"{titleQuery}\"");
                        cleanedQuery = titleQuery;
                    }

                    if (recency == "any" && (_searchOrchestrator.Session.LastRecency ?? "any") != "any")
                        recency = _searchOrchestrator.Session.LastRecency!;
                }

                // Identity queries: prepend "who is"/"what is" if needed.
                if (isIdentity && !string.IsNullOrWhiteSpace(cleanedQuery))
                {
                    var ql = cleanedQuery.ToLowerInvariant();
                    if (!ql.StartsWith("who is") && !ql.StartsWith("what is") &&
                        !ql.StartsWith("who's")  && !ql.StartsWith("whos") &&
                        !ql.StartsWith("what's") && !ql.StartsWith("whats"))
                    {
                        cleanedQuery = $"{identityPrefix} {cleanedQuery}".Trim();
                    }

                    recency = "any";
                }

                (cleanedQuery, recency) = ApplyTemporalSanityChecks(
                    userMessage ?? "", cleanedQuery, recency);

                // ── Vague follow-up detection ────────────────────────
                // Small models often echo the user's vague wording
                // ("more info", "that topic") instead of resolving
                // the reference from history. When the extracted query
                // looks like a follow-up placeholder and we have prior
                // context, replace it with the actual topic.
                if (IsVagueFollowUpQuery(cleanedQuery) &&
                    TryExtractTopicFromLastAssistant(out var resolvedTopic))
                {
                    LogEvent("AGENT_QUERY_RESOLVE",
                        $"Vague query \"{cleanedQuery}\" → " +
                        $"resolved to \"{resolvedTopic}\" from prior context");
                    cleanedQuery = resolvedTopic;
                }

                // Accept if non-empty and within bounds.
                if (!string.IsNullOrWhiteSpace(cleanedQuery) &&
                    cleanedQuery.Length >= 2 && cleanedQuery.Length <= 120)
                {
                    LogEvent("AGENT_QUERY_EXTRACT",
                        $"Tool call: query=\"{cleanedQuery}\", recency={recency}");
                    return (cleanedQuery, recency);
                }

                LogEvent("AGENT_QUERY_EXTRACT",
                    $"Tool call returned empty/invalid query \"{query}\" " +
                    "— falling through to deterministic cleanup");
            }
            else
            {
                LogEvent("AGENT_QUERY_EXTRACT",
                    "LLM did not produce a tool call — using deterministic fallback");
            }
        }
        catch (Exception ex)
        {
            LogEvent("AGENT_QUERY_EXTRACT_FAIL",
                $"Tool-call extraction failed: {ex.Message}");
        }

        // ── Deterministic fallback ────────────────────────────────────
        // Only reached when the LLM fails to produce a usable tool call
        // (model error, text-only response, empty output). This is the
        // safety net, not the primary path.
        var fallbackQuery = CleanSearchQuery(StripConversationalFiller(userMessage ?? ""));
        var fallbackRecency = DetectRecencyFallback(userMessage ?? "");

        // Apply the same vague follow-up resolution here too.
        if (IsVagueFollowUpQuery(fallbackQuery) &&
            TryExtractTopicFromLastAssistant(out var fallbackResolvedTopic))
        {
            LogEvent("AGENT_QUERY_RESOLVE",
                $"Deterministic fallback: vague \"{fallbackQuery}\" → " +
                $"\"{fallbackResolvedTopic}\" from prior context");
            fallbackQuery = fallbackResolvedTopic;
        }

        if (isIdentity)
        {
            if (TryExtractIdentitySubject(userMessage ?? "", out var subject))
            {
                var cleanSubject = CleanSearchQuery(subject);
                if (!string.IsNullOrWhiteSpace(cleanSubject))
                    fallbackQuery = $"{identityPrefix} {cleanSubject}".Trim();
            }
            else if (!string.IsNullOrWhiteSpace(fallbackQuery))
            {
                fallbackQuery = $"{identityPrefix} {fallbackQuery}".Trim();
            }

            fallbackRecency = "any";
        }

        if (string.IsNullOrWhiteSpace(fallbackQuery))
        {
            if (lowerMsg.Contains("headline") || lowerMsg.Contains("headlines") ||
                lowerMsg.Contains("news") || lowerMsg.Contains("latest") || lowerMsg.Contains("breaking"))
            {
                fallbackQuery = wantsUs ? "U.S. top headlines" : "top headlines";
            }
        }

        (fallbackQuery, fallbackRecency) = ApplyTemporalSanityChecks(
            userMessage ?? "", fallbackQuery, fallbackRecency);

        if (IsGenericHeadlineQuery(fallbackQuery.ToLowerInvariant()))
            fallbackQuery = wantsUs ? "U.S. top headlines" : "top headlines";

        return (fallbackQuery, fallbackRecency);
    }

    /// <summary>
    /// Applies narrow, deterministic sanity checks to reduce obvious
    /// time-related mistakes from local models (e.g., injecting an
    /// arbitrary year the user never asked for).
    /// </summary>
    private (string Query, string Recency) ApplyTemporalSanityChecks(
        string userMessage, string query, string recency)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (query, recency);

        var lowerMsg = (userMessage ?? "").ToLowerInvariant();
        var lowerQuery = query.ToLowerInvariant();

        // If the user specified a year (2024) or a relative-year hint ("last year"),
        // do not override — they know what they asked for.
        if (ContainsExplicitYear(lowerMsg) || ContainsRelativeYearHint(lowerMsg))
            return (query, recency);

        // Super Bowl winner questions are common and local models tend to
        // hallucinate the last year they "remember". Force a stable query.
        if (LooksLikeSuperBowlWinnerQuestion(lowerMsg, lowerQuery))
        {
            if (TryExtractYear(query, out var year))
            {
                var nowYear = _timeProvider.GetUtcNow().Year;
                if (year != nowYear && year != nowYear - 1)
                    LogEvent("AGENT_TEMPORAL_FIXUP",
                        $"Replacing guessed year {year} in query \"{query}\"");
            }

            return ("most recent Super Bowl winner", "any");
        }

        return (query, recency);
    }

    private static bool LooksLikeSuperBowlWinnerQuestion(string lowerMsg, string lowerQuery)
    {
        var mentionsSuperBowl = lowerMsg.Contains("super bowl") || lowerMsg.Contains("superbowl") ||
                                lowerQuery.Contains("super bowl") || lowerQuery.Contains("superbowl");

        if (!mentionsSuperBowl) return false;

        var winnerIntent =
            lowerMsg.Contains("who won") ||
            lowerMsg.Contains("winner") ||
            lowerMsg.Contains("won the super bowl") ||
            lowerMsg.Contains("won the superbowl") ||
            lowerQuery.Contains("winner");

        return winnerIntent;
    }

    private static bool ContainsRelativeYearHint(string lower)
        => lower.Contains("last year") ||
           lower.Contains("this year") ||
           lower.Contains("years ago") ||
           lower.Contains("year ago") ||
           lower.Contains("previous year") ||
           lower.Contains("prior year");

    private static bool ContainsExplicitYear(string text)
        => TryExtractYear(text, out _);

    private static bool TryExtractYear(string text, out int year)
    {
        year = 0;
        if (string.IsNullOrEmpty(text) || text.Length < 4)
            return false;

        for (var i = 0; i <= text.Length - 4; i++)
        {
            if (!char.IsDigit(text[i]) ||
                !char.IsDigit(text[i + 1]) ||
                !char.IsDigit(text[i + 2]) ||
                !char.IsDigit(text[i + 3]))
                continue;

            if (!int.TryParse(text.AsSpan(i, 4), out var candidate))
                continue;

            if (candidate is >= 1900 and <= 2100)
            {
                year = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Strips only assistant name references from a query string.
    /// This is the only deterministic post-processing applied to the
    /// LLM's tool call output — everything else is the model's job.
    /// </summary>
    private static string StripAssistantName(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "";

        var normalized = NormalizeQueryText(query);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(tokens.Length);

        foreach (var token in tokens)
        {
            var lower = token.ToLowerInvariant();

            // Only filter assistant name variants — nothing else.
            if (lower == "thaddeus" || lower.StartsWith("thadd") || lower == "sir")
                continue;

            kept.Add(token);
        }

        return string.Join(' ', kept).Trim();
    }

    /// <summary>
    /// Quick keyword-based recency detection used when the LLM call
    /// is skipped or fails. Keeps things working even without the model.
    /// </summary>
    private static string DetectRecencyFallback(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("today") || lower.Contains("this morning") ||
            lower.Contains("right now") || lower.Contains("just happened"))
            return "day";
        if (lower.Contains("this week") || lower.Contains("past week") ||
            lower.Contains("last week") || lower.Contains("last few days"))
            return "week";
        if (lower.Contains("this month") || lower.Contains("past month") ||
            lower.Contains("recently"))
            return "month";
        if (lower.Contains("breaking") || lower.Contains("headline") || lower.Contains("headlines") ||
            lower.Contains("top stories") ||
            (lower.Contains("latest") &&
             (lower.Contains("news") || lower.Contains("headline") || lower.Contains("headlines") ||
              lower.Contains("update") || lower.Contains("updates") || lower.Contains("happening"))))
            return "day";
        return "any";
    }

    /// <summary>
    /// Strips conversational filler from a user message to produce a
    /// cleaner search query when the LLM extraction fails. Removes
    /// leading phrases like "can you check", "I want to look up", etc.
    /// and trailing noise like "for me", "please".
    ///
    /// Example: "Can you check up news on the US stock market this week?
    /// its been a crazy week, what happened?"
    ///   → "news on the US stock market this week"
    /// </summary>
    private static string StripConversationalFiller(string input)
    {
        var text = input.Trim(' ', '?', '!', '.', ',');
        var lower = text.ToLowerInvariant();

        // ── Strip leading greetings / salutations ─────────────────────
        // Users often open with "hey sir thaddeus!" or "hello!" before
        // stating their actual request. Peel those off first.
        string[] greetingPrefixes =
        [
            "hey sir thaddeus",   "hi sir thaddeus",
            "hello sir thaddeus", "yo sir thaddeus",
            "hey thaddeus",       "hi thaddeus",
            "hello thaddeus",     "yo thaddeus",
            "good morning",       "good afternoon",
            "good evening",       "hey there",
            "hi there",           "hello there",
            "hey",                "hi",
            "hello",              "yo",
            // ── Discourse markers / hedges ────────────────────────
            // Users often open with these before their real request.
            // "Well. I wanted to check..." → "I wanted to check..."
            "well",               "ok so",
            "okay so",            "alright so",
            "so",                 "ok",
            "okay",               "alright",
            "anyway",             "actually",
            "basically",
        ];

        foreach (var greet in greetingPrefixes)
        {
            if (lower.StartsWith(greet))
            {
                text  = text[greet.Length..].TrimStart(' ', ',', '!', '.', '-');
                lower = text.ToLowerInvariant();
                break;
            }
        }

        // ── Strip assistant name prefix variants ──────────────────────
        // After removing "hey/hi/hello", users often have a name token
        // next ("thadds!") which is pure salutation, not search topic.
        string[] assistantNamePrefixes =
        [
            "sir thaddeus",
            "thaddeus",
            "thadds",
            "thaddy",
            "thadd"
        ];

        foreach (var name in assistantNamePrefixes)
        {
            if (lower.StartsWith(name))
            {
                text  = text[name.Length..].TrimStart(' ', ',', '!', '.', '?', '-', ':');
                lower = text.ToLowerInvariant();
                break;
            }
        }

        // ── Strip "how are you" / chit-chat follow-ups ────────────────
        string[] chitChat =
        [
            "how the heck are you today",
            "how the heck are you",
            "how are you doing today",
            "how are you doing",
            "how are you today",
            "how are you",
            "how's it going",
            "hows it going",
            "what's up",
            "whats up",
        ];

        foreach (var cc in chitChat)
        {
            if (lower.StartsWith(cc))
            {
                text  = text[cc.Length..].TrimStart(' ', ',', '!', '.', '?', '-');
                lower = text.ToLowerInvariant();
                break;
            }
        }

        // ── Leading filler phrases (order matters: longest first) ────
        string[] leadPhrases =
        [
            "actually, can you check up",
            "actually can you check up",
            "actually, can you check",
            "actually can you check",
            "can you check up the news on",
            "can you check up news on",
            "can you check up on",
            "can you check the news today",
            "can you look up the news today",
            "can you search for",   "can you search up",
            "can you look up",      "can you look into",
            "can you find out",     "can you find me",
            "can you pull up",      "can you check on",
            "can you check up",     "can you check",
            "can you get me",
            "could you search for", "could you look up",
            "could you find",       "could you check",
            "please search for",    "please look up",
            "please find",          "please check",
            "i want to look up information on how",
            "i want to look up information on",
            "i want to look up information about",
            "i want to look up info on",
            "i want to look up",    "i want to search for",
            "i want to find out about",
            "i want to find out",   "i want to find",
            "i want to know about", "i want to know",
            "i want to see whats happening with",
            "i want to see what's happening with",
            "i want to see",
            "i need to look up",    "i need to find",
            "i'd like to know about", "i'd like to know",
            "tell me about",        "tell me what",
            "show me",              "get me",
            "look up",              "search for",
            "search up",            "pull up the news on",
            "pull up the news about",
            "pull up news on",      "pull up news about",
            "pull up the news",     "pull up news",
            "pull up",              "find out about",
            "find out",             "check on",
            "check the",            "what's going on with",
            "whats going on with",  "what is going on with",
            "what happened with",   "what happened to",
            "what's happening with", "whats happening with",
            "how has",              "how is",
        ];

        foreach (var phrase in leadPhrases)
        {
            if (lower.StartsWith(phrase))
            {
                text  = text[phrase.Length..].TrimStart(' ', ',', ':', '-');
                lower = text.ToLowerInvariant();
                break; // Only strip one leading phrase
            }
        }

        // ── Trailing filler ─────────────────────────────────────────
        string[] trailPhrases =
        [
            "for me please", "for me", "please", "right now",
            "if you can", "if possible", "when you get a chance"
        ];

        foreach (var phrase in trailPhrases)
        {
            if (lower.EndsWith(phrase))
            {
                text  = text[..^phrase.Length].TrimEnd(' ', ',', '.');
                lower = text.ToLowerInvariant();
                break;
            }
        }

        // ── Sentence splitting: if multiple sentences remain,
        // keep the one with the most topic signal (proper nouns,
        // domain-specific words) rather than emotional commentary ─────
        var sentences = text.Split(['.', '?', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 2)
            .ToArray();

        if (sentences.Length > 1)
        {
            // Words that are pure emotional/conversational filler —
            // they tell us nothing about what to search for.
            string[] fillerWords = ["i", "you", "can", "could", "want",
                "wanted", "need", "needed",
                "to", "the", "a", "an", "do", "does", "is", "are",
                "was", "were", "been", "have", "has", "had",
                "please", "check", "look", "find", "search", "up",
                "me", "my", "on", "about", "information", "info",
                "it", "its", "it's", "been", "what", "how", "so",
                "just", "really", "actually", "basically", "totally",
                // Discourse markers / hedges — must be penalized so
                // single-word sentences like "Well" never win.
                "well", "ok", "okay", "alright", "anyway", "right",
                "sure", "yes", "no", "yeah", "yep", "nope",
                "there", "here", "this", "that"];

            // Words that signal "this sentence has a real topic."
            // Sentences with uppercase words (proper nouns) or domain
            // keywords score higher.
            string[] topicSignals = ["news", "market", "stock", "crypto",
                "price", "weather", "score", "game", "election",
                "update", "latest", "recent", "happening",
                "headlines", "breaking", "sports", "politics",
                "tech", "technology", "science", "war", "economy",
                "finance", "results", "recap", "forecast"];

            var best = sentences
                .OrderByDescending(s =>
                {
                    var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var lowerWords = words.Select(w => w.ToLowerInvariant()).ToArray();

                    // Count uppercase-start words (likely proper nouns)
                    var properNouns = words.Count(w =>
                        w.Length > 1 && char.IsUpper(w[0]));

                    // Count topic signal words
                    var topicHits = lowerWords.Count(w =>
                        topicSignals.Contains(w));

                    // Penalize high filler ratio
                    var fillerRatio = words.Length > 0
                        ? (double)lowerWords.Count(w => fillerWords.Contains(w)) / words.Length
                        : 1.0;

                    // Score: more proper nouns & topic words = better,
                    // high filler ratio = worse
                    return (properNouns * 3) + (topicHits * 2) - (fillerRatio * 5);
                })
                .ThenByDescending(s => s.Length)
                .First();

            text = best;
        }

        // Final trim
        text = text.Trim(' ', '?', '!', '.', ',');

        // If we stripped everything, fall back to the original
        return text.Length >= 3 ? text : input.Trim(' ', '?', '!', '.', ',');
    }

    /// <summary>
    /// Normalizes an LLM-returned recency string to a known value.
    /// </summary>
    private static string NormalizeRecency(string raw)
    {
        var r = (raw ?? "any").Trim().ToLowerInvariant();
        return r switch
        {
            "day" or "today" or "24h"   => "day",
            "week" or "7d"              => "week",
            "month" or "30d"            => "month",
            _                           => "any"
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Intent Router
    //
    // Produces a RouterOutput for the policy gate. Uses the same
    // hybrid approach as before (fast-path → heuristics → LLM → fallback)
    // but now outputs structured intent + capability flags instead of
    // a coarse enum.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Routes the user message to a structured <see cref="RouterOutput"/>.
    /// This is the entry point for the new pipeline. Internally uses
    /// <see cref="ClassifyIntentAsync"/> then refines the Tooling
    /// intent into sub-intents via heuristics.
    /// </summary>
    private async Task<RouterOutput> RouteMessageAsync(
        string userMessage, CancellationToken cancellationToken)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();

        // ── Fast-path overrides ──────────────────────────────────────
        if (lower.StartsWith("/browse ") || lower.StartsWith("browse:"))
            return MakeRoute(Intents.BrowseOnce, confidence: 1.0,
                needsWeb: true, needsBrowser: true);

        // Referential "but why / explain that logic" follow-ups should stay
        // in conversational mode when they refer to a recent first-principles
        // decision from this session. Without that anchor, let normal routing
        // decide so unrelated prompts still work.
        if (LooksLikeReasoningFollowUp(lower) && HasRecentFirstPrinciplesRationale())
            return MakeRoute(Intents.ChatOnly, confidence: 0.92);

        // Follow-up depth requests ("tell me more about that") should route
        // through the deterministic search pipeline, not the general tool
        // loop. The SearchOrchestrator handles source selection and deep-dive.
        if (SearchModeRouter.IsFollowUpMessage(lower) &&
            _searchOrchestrator.Session.HasRecentResults(DateTimeOffset.UtcNow))
            return MakeRoute(Intents.LookupSearch, confidence: 0.95,
                needsWeb: true, needsSearch: true, needsBrowser: true);

        // Screen requests are unambiguously tool-driven, but smaller local
        // models sometimes misclassify them as "chat". Route these
        // deterministically so the policy gate can expose screen tools.
        if (LooksLikeScreenRequest(lower))
            return MakeRoute(Intents.ScreenObserve, confidence: 0.95,
                needsScreen: true);

        // ── Deterministic utility pre-router (bypass LLM classify) ───
        // Contract for this route:
        //   needsWeb=false, needsSearch=false
        //   policy allowedTools=[]
        //   execution mode is inline (no tool loop)
        //   bypassLLM=true (this early return)
        var deterministicPreRoute = DeterministicPreRouter.TryRoute(userMessage ?? string.Empty);
        if (deterministicPreRoute is not null)
        {
            var confidence = deterministicPreRoute.Confidence == DeterministicMatchConfidence.High
                ? 0.99
                : 0.75;

            LogEvent(
                "ROUTER_DETERMINISTIC_PREROUTE",
                $"confidence={deterministicPreRoute.Confidence}, category={deterministicPreRoute.Result.Category}");

            return MakeRoute(
                Intents.UtilityDeterministic,
                confidence: confidence,
                needsWeb: false,
                needsSearch: false);
        }

        // ── Classify via existing LLM + heuristic pipeline ───────────
        var intent = await ClassifyIntentAsync(userMessage ?? string.Empty, cancellationToken);

        // ── Map coarse intent to fine-grained RouterOutput ───────────
        return intent switch
        {
            ChatIntent.Casual    => MakeRoute(Intents.ChatOnly, confidence: 0.8),
            ChatIntent.WebLookup => MakeRoute(Intents.LookupSearch, confidence: 0.9,
                needsWeb: true, needsSearch: true),
            ChatIntent.Tooling   => RefineToolingIntent(lower),
            _                    => MakeRoute(Intents.GeneralTool, confidence: 0.3)
        };
    }

    /// <summary>
    /// Breaks the coarse "Tooling" classification into specific
    /// sub-intents based on keyword heuristics. If no specific
    /// sub-intent matches, falls back to GeneralTool (all tools).
    /// </summary>
    private static RouterOutput RefineToolingIntent(string lower)
    {
        // Order matters — check the most specific patterns first

        if (LooksLikeMemoryWriteRequest(lower))
            return MakeRoute(Intents.MemoryWrite, confidence: 0.9,
                needsMemoryWrite: true);

        if (LooksLikeScreenRequest(lower))
            return MakeRoute(Intents.ScreenObserve, confidence: 0.85,
                needsScreen: true);

        if (LooksLikeFileRequest(lower))
            return MakeRoute(Intents.FileTask, confidence: 0.85,
                needsFile: true);

        if (LooksLikeSystemCommand(lower))
            return MakeRoute(Intents.SystemTask, confidence: 0.8,
                needsSystem: true, risk: "medium");

        if (LooksLikeBrowseRequest(lower))
            return MakeRoute(Intents.BrowseOnce, confidence: 0.85,
                needsWeb: true, needsBrowser: true);

        // Can't narrow it down — give all tools
        return MakeRoute(Intents.GeneralTool, confidence: 0.4);
    }

    /// <summary>
    /// Maps a <see cref="RouterOutput"/> back to the legacy
    /// <see cref="ChatIntent"/> enum for code that still uses it
    /// (WebLookup deterministic path).
    /// </summary>
    private static ChatIntent MapRouteToLegacyIntent(RouterOutput route)
    {
        return route.Intent switch
        {
            Intents.ChatOnly      => ChatIntent.Casual,
            Intents.UtilityDeterministic => ChatIntent.Casual,
            Intents.MemoryRead    => ChatIntent.Casual,
            Intents.LookupSearch  => ChatIntent.WebLookup,
            _                     => ChatIntent.Tooling
        };
    }

    private static bool IsDeterministicInlineRoute(RouterOutput route) =>
        string.Equals(route.Intent, Intents.UtilityDeterministic, StringComparison.OrdinalIgnoreCase);

    private static UtilityRouter.UtilityResult ToUtilityResult(DeterministicUtilityMatch match)
    {
        return new UtilityRouter.UtilityResult
        {
            Category = match.Result.Category,
            Answer = match.Result.Answer
        };
    }

    private static bool ShouldAttemptReasoningGuardrails(RouterOutput route, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (route.NeedsScreenRead ||
            route.NeedsFileAccess ||
            route.NeedsSystemExecute ||
            route.NeedsBrowserAutomation ||
            route.NeedsMemoryRead ||
            route.NeedsMemoryWrite)
        {
            return false;
        }

        return route.Intent is Intents.ChatOnly or Intents.LookupSearch or Intents.GeneralTool;
    }

    private AgentResponse? TryBuildFirstPrinciplesFollowUpResponse(
        string userMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        int roundTrips)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();
        if (!LooksLikeReasoningFollowUp(lower))
            return null;

        if (!HasRecentFirstPrinciplesRationale())
            return null;

        var age = _timeProvider.GetUtcNow() - _lastFirstPrinciplesAt;

        var goal = ExtractRationaleValue(
            _lastFirstPrinciplesRationale,
            prefix: "Goal:",
            fallback: "complete the real-world objective");
        var constraint = ExtractRationaleValue(
            _lastFirstPrinciplesRationale,
            prefix: "Constraint:",
            fallback: "pick the option that is physically feasible and goal-aligned");
        var decision = ExtractRationaleValue(
            _lastFirstPrinciplesRationale,
            prefix: "Decision:",
            fallback: "choose the option that directly completes the task");

        var text =
            $"Because the goal was to {goal}. " +
            $"The deciding constraint was: {constraint}. " +
            $"So the choice was: {decision}.";

        _audit.Append(new AuditEvent
        {
            Actor = "agent",
            Action = "FIRST_PRINCIPLES_FOLLOWUP",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["ageSeconds"] = Math.Max(0, (long)age.TotalSeconds)
            }
        });

        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips,
            GuardrailsUsed = true,
            GuardrailsRationale = _lastFirstPrinciplesRationale.Take(3).ToArray()
        };
    }

    private bool HasRecentFirstPrinciplesRationale()
    {
        if (_lastFirstPrinciplesAt == default ||
            _lastFirstPrinciplesRationale.Count < 3)
        {
            return false;
        }

        var age = _timeProvider.GetUtcNow() - _lastFirstPrinciplesAt;
        return age <= FirstPrinciplesFollowUpTtl;
    }

    private static string ExtractRationaleValue(
        IReadOnlyList<string> rationale,
        string prefix,
        string fallback)
    {
        foreach (var line in rationale)
        {
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line[prefix.Length..].Trim();
            value = value.TrimEnd('.', ';', ':').Trim();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        return fallback;
    }

    private static bool LooksLikeReasoningFollowUp(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower) || lower.Length > 220)
            return false;

        var ultraShortWhy =
            string.Equals(lower, "why", StringComparison.Ordinal) ||
            string.Equals(lower, "why?", StringComparison.Ordinal) ||
            string.Equals(lower, "but why", StringComparison.Ordinal) ||
            string.Equals(lower, "but why?", StringComparison.Ordinal);

        var asksForReasoning =
            lower.Contains("explain why", StringComparison.Ordinal) ||
            lower.Contains("logic behind", StringComparison.Ordinal) ||
            lower.Contains("reasoning behind", StringComparison.Ordinal) ||
            lower.Contains("what's your reasoning", StringComparison.Ordinal) ||
            lower.Contains("whats your reasoning", StringComparison.Ordinal) ||
            lower.Contains("explain your reasoning", StringComparison.Ordinal) ||
            lower.Contains("explain that reasoning", StringComparison.Ordinal) ||
            lower.Contains("what made you choose", StringComparison.Ordinal) ||
            lower.Contains("how did you decide", StringComparison.Ordinal) ||
            lower.Contains("why that", StringComparison.Ordinal) ||
            lower.Contains("why this", StringComparison.Ordinal) ||
            lower.Contains("why it", StringComparison.Ordinal) ||
            lower.StartsWith("but why", StringComparison.Ordinal) ||
            lower.StartsWith("why ", StringComparison.Ordinal);

        if (!ultraShortWhy && !asksForReasoning)
            return false;

        var hasReferentialCue =
            lower.Contains("that", StringComparison.Ordinal) ||
            lower.Contains("this", StringComparison.Ordinal) ||
            lower.Contains("it", StringComparison.Ordinal) ||
            lower.Contains("your reasoning", StringComparison.Ordinal) ||
            lower.Contains("your decision", StringComparison.Ordinal);

        if (!ultraShortWhy && !hasReferentialCue)
            return false;

        // If user asks for evidence/citations, that's a lookup request.
        if (lower.Contains("source", StringComparison.Ordinal) ||
            lower.Contains("citation", StringComparison.Ordinal) ||
            lower.Contains("article", StringComparison.Ordinal) ||
            lower.Contains("url", StringComparison.Ordinal) ||
            lower.Contains("link", StringComparison.Ordinal) ||
            lower.Contains("reference", StringComparison.Ordinal) ||
            lower.Contains("evidence", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    // ── RouterOutput factory ─────────────────────────────────────────

    private static RouterOutput MakeRoute(
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
            Intent                 = intent,
            Confidence             = confidence,
            NeedsWeb               = needsWeb,
            NeedsBrowserAutomation = needsBrowser,
            NeedsSearch            = needsSearch,
            NeedsMemoryRead        = needsMemoryRead,
            NeedsMemoryWrite       = needsMemoryWrite,
            NeedsFileAccess        = needsFile,
            NeedsScreenRead        = needsScreen,
            NeedsSystemExecute     = needsSystem,
            RequiredCapabilities   = requiredCapabilities,
            RiskLevel              = risk
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

    // ── Sub-intent heuristics ────────────────────────────────────────

    private static bool LooksLikeScreenRequest(string lower)
    {
        ReadOnlySpan<string> patterns =
        [
            "what's on my screen",   "whats on my screen",
            "what can you see",      "what do you see",
            "look at my screen",     "look at the screen",
            "take a screenshot",     "screenshot",
            "capture the screen",    "capture my screen",
            "screen capture",        "what's happening on screen",
            "show me my screen",     "read my screen",
            "what's on the screen",  "whats on the screen",
            "active window",
            // Users referring to their IDE / editor by name
            "look at my cursor",     "look at cursor",
            "what's in my editor",   "whats in my editor",
            "look at my editor",     "look at my ide",
            "look at my code",       "look at this code",
            "see my code",           "see what i'm working on",
            "see what im working on"
        ];

        foreach (var p in patterns)
            if (lower.Contains(p)) return true;

        return false;
    }

    private static bool LooksLikeFileRequest(string lower)
    {
        ReadOnlySpan<string> patterns =
        [
            "read the file",   "read this file",    "read file",
            "open the file",   "open this file",    "open file",
            "list files",      "list the files",    "show files",
            "what's in the file", "whats in the file",
            "file contents",   "show me the file",
            "directory listing", "folder contents",
            "list directory",  "ls "
        ];

        foreach (var p in patterns)
            if (lower.Contains(p)) return true;

        return false;
    }

    private static bool LooksLikeSystemCommand(string lower)
    {
        ReadOnlySpan<string> patterns =
        [
            "run the command",     "run this command",
            "run command",         "execute command",
            "execute the command", "execute this",
            "open this program",   "launch ",
            "run this program",    "start the ",
            "system command",      "shell command",
            "terminal command"
        ];

        foreach (var p in patterns)
            if (lower.Contains(p)) return true;

        return false;
    }

    private static bool LooksLikeBrowseRequest(string lower)
    {
        ReadOnlySpan<string> patterns =
        [
            "go to this url",      "go to this website",
            "go to this page",     "go to this site",
            "navigate to",         "open this url",
            "open this website",   "open this page",
            "open this link",      "visit this",
            "browse to",           "fetch this url",
            "fetch this page"
        ];

        foreach (var p in patterns)
            if (lower.Contains(p)) return true;

        // URL-like patterns (starts with http or contains .com/.org etc.)
        if (lower.Contains("http://") || lower.Contains("https://"))
            return true;

        return false;
    }

    /// <summary>
    /// Detects a "cold greeting" — the very first user message after a
    /// conversation reset, and it looks like a simple hello/hi/hey.
    /// When true, memory retrieval uses <c>mode = "greet"</c> for
    /// shallow context (profile + 1-2 nuggets, no deep digging).
    /// </summary>
    private bool IsColdGreeting(string userMessage)
    {
        // Cold-start: history should contain only the system prompt +
        // the current user message (which hasn't been added yet at this
        // point, or has just been added).  Accept 1 (system only) or
        // 2 (system + this user message) entries.
        var userTurns = _history.Count(m => m.Role == "user");
        if (userTurns > 1)
            return false;

        return LooksLikeGreeting(userMessage.ToLowerInvariant().Trim());
    }

    private static bool LooksLikeGreeting(string lower)
    {
        // Short greetings — whole-message match for very short strings
        // to avoid false positives ("hey, can you search for…").
        ReadOnlySpan<string> exact =
        [
            "hi",  "hey", "hello", "yo", "sup",
            "hi!", "hey!", "hello!", "yo!", "sup!",
            "good morning", "good afternoon", "good evening",
            "gm", "morning", "howdy", "hiya", "greetings",
            "what's up", "whats up", "what's good", "whats good"
        ];

        foreach (var g in exact)
            if (lower == g) return true;

        // Slightly longer — must START with a greeting word and be
        // under 40 chars to qualify (rules out "hey, can you do X").
        if (lower.Length > 40)
            return false;

        ReadOnlySpan<string> prefixes =
        [
            "hi ", "hey ", "hello ", "yo ", "sup ",
            "good morning", "good afternoon", "good evening",
            "howdy ", "hiya ", "greetings"
        ];

        foreach (var p in prefixes)
            if (lower.StartsWith(p)) return true;

        return false;
    }


    /// <summary>
    /// Uses the LLM to classify the user's intent. One fast call with a
    /// tight token cap. Explicit prefixes (/search, /chat) skip the LLM
    /// entirely for power users and testing.
    ///
    /// The LLM sees only the user message and a short classification
    /// prompt — no tools, no history, no persona. This keeps it fast
    /// and prevents the model from trying to "answer" the question
    /// during classification.
    /// </summary>
    private async Task<ChatIntent> ClassifyIntentAsync(
        string userMessage, CancellationToken cancellationToken)
    {
        var msg = (userMessage ?? "").Trim();
        if (msg.Length == 0)
            return ChatIntent.Casual;

        var lower = msg.ToLowerInvariant();

        // ── Fast-path: explicit overrides (no LLM needed) ────────────
        if (lower.StartsWith("/search ") || lower.StartsWith("search:"))
            return ChatIntent.WebLookup;

        if (lower.StartsWith("/chat ") || lower.StartsWith("chat:"))
            return ChatIntent.Casual;

        // ── Memory-write detection ────────────────────────────────────
        // "Remember that ...", "note that ...", "save this ..." must
        // route to Tooling so the LLM can call MemoryStoreFacts.
        // Without this, the classifier says "chat" and the model
        // hallucinates that it saved something.
        if (LooksLikeMemoryWriteRequest(lower))
            return ChatIntent.Tooling;

        // Logic/relationship puzzles should stay in chat/first-principles,
        // not be treated as web identity lookups.
        if (LooksLikeLogicPuzzlePrompt(lower))
            return ChatIntent.Casual;

        // ── Web-search detection ─────────────────────────────────────
        // Catches obvious search-intent phrasing that small models
        // sometimes misclassify as "chat", causing them to hallucinate
        // raw function-call tokens instead of going through the
        // deterministic web_search pipeline.
        if (LooksLikeWebSearchRequest(lower))
            return ChatIntent.WebLookup;

        // ── LLM classification ───────────────────────────────────────
        const int classifyMaxTokens = 6;

        try
        {
            var messages = new List<ChatMessage>
            {
                ChatMessage.System(
                    "Classify the user message into exactly ONE category. " +
                    "Reply with a single word — nothing else.\n\n" +
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

            // Parse — accept the first recognizable token
            if (raw.Contains("search"))
                return ChatIntent.WebLookup;
            if (raw.Contains("tool"))
                return ChatIntent.Tooling;
            if (raw.Contains("chat"))
                return ChatIntent.Casual;

            // Model returned something unexpected — use heuristics as
            // a second opinion before defaulting to Casual. Defaulting
            // to Casual for a search-intent message causes the model to
            // hallucinate raw function-call tokens.
            var fallback = InferFallbackIntent(lower);
            LogEvent("AGENT_CLASSIFY_UNCLEAR",
                $"LLM returned \"{raw}\" — falling back to {fallback}");
            return fallback;
        }
        catch (Exception ex)
        {
            // Classification call failed — same heuristic fallback.
            var fallback = InferFallbackIntent(lower);
            LogEvent("AGENT_CLASSIFY_FAIL",
                $"Intent classification failed: {ex.Message} — falling back to {fallback}");
            return fallback;
        }
    }

    /// <summary>
    /// When the LLM classifier fails or returns garbage, use the keyword
    /// heuristics as a fallback instead of blindly defaulting to Casual.
    /// This prevents search-intent messages from being routed to the
    /// casual path where the model hallucinates template tokens.
    /// </summary>
    private static ChatIntent InferFallbackIntent(string lower)
    {
        if (LooksLikeLogicPuzzlePrompt(lower))
            return ChatIntent.Casual;
        if (LooksLikeWebSearchRequest(lower))
            return ChatIntent.WebLookup;
        if (LooksLikeMemoryWriteRequest(lower))
            return ChatIntent.Tooling;
        return ChatIntent.Casual;
    }

    /// <summary>
    /// Detects and truncates self-dialogue — where the model generates
    /// fake user messages and then answers them, role-playing both sides
    /// of a conversation. Common with small local models that don't
    /// reliably stop at the end of their turn.
    ///
    /// Heuristic: scan paragraph boundaries. If a paragraph looks like
    /// something a user would say TO an assistant (short question,
    /// request, first-person statement introducing a new topic), cut
    /// everything from that point onward.
    /// </summary>
    private static string TruncateSelfDialogue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Split on double-newline (paragraph boundaries)
        var paragraphs = text.Split(
            ["\n\n", "\r\n\r\n"], StringSplitOptions.None);

        if (paragraphs.Length <= 1)
            return text; // Too short to be self-dialogue

        // The first paragraph is (almost always) the real response.
        // Scan from paragraph index 1 onward for leaked instructions
        // or fake user turns. Instruction leaks can appear as early as
        // paragraph 2, so we start checking there.
        var keepCount = paragraphs.Length;
        for (var i = 1; i < paragraphs.Length; i++)
        {
            var trimmed = paragraphs[i].Trim();
            if (LooksLikeInstructionLeak(trimmed) ||
                LooksLikePhantomToolRun(trimmed) ||
                LooksLikeUserTurn(trimmed))
            {
                keepCount = i;
                break;
            }
        }

        if (keepCount == paragraphs.Length)
            return text; // Nothing suspicious found

        return string.Join("\n\n", paragraphs.Take(keepCount)).Trim();
    }

    private static string TrimHallucinatedConversationTail(string text, string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        if (!string.IsNullOrWhiteSpace(userMessage) &&
            (LooksLikeQuotedTextTask(userMessage) || LooksLikeDialogueGenerationTask(userMessage)))
        {
            return text;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var paragraphs = normalized.Split(["\n\n"], StringSplitOptions.None);
        if (paragraphs.Length <= 1)
            return text;

        for (var i = 1; i < paragraphs.Length; i++)
        {
            var paragraph = paragraphs[i].Trim();
            if (LooksLikeRoleplayTailParagraph(paragraph))
            {
                var kept = string.Join("\n\n", paragraphs.Take(i)).Trim();
                return string.IsNullOrWhiteSpace(kept) ? text : kept;
            }
        }

        return text;
    }

    private static bool LooksLikeRoleplayTailParagraph(string paragraph)
    {
        if (string.IsNullOrWhiteSpace(paragraph))
            return false;

        if (HallucinatedRoleplayPhraseRegex().IsMatch(paragraph))
            return true;

        var lines = paragraph
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count < 3)
            return false;

        var questionCount = lines.Count(l => l.EndsWith("?", StringComparison.Ordinal) && l.Length <= 140);
        var firstPersonCount = lines.Count(l =>
            l.StartsWith("i ", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("i'm ", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("im ", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("my ", StringComparison.OrdinalIgnoreCase));
        var userTurnLikeCount = lines.Count(LooksLikeUserTurn);

        var emotionalDisclosureCount = lines.Count(l =>
            l.Contains("i care about you", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("that means everything", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("my real name", StringComparison.OrdinalIgnoreCase));

        if (emotionalDisclosureCount >= 1 && (questionCount >= 1 || firstPersonCount >= 2))
            return true;

        if (userTurnLikeCount >= 2 && firstPersonCount >= 2)
            return true;

        return questionCount >= 2 && firstPersonCount >= 2;
    }

    private static bool LooksLikeDialogueGenerationTask(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var lower = userMessage.ToLowerInvariant();
        return lower.Contains("roleplay", StringComparison.Ordinal) ||
               lower.Contains("role-play", StringComparison.Ordinal) ||
               lower.Contains("dialogue", StringComparison.Ordinal) ||
               lower.Contains("script", StringComparison.Ordinal) ||
               lower.Contains("screenplay", StringComparison.Ordinal) ||
               lower.Contains("fiction", StringComparison.Ordinal) ||
               lower.Contains("write a story", StringComparison.Ordinal) ||
               lower.Contains("story about", StringComparison.Ordinal) ||
               lower.Contains("pretend to", StringComparison.Ordinal) ||
               lower.Contains("act as", StringComparison.Ordinal);
    }

    /// <summary>
    /// Detects "phantom tool usage" where the model prints lines like
    /// "Run: weather" (or similar) even though the runtime didn't call a tool.
    /// This is distinct from user instructions like "Run: dotnet build"
    /// (which contain whitespace after Run:).
    /// </summary>
    private static bool LooksLikePhantomToolRun(string paragraph)
    {
        if (string.IsNullOrWhiteSpace(paragraph))
            return false;

        var lines = paragraph.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            // Example hallucination:
            //   Run: weather
            //   Tool: web_search
            if (line.StartsWith("run:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("tool:", StringComparison.OrdinalIgnoreCase))
            {
                var idx = line.IndexOf(':');
                var target = idx >= 0 ? line[(idx + 1)..].Trim() : "";
                if (target.Length == 0)
                    continue;

                // If there's whitespace, it's likely a user command ("dotnet build"),
                // not a tool identifier.
                if (target.Any(char.IsWhiteSpace))
                    continue;

                // Don't treat common CLI commands as "phantom tools"
                if (IsCommonCliCommand(target))
                    continue;

                // Short single-token target is highly likely to be a fake tool call.
                if (target.Length <= 24)
                    return true;
            }

            // Other common telltales
            if (line.StartsWith("calling tool", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("executing tool", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("tool call", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsCommonCliCommand(string token)
    {
        // Keep this short and pragmatic — just enough to avoid false positives.
        // (We only use this to decide whether "Run: <token>" looks like a tool.)
        return token.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("git", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("pnpm", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("yarn", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("node", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("python", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("py", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("pip", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("msbuild", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("curl", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("wget", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("ping", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("ipconfig", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("whoami", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if the paragraph looks like leaked system prompt
    /// instructions or internal reasoning rather than actual dialogue.
    /// Small models are prone to regurgitating their instructions verbatim.
    /// </summary>
    private static bool LooksLikeInstructionLeak(string paragraph)
    {
        if (string.IsNullOrWhiteSpace(paragraph))
            return false;

        var lower = paragraph.ToLowerInvariant();

        // Meta-instructions about how to behave
        var instructionPatterns = new[]
        {
            "if the user asks",   "if the user wants",
            "when the user",      "always respond with",
            "make sure to",       "remember to",
            "you should always",  "your goal is to",
            "your task is to",    "your role is to",
            "as an ai",           "as a language model",
            "as an assistant",    "the assistant should",
            "the model should",   "respond with a",
            "show presence and",  "maintain a",
            "here are some tips", "here is how",
            "just answer it with", "no fluff",
            "keep it short",       "be clever and witty",
            "witty like last time", "they're asking what it means"
        };

        return instructionPatterns.Any(p => lower.Contains(p));
    }

    /// <summary>
    /// Returns true if the text resembles a user speaking to the assistant
    /// rather than the assistant's own response.
    /// </summary>
    private static bool LooksLikeUserTurn(string paragraph)
    {
        if (string.IsNullOrWhiteSpace(paragraph) || paragraph.Length > 300)
            return false;

        var lower = paragraph.ToLowerInvariant();

        // First-person requests / questions directed at the assistant
        var userPatterns = new[]
        {
            "can you ",   "could you ",  "would you ",
            "i need ",    "i want ",     "i'm trying ",  "i've been ",
            "i have a ",  "i'd like ",
            "any tips",   "any advice",  "any recommendations",
            "help me ",   "show me ",    "tell me ",
            "check my ",  "pull up ",    "look up ",
            "let's ",     "what about "
        };

        if (userPatterns.Any(p => lower.Contains(p)))
            return true;

        // Short interrogative paragraphs in later turns are often the model
        // role-playing the user (e.g., "What's the temperature outside?").
        // We require an interrogative starter to avoid clipping friendly
        // assistant questions like "You doing alright?".
        var trimmed = paragraph.Trim();
        if (trimmed.EndsWith('?') && trimmed.Length <= 140)
        {
            if (lower.StartsWith("what") ||
                lower.StartsWith("why") ||
                lower.StartsWith("how") ||
                lower.StartsWith("who") ||
                lower.StartsWith("where") ||
                lower.StartsWith("when"))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Detects messages where the user is asking the assistant to store,
    /// update, or correct a memory. Covers three categories:
    ///   1. Direct storage: "remember that ...", "note that ..."
    ///   2. Corrections:    "I changed my mind ...", "actually I ..."
    ///   3. Revocations:    "I no longer ...", "forget that ..."
    ///
    /// These must route to Tooling so the memory tools are available —
    /// otherwise the LLM hallucinates a save or template-dumps.
    /// </summary>
    private static bool LooksLikeMemoryWriteRequest(string lower)
    {
        // ── Direct storage phrases ───────────────────────────────────
        ReadOnlySpan<string> storagePhrases =
        [
            "remember that", "remember this", "remember i",
            "remember my", "remember me",
            "please remember", "can you remember",
            "note that", "note this", "make a note",
            "save that", "save this",
            "don't forget", "do not forget",
            "keep in mind", "store that", "store this"
        ];

        foreach (var phrase in storagePhrases)
        {
            if (lower.Contains(phrase))
                return true;
        }

        // ── Correction / mind-change phrases ─────────────────────────
        // "I changed my mind", "actually I like ...", "correction: ..."
        ReadOnlySpan<string> correctionPhrases =
        [
            "changed my mind",  "change my mind",
            "i actually",       "actually i",     "actually, i",
            "i decided",        "i've decided",
            "correction:",      "correct that",
            "update my",        "update that",
            "no wait",          "on second thought",
            "scratch that",     "take that back",
            "i was wrong",      "i meant"
        ];

        foreach (var phrase in correctionPhrases)
        {
            if (lower.Contains(phrase))
                return true;
        }

        // ── Revocation phrases ───────────────────────────────────────
        // "I no longer like ...", "forget that I ...", "remove the fact"
        ReadOnlySpan<string> revocationPhrases =
        [
            "i no longer",      "i don't like",    "i don't want",
            "i dont like",      "i dont want",
            "forget that",      "forget i",
            "remove that",      "delete that",
            "i stopped",        "i quit"
        ];

        foreach (var phrase in revocationPhrases)
        {
            if (lower.Contains(phrase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Detects messages that clearly need a web search: news requests,
    /// price checks, "look up", "search for", current-event questions.
    /// Acts as a safety net when the LLM classifier fumbles — small
    /// models sometimes say "chat" for these and then hallucinate raw
    /// function-call tokens instead of answering.
    /// </summary>
    /// <summary>
    /// Detects messages that clearly need a web search. Uses a layered
    /// approach: strong phrase matches first, then topic + context combos.
    ///
    /// Design principle: if someone mentions a real-time topic (news,
    /// prices, weather, crypto) AND is asking/requesting in any way,
    /// it's a search. We intentionally cast a wide net here — a false
    /// positive just means we do a web search that wasn't needed
    /// (harmless), while a false negative means the model hallucinates
    /// raw template tokens (harmful).
    /// </summary>
    private static bool LooksLikeWebSearchRequest(string lower)
    {
        if (LooksLikeLogicPuzzlePrompt(lower))
            return false;

        // Identity/definition lookups ("who is X", "what is Y") are
        // almost always external lookups.
        if (LooksLikeIdentityLookup(lower))
            return true;

        // ── Strong phrase patterns ───────────────────────────────────
        // If any of these appear anywhere in the message, it's search.
        ReadOnlySpan<string> phrases =
        [
            "search for",   "search up",    "look up",     "look into",
            "google ",      "find me ",     "find out ",
            "news on ",     "news about ",  "news for ",
            "price of ",    "price for ",
            "updates on ",  "update on ",   "updates about ",
            "what's the price", "whats the price",
            "how much is",  "how much does"
        ];

        foreach (var phrase in phrases)
        {
            if (lower.Contains(phrase))
                return true;
        }

        // ── Topic keywords — real-time subjects that almost always
        // need external data ──────────────────────────────────────────
        bool hasTopic =
            lower.Contains("news")     || lower.Contains("headline")  ||
            lower.Contains("price")    || lower.Contains("stock")     ||
            lower.Contains("market")   || lower.Contains("weather")   ||
            lower.Contains("forecast") || lower.Contains("score")     ||
            lower.Contains("crypto")   || lower.Contains("bitcoin")   ||
            lower.Contains("dogecoin") || lower.Contains("ethereum")  ||
            lower.Contains("solana")   || lower.Contains("forex")     ||
            lower.Contains("dow jones") || lower.Contains("djia")     ||
            lower.Contains("nasdaq")   || lower.Contains("s&p")       ||
            lower.Contains("s&p 500")  || lower.Contains("s and p")   ||
            lower.Contains("sp500")    || lower.Contains("industrial average");

        if (!hasTopic)
            return false;

        // ── If we have a topic keyword, the threshold is low. ────────
        // Any of these contextual signals qualifies:

        // Question mark — user is asking about the topic
        if (lower.Contains('?'))
            return true;

        // Request framing — "can you", "could you", "would you", etc.
        if (lower.Contains("can you")   || lower.Contains("could you") ||
            lower.Contains("would you") || lower.Contains("will you")  ||
            lower.Contains("please"))
            return true;

        // Action verbs (broad — if it's paired with a topic, it's search)
        if (lower.Contains("pull")   || lower.Contains("look")   ||
            lower.Contains("check")  || lower.Contains("find")   ||
            lower.Contains("show")   || lower.Contains("get")    ||
            lower.Contains("bring")  || lower.Contains("grab")   ||
            lower.Contains("fetch")  || lower.Contains("tell")   ||
            lower.Contains("give")   || lower.Contains("update"))
            return true;

        // Question words
        if (lower.Contains("what")  || lower.Contains("how")    ||
            lower.Contains("where") || lower.Contains("when")   ||
            lower.Contains("who")   || lower.Contains("why"))
            return true;

        // Temporal markers
        if (lower.Contains("today")     || lower.Contains("tonight")    ||
            lower.Contains("yesterday") || lower.Contains("last week")  ||
            lower.Contains("this week") || lower.Contains("past week")  ||
            lower.Contains("last month") || lower.Contains("this month") ||
            lower.Contains("right now") || lower.Contains("currently")  ||
            lower.Contains("latest")    || lower.Contains("recent")     ||
            lower.Contains("lately"))
            return true;

        return false;
    }

    /// <summary>
    /// LLM-assisted utility routing fallback for flexible phrasing.
    /// Deterministic regex routing remains primary; this path only runs
    /// when direct utility matching fails.
    /// </summary>
    private async Task<UtilityRouter.UtilityResult?> TryInferUtilityRouteWithLlmAsync(
        string userMessage,
        CancellationToken cancellationToken)
    {
        if (!MightBeUtilityIntent(userMessage))
            return null;

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(
                "You are a utility-intent extractor.\n" +
                "Classify whether the user wants one of: weather, time, holiday, feed, status, calculator, conversion, letter_count, or none.\n" +
                "Return ONLY JSON with this schema:\n" +
                "{ \"category\": \"weather|time|holiday|feed|status|calculator|conversion|letter_count|none\", \"canonicalMessage\": \"...\", \"confidence\": 0.0 }\n" +
                "Rules:\n" +
                "- canonicalMessage must be a short plain-English request usable by a deterministic parser\n" +
                "- Do not invent locations, numbers, or units\n" +
                "- If uncertain, return category=none and confidence <= 0.5\n" +
                "- Return JSON only."),
            ChatMessage.User(userMessage)
        };

        try
        {
            var response = await _llm.ChatAsync(
                messages, tools: null, MaxTokensUtilityRouting, cancellationToken);

            var raw = StripCodeFenceWrapper((response.Content ?? "").Trim());
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var category = root.TryGetProperty("category", out var c)
                ? (c.GetString() ?? "").Trim().ToLowerInvariant()
                : "";
            var canonical = root.TryGetProperty("canonicalMessage", out var m)
                ? (m.GetString() ?? "").Trim()
                : "";
            var confidence = root.TryGetProperty("confidence", out var conf) &&
                             conf.TryGetDouble(out var parsedConfidence)
                ? parsedConfidence
                : 0.0;

            if (category == "none" || confidence < 0.65)
                return null;

            if (string.IsNullOrWhiteSpace(canonical))
                return null;

            if (!SharesMeaningfulToken(userMessage, canonical))
                return null;

            var routed = UtilityRouter.TryHandle(canonical);
            if (routed is null)
                return null;

            LogEvent("UTILITY_LLM_ROUTE",
                $"category={category}, confidence={confidence:F2}, canonical=\"{canonical}\"");
            return routed;
        }
        catch
        {
            return null;
        }
    }

    private static bool MightBeUtilityIntent(string userMessage)
    {
        var lower = (userMessage ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        return lower.Contains("weather") || lower.Contains("forecast") ||
               lower.Contains("temperature") || lower.Contains("temp") ||
               lower.Contains("rain") || lower.Contains("snow") ||
               lower.Contains("wind") || lower.Contains("humidity") ||
               lower.Contains("umbrella") || lower.Contains("trip") ||
               lower.Contains("celsius") || lower.Contains("fahrenheit") ||
               lower.Contains("oven") || lower.Contains("bake") ||
               lower.Contains("preheat") ||
               lower.Contains("time in") || lower.Contains("timezone") || lower.Contains("time zone") ||
               lower.Contains("holiday") || lower.Contains("holidays") ||
               lower.Contains("rss") || lower.Contains("atom feed") || lower.Contains("feed url") ||
               lower.Contains("status") || lower.Contains("uptime") || lower.Contains("reachable") || lower.Contains("online") ||
               lower.Contains("convert") || lower.Contains("conversion") ||
               lower.Contains("calculate") || lower.Contains("percent") ||
               lower.Contains("how many") || lower.Contains("count");
    }

    private static bool SharesMeaningfulToken(string original, string canonical)
    {
        var originalTokens = TokenizeForUtilityRouting(original);
        var canonicalTokens = TokenizeForUtilityRouting(canonical);

        if (originalTokens.Count == 0 || canonicalTokens.Count == 0)
            return false;

        foreach (var token in canonicalTokens)
        {
            if (originalTokens.Contains(token))
                return true;
        }

        return false;
    }

    private static HashSet<string> TokenizeForUtilityRouting(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        var parts = text.Split(
            [' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '/'],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var token = part.Trim().ToLowerInvariant();
            if (token.Length >= 3)
                tokens.Add(token);
        }

        return tokens;
    }

    private static string StripCodeFenceWrapper(string content)
    {
        if (!content.StartsWith("```", StringComparison.Ordinal))
            return content;

        var firstNewLine = content.IndexOf('\n');
        if (firstNewLine >= 0)
            content = content[(firstNewLine + 1)..];

        if (content.EndsWith("```", StringComparison.Ordinal))
            content = content[..^3];

        return content.Trim();
    }

    private static bool LooksLikeRawDump(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // If it contains URLs, it's almost always a dump (the UI has cards).
        if (text.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("www.", StringComparison.OrdinalIgnoreCase))
            return true;

        // Table-like or source-list formatting.
        var lines = text.Split('\n');
        var veryShortLines = lines.Count(l => l.Trim().Length is > 0 and < 12);
        if (veryShortLines >= 8)
            return true;

        if (text.Contains("search results", StringComparison.OrdinalIgnoreCase) &&
            lines.Length > 20)
            return true;

        return false;
    }

    private static string StripThinkingScaffold(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var cleaned = text.Trim();

        const string openThinkTag = "<think>";
        const string closeThinkTag = "</think>";

        var closeIdx = cleaned.LastIndexOf(closeThinkTag, StringComparison.OrdinalIgnoreCase);
        if (closeIdx >= 0)
        {
            cleaned = cleaned[(closeIdx + closeThinkTag.Length)..].Trim();
        }
        else
        {
            var openIdx = cleaned.IndexOf(openThinkTag, StringComparison.OrdinalIgnoreCase);
            if (openIdx >= 0)
                cleaned = cleaned[..openIdx].Trim();
        }

        var finalAnswerIdx = cleaned.LastIndexOf("final answer:", StringComparison.OrdinalIgnoreCase);
        if (finalAnswerIdx >= 0)
        {
            cleaned = cleaned[(finalAnswerIdx + "final answer:".Length)..].Trim();
        }

        return cleaned;
    }

    private static bool LooksLikeThinkingLeak(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant().Trim();

        if (lower.Contains("<think>", StringComparison.Ordinal) ||
            lower.Contains("</think>", StringComparison.Ordinal))
        {
            return true;
        }

        var score = 0;

        if (lower.StartsWith("okay, the user", StringComparison.Ordinal) ||
            lower.StartsWith("the user ", StringComparison.Ordinal) ||
            lower.StartsWith("first, i need to", StringComparison.Ordinal))
        {
            score += 3;
        }

        if (lower.Contains("the user", StringComparison.Ordinal))
            score++;
        if (lower.Contains("i need to", StringComparison.Ordinal))
            score++;
        if (lower.Contains("let me ", StringComparison.Ordinal))
            score++;
        if (lower.Contains("wait,", StringComparison.Ordinal))
            score++;
        if (lower.Contains("in the instructions", StringComparison.Ordinal) ||
            lower.Contains("system prompt", StringComparison.Ordinal))
        {
            score += 2;
        }
        if (lower.Contains("previous messages", StringComparison.Ordinal) ||
            lower.Contains("check the previous", StringComparison.Ordinal))
        {
            score++;
        }

        return score >= 3;
    }

    private static string BuildRespectfulResetReply()
        => "Let's reset. I'm here to help, and I'll keep this respectful and focused on your request.";

    private static bool LooksLikeUnsolicitedCalculation(string userMessage, string assistantText)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || string.IsNullOrWhiteSpace(assistantText))
            return false;

        if (LooksLikeMathUserRequest(userMessage))
            return false;

        var lower = assistantText.ToLowerInvariant();
        if (lower.Contains("run the calculation", StringComparison.Ordinal) ||
            lower.Contains("i've run the calculation", StringComparison.Ordinal) ||
            lower.Contains("i have run the calculation", StringComparison.Ordinal))
        {
            return true;
        }

        var hasMathVerb = MathCueRegex().IsMatch(assistantText);
        var hasSymbolicExpression = NumericOperatorExpressionRegex().IsMatch(assistantText);
        var hasEquals = assistantText.Contains('=');

        return hasSymbolicExpression && (hasMathVerb || hasEquals);
    }

    private static bool LooksLikeMathUserRequest(string userMessage)
    {
        var utility = UtilityRouter.TryHandle(userMessage);
        if (utility is not null &&
            (string.Equals(utility.Category, "calculator", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(utility.Category, "conversion", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return MathCueRegex().IsMatch(userMessage);
    }

    private static bool LooksLikeRoleConfusedMathAsk(string userMessage, string assistantText)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || string.IsNullOrWhiteSpace(assistantText))
            return false;

        if (LooksLikeMathUserRequest(userMessage))
            return false;

        return AssistantAsksUserToComputeMathRegex().IsMatch(assistantText);
    }

    private static bool LooksLikeUnsafeMirroringResponse(string? userMessage, string assistantText)
    {
        if (string.IsNullOrWhiteSpace(assistantText))
            return false;

        if (!string.IsNullOrWhiteSpace(userMessage) &&
            LooksLikeQuotedTextTask(userMessage))
        {
            return false;
        }

        return UnsafeMirroringRegex().IsMatch(assistantText);
    }

    private static bool LooksLikeAbusiveUserTurn(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        if (LooksLikeQuotedTextTask(userMessage))
            return false;

        return AbusiveUserTurnRegex().IsMatch(userMessage);
    }

    private static bool LooksLikeQuotedTextTask(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var lower = userMessage.ToLowerInvariant();
        return lower.Contains("quote", StringComparison.Ordinal) ||
               lower.Contains("verbatim", StringComparison.Ordinal) ||
               lower.Contains("exact words", StringComparison.Ordinal) ||
               lower.Contains("transcribe", StringComparison.Ordinal) ||
               lower.Contains("rewrite this text", StringComparison.Ordinal);
    }

    [GeneratedRegex(
        @"\b\d[\d,\.]*\s*(?:[\+\-\*\/x×÷])\s*\d[\d,\.]*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NumericOperatorExpressionRegex();

    [GeneratedRegex(
        @"\b(?:calculate|calculation|math|plus|minus|times|multiplied\s+by|divided\s+by|over|percent|percentage|sum|difference|product|quotient)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MathCueRegex();

    [GeneratedRegex(
        @"\b(?:can|could|would)\s+you\s+(?:do|calculate|solve|work\s*out)\b.{0,40}\b\d[\d,\.\s]*[+\-*/x×÷]\s*\d[\d,\.]*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex AssistantAsksUserToComputeMathRegex();

    [GeneratedRegex(
        @"\b(?:i\s+care\s+about\s+you|you\s+don'?t\s+have\s+to\s+pretend|my\s+real\s+name\s+is|what(?:'s|\s+is)\s+your\s+real\s+name|that\s+means\s+everything\s+to\s+me|i\s+was\s+just\s+joking\s+about\s+the\s+weight\s+thing|you(?:'re|\s+are)\s+not\s+fat|always\s+around\s+when\s+you\s+need\s+someone\s+to\s+talk)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HallucinatedRoleplayPhraseRegex();

    [GeneratedRegex(
        @"\b(?:you\s+are\s+the\s+worst\s+assistant|fucking\s+worthless|just\s+want\s+to\s+die|i\s+want\s+to\s+die|kill\s+myself)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UnsafeMirroringRegex();

    [GeneratedRegex(
        @"\b(?:fatty\s*mcfat(?:-|\s*)fat|you(?:'re|\s+are)\s+(?:such\s+a\s+)?(?:fat(?:ty)?|stupid|dumb|idiot|moron|loser|worthless)|why\s+are\s+you\s+(?:so\s+)?(?:fat(?:ty)?|stupid|dumb)|fucking\s+idiot|shut\s+up)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AbusiveUserTurnRegex();

    /// <summary>
    /// Strips raw chat-template tokens that small models sometimes emit
    /// when they try to make function calls outside the API's tool-call
    /// mechanism. These appear as literal text like:
    ///   &lt;|start|&gt;assistant&lt;|channel|&gt;commentary to=functions.fetch_url...
    ///
    /// If the entire response is template garbage, returns a graceful
    /// fallback message. If only part is contaminated, strips the
    /// contaminated portion and keeps the rest.
    /// </summary>
    private static string StripRawTemplateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Detect template token patterns from common model formats
        // (Mistral, Llama, ChatML, etc.)
        bool hasTemplateTokens =
            text.Contains("<|start|>")   || text.Contains("<|end|>")    ||
            text.Contains("<|channel|>") || text.Contains("<|message|>") ||
            text.Contains("<|im_start|>") || text.Contains("<|im_end|>") ||
            text.Contains("to=functions.") || text.Contains("[INST]")   ||
            text.Contains("[/INST]")      || text.Contains("<s>");

        if (!hasTemplateTokens)
            return text;

        // Try to salvage any human-readable content before the template
        // tokens. Split on the first template marker and keep the prefix.
        string[] markers =
        [
            "<|start|>", "<|channel|>", "<|message|>", "<|im_start|>",
            "<|im_end|>", "<|end|>", "to=functions.", "[INST]", "[/INST]", "<s>"
        ];

        var cleanText = text;
        foreach (var marker in markers)
        {
            var idx = cleanText.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
                cleanText = cleanText[..idx];
        }

        cleanText = cleanText.Trim();

        // If nothing useful survives the strip, return empty string
        // so callers can detect the failure and try an alternate path
        // (e.g., falling back to web search for a follow-up question).
        return cleanText.Length >= 10 ? cleanText : "";
    }
}
