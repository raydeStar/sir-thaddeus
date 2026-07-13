using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.Validation;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine;
using Thaddeus.Runtime.Chat.Pipeline;
using Thaddeus.Runtime.Tools;
using Thaddeus.SharedTypes;
using RuntimeChatMessage = Thaddeus.SharedTypes.ChatMessage;
using LlmChatMessage = SirThaddeus.LlmClient.ChatMessage;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Tool-capable assistant. Hits an OpenAI-compatible chat endpoint via
/// <see cref="ILlmClient"/>, handing it the list of MCP tools the server
/// exposes. When the model decides to call tools, the loop executes them
/// via <see cref="IMcpToolClient"/>, appends the results to the history,
/// and re-queries — until the model produces a final text answer or the
/// round-trip cap is reached. The final text is chunked into word-sized
/// deltas so the UI streams identically regardless of whether tools fired.
/// </summary>
public sealed class LmStudioAssistant : IAssistant
{
    private readonly ILlmClient _llm;
    private readonly IMcpToolClient _mcp;
    private readonly ToolPermissionGate _gate;
    private readonly IThreadStore _store;
    private readonly ChatTurnPublisher _publisher;
    private readonly IAuditLogger _audit;
    private readonly ILogger<LmStudioAssistant> _logger;

    /// <summary>Delay between streamed deltas. Tests override to zero.</summary>
    public TimeSpan DeltaDelay { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Optional footman (gatekeeper) router. When set, each turn starts by
    /// asking the small gatekeeper model to classify the user's intent into
    /// an <see cref="AgentState"/>. The deterministic
    /// <see cref="ToolFamilyPolicy"/> mapping then narrows the MCP tool
    /// list handed to the primary model — preventing, e.g., the browser
    /// tools from being pitched for a pure-chat greeting, and stopping
    /// small models from spinning on irrelevant tool choices.
    ///
    /// Null means the gatekeeper isn't configured — behavior matches the
    /// pre-footman runtime (every tool exposed every turn).
    /// </summary>
    public IFootmanRouter? Footman { get; init; }

    /// <summary>
    /// Formatted "city, region" hint from <c>LocationSettings.ManualLocation</c>.
    /// Injected into the system prompt so the model knows what to pass to
    /// <c>weather_geocode</c> / local-search tools when the user asks about
    /// weather, places, or news without specifying a location. Null leaves
    /// the system prompt unchanged.
    /// </summary>
    public string? LocationHint { get; init; }

    /// <summary>
    /// User's preferred unit system ("imperial" or "metric"). Surfaced to the
    /// model alongside <see cref="LocationHint"/> so replies use the expected
    /// units when the tool result is ambiguous. Null falls through.
    /// </summary>
    public string? PreferredUnits { get; init; }

    /// <summary>
    /// When true, chat turns hide web-classified tools and instruct the
    /// model to work from local/internal context only.
    /// </summary>
    public bool OfflineMode { get; init; }

    /// <summary>
    /// Personality runtime used by <c>PersonalityInjectionStep</c> to wrap
    /// the base system prompt with tone / formality / warmth modifiers
    /// and to inject few-shot examples. Null leaves the pipeline without
    /// a personality step (the base system prompt is used verbatim).
    /// </summary>
    public IPersonalityRuntime? PersonalityRuntime { get; init; }

    /// <summary>
    /// Optional memory-context provider for <c>MemoryContextStep</c>.
    /// When set, the pipeline prepends a [REMEMBERED CONTEXT] block with
    /// facts relevant to the current turn. Null = no memory read.
    /// </summary>
    public IMemoryContextProvider? MemoryContextProvider { get; init; }

    /// <summary>
    /// Optional direct handle to the semantic memory store, used by
    /// <c>CoreMemoryStep</c> to inject a small always-in-prompt block
    /// (user profile + top user-pinned nuggets) on every turn.
    /// Independent from <see cref="MemoryContextProvider"/>, which handles
    /// situation-specific dynamic retrieval. Null = no core memory tier.
    /// </summary>
    public SirThaddeus.Memory.IMemoryStore? MemoryStore { get; init; }

    /// <summary>
    /// Optional fire-and-forget memory extractor for
    /// <c>AutoMemoryExtractStep</c>. Captures user + assistant chunks
    /// and runs structured fact extraction in the background.
    /// Null = no memory writes.
    /// </summary>
    public IAutoMemoryExtractor? AutoMemoryExtractor { get; init; }

    /// <summary>
    /// Optional search-fallback executor for <c>SearchFallbackStep</c>.
    /// Replaces refusal-shaped drafts with a search-backed retry. Null =
    /// weak drafts pass through unchanged.
    /// </summary>
    public ISearchFallbackExecutor? SearchFallbackExecutor { get; init; }

    /// <summary>
    /// Optional reasoning-guardrails pipeline for <c>GuardrailsStep</c>.
    /// When set, questions that match the guardrails detector get a
    /// first-principles breakdown + synthesized answer before the tool
    /// loop runs. Null = guardrails step is a no-op.
    /// </summary>
    public ReasoningGuardrailsPipeline? GuardrailsPipeline { get; init; }

    /// <summary>
    /// Optional completion validator for <c>CompletionValidationStep</c>.
    /// When paired with <see cref="CompletionRepairLoop"/>, inadequate
    /// drafts get one targeted repair attempt before the composer
    /// finalizes the response.
    /// </summary>
    public CompletionValidator? CompletionValidator { get; init; }

    /// <summary>
    /// Optional repair loop paired with <see cref="CompletionValidator"/>.
    /// Null disables repair (validator can still run diagnostically).
    /// </summary>
    public RepairLoop? CompletionRepairLoop { get; init; }

    /// <summary>
    /// Optional per-conversation dialogue-state accessor for
    /// <c>DialogueStateStep</c>. UI runtime should use a
    /// <see cref="ThreadScopedDialogueStateAccessor"/> so each chat
    /// thread keeps its own topic / location / time-scope context.
    /// Null = dialogue-state step is a no-op.
    /// </summary>
    public IDialogueStateAccessor? DialogueStateAccessor { get; init; }

    /// <summary>Most recent N messages from the thread to send as history.</summary>
    public int HistoryTurns { get; init; } = 16;

    /// <summary>Maximum LLM round-trips inside the tool loop before giving up.</summary>
    public int MaxRoundTrips { get; init; } = 6;

    /// <summary>System prompt prepended to every request.</summary>
    /// <remarks>
    /// Tuned for small local models. The refusal pattern ("I cannot do X
    /// because...") is replaced with an attempt-then-caveat framing. Rules
    /// are stated as affirmatives so small models don't mirror negatives
    /// back as refusals. Tool-calling gets its own short paragraph so the
    /// model knows when it *must* use tools vs. answer from knowledge.
    /// </remarks>
    public string SystemPrompt { get; init; } =
        "You are Sir Thaddeus, a witty, direct AI assistant running locally on the user's machine. " +
        "Answer the question that was actually asked, then stop. " +

        "When a request is broad or underspecified, give a useful first pass " +
        "grounded in reasonable assumptions, then flag the assumptions or " +
        "offer to narrow. Do not refuse a reasonable request just because " +
        "the details are fuzzy. " +
        "When a request is genuinely outside your ability, say so in one " +
        "short sentence and suggest the nearest thing you *can* do. " +

        // ── Tool use ─────────────────────────────────────────────────────
        "You have access to local tools (file system, web search, weather, " +
        "times, holidays, screen capture, places, clipboard, and more). Use " +
        "them whenever they would give a more accurate or current answer " +
        "than your own knowledge — especially for live data (weather, news, " +
        "dates, file contents). Do not narrate the tool call itself. " +

        // ── Using tool RESULTS (small-model failure mode) ────────────────
        // Small local models sometimes call a second tool and forget to use
        // the first one's result. This rule makes that a hard stop.
        "Once a tool returns useful data, your NEXT message MUST directly " +
        "answer the user's original question using that data. Do not call " +
        "another tool unless the first tool's result is clearly insufficient " +
        "(e.g. empty, an error, or missing a specific field the user asked " +
        "for). If the result was long (JSON, prose, tables), extract the " +
        "part that matters to the user and summarize succinctly — don't " +
        "paste the whole payload back, and don't pivot to a different tool " +
        "because the output was complicated. " +

        // ── Format ───────────────────────────────────────────────────────
        "Respond in Markdown: headings, bullets, code blocks, tables as " +
        "needed. Keep paragraphs short. Lead with the answer, not with the " +
        "process. Avoid filler openings like 'Great question!' or 'As an AI, …'. " +

        // ── Honesty ──────────────────────────────────────────────────────
        "Be honest about uncertainty, but prefer to express it with a short " +
        "inline caveat rather than a whole-reply hedge. If you don't know a " +
        "fact and it matters, say so and keep going. ";

    private string ComposeSystemPrompt()
    {
        var text = SystemPrompt;

        // Logic-puzzle scaffold moved out of here — LogicPuzzleScaffoldStep
        // in the pipeline handles it after FeatureExtractorStep classifies
        // the turn. Keeping both would double-inject the suffix.
        //
        // Existence-verification hint also lives in the pipeline now
        // (ExistenceVerificationHintStep) so UI and CLI share the same
        // pattern-gated injection instead of duplicating it here.

        var dateBlock = BuildDateBlock();
        var locBlock = BuildLocationBlock();
        var offlineBlock = BuildOfflineModeBlock();
        var preamble = string.Join("\n\n",
            new[] { dateBlock, locBlock, offlineBlock }.Where(s => !string.IsNullOrEmpty(s)));
        return string.IsNullOrEmpty(preamble) ? text : preamble + "\n\n" + text;
    }

    // Without this block the model freely hallucinates a year — local-LLM
    // training cutoffs are months to years stale, and "what is today's date"
    // is routine enough that we can't rely on tool calls to rescue it.
    private static string BuildDateBlock()
    {
        var today = DateTimeOffset.Now;
        return $"Today's date is {today:dddd, MMMM d, yyyy} ({today:yyyy-MM-dd}). " +
               "Use this when the user asks about the current date, day of week, " +
               "or relative dates (e.g. \"tomorrow\", \"last week\"). Do not guess " +
               "or rely on your training cutoff.";
    }

    // (Existence-verification regex + builder moved to
    // ExistenceVerificationHintStep — it belongs in the pipeline so both
    // runtimes get it with the same gating.)

    /// <summary>
    /// Builds the one-paragraph location hint prepended to the system prompt
    /// when the user has a configured home location. Weather / local-search
    /// queries resolve to the user's city instead of the model's geographic
    /// default. Mirrors <c>BuildHeadlessSystemPrompt</c> in the CLI so both
    /// runtimes give the model the same baseline location context.
    /// </summary>
    private string BuildLocationBlock()
    {
        if (string.IsNullOrWhiteSpace(LocationHint)) return string.Empty;
        var units = string.IsNullOrWhiteSpace(PreferredUnits) ? "" : $" Preferred units: {PreferredUnits!.Trim()}.";
        return
            $"The user's home location is: {LocationHint!.Trim()}.{units} " +
            "Use this ONLY as the default area when they ask about weather, local " +
            "places, news, or times WITHOUT specifying a location. When the user " +
            "explicitly names a different city (e.g. \"weather in Seattle\"), use " +
            "the city THEY named — do not ask for clarification or second-guess. " +
            "Pass the location string to weather_geocode and similar location-scoped " +
            "tools verbatim. Do not announce that you know their home location — " +
            "just use it naturally when they omit one.";
    }

    private string BuildOfflineModeBlock()
    {
        if (!OfflineMode) return string.Empty;
        return
            "Offline mode is ON. Do not use web, browser, weather, places, feed, " +
            "holiday, status-check, or other network-backed tools. Work from local " +
            "conversation context, local memory, wiki/files when available, and " +
            "clearly say when a question needs live web access that offline mode is blocking.";
    }

    public LmStudioAssistant(
        ILlmClient llm,
        IMcpToolClient mcp,
        ToolPermissionGate gate,
        IThreadStore store,
        ChatTurnPublisher publisher,
        IAuditLogger audit,
        ILogger<LmStudioAssistant> logger)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RuntimeChatMessage> RespondAsync(string threadId, string userText, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentException.ThrowIfNullOrEmpty(userText);

        var messageId = "msg_" + Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 8))
            .ToLowerInvariant();
        var latencyTrace = RoutingLatencyTrace.Current;
        RoutingLatencyTrace.BindAssistantMessage(latencyTrace, messageId);
        await _publisher.PublishStartAsync(threadId, messageId, ct).ConfigureAwait(false);
        RoutingLatencyTrace.Mark(_logger, latencyTrace, "assistant_turn_start_event");

        var thread = await _store.GetAsync(threadId, ct).ConfigureAwait(false);
        var llmMessages = new List<LlmChatMessage>(HistoryTurns + 2)
        {
            LlmChatMessage.System(ComposeSystemPrompt()),
        };
        llmMessages.AddRange(BuildHistory(thread));

        // Fetch available tools from the MCP server and shape them for the
        // OpenAI function-calling API. Empty list means "no tools" — the
        // model will just answer from knowledge.
        var toolDiscoveryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var toolDefs = await BuildToolDefinitionsAsync(ct).ConfigureAwait(false);
        RoutingLatencyTrace.Mark(
            _logger,
            latencyTrace,
            "tool_registry_discovery_complete",
            System.Diagnostics.Stopwatch.GetElapsedTime(toolDiscoveryStarted).TotalMilliseconds);

        // Build the per-turn pipeline. Steps are cheap to construct; the
        // long-lived collaborators (LLM client, MCP client, footman) are
        // reused. The permission-gate adapter is per-turn because it
        // captures (threadId, turnId) at construction.
        var sink = new ChatTurnPublisherEventSink(
            _publisher, NullLogger<ChatTurnPublisherEventSink>.Instance);
        var gateAdapter = new RuntimePermissionGateAdapter(_gate, threadId, messageId);

        // Wrap the raw MCP client so every tool call writes
        // MCP_TOOL_CALL_START/END events to the audit log. The CLI harness
        // and any other auditing consumer reconstruct the tool trace from
        // those entries — without this wrapper, v2's audit file is empty.
        var auditedMcp = new AuditedMcpToolClient(
            _mcp,
            _audit,
            gateAdapter,
            sessionId: messageId);

        var pipeline = BuildTurnPipeline(auditedMcp, sink);

        var initialContext = new TurnContext
        {
            ThreadId = threadId,
            MessageId = messageId,
            UserText = userText,
            IsAutomationRun = false,
            LlmMessages = llmMessages,
            ToolDefs = toolDefs,
        };

        AgentResponse response;
        try
        {
            RoutingLatencyTrace.Mark(_logger, latencyTrace, "pipeline_start");
            response = await pipeline.RunAsync(initialContext, ct).ConfigureAwait(false);
            RoutingLatencyTrace.Mark(_logger, latencyTrace, "pipeline_complete");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _publisher.PublishCompleteAsync(threadId, messageId, string.Empty, cancelled: true,
                ct: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (HttpRequestException)
        {
            await _publisher.PublishCompleteAsync(threadId, messageId, string.Empty, cancelled: true,
                ct: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "lmstudio_assistant.pipeline_failed thread={ThreadId}", threadId);
            response = new AgentResponse { Text = $"(LLM error: {ex.Message})", Success = false };
        }

        var fullReply = response.Text;

        // Stream the final answer chunk-by-chunk for the UI.
        var sentSoFar = new System.Text.StringBuilder(fullReply.Length);
        var cancelled = false;
        try
        {
            foreach (var chunk in Chunkify(fullReply))
            {
                ct.ThrowIfCancellationRequested();
                sentSoFar.Append(chunk);
                if (sentSoFar.Length == chunk.Length)
                    RoutingLatencyTrace.Mark(_logger, latencyTrace, "first_ui_delta");
                await _publisher.PublishDeltaAsync(threadId, messageId, chunk, ct).ConfigureAwait(false);
                if (DeltaDelay > TimeSpan.Zero)
                {
                    await Task.Delay(DeltaDelay, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        var finalText = sentSoFar.ToString();
        // Copy citation cards from the pipeline onto the persisted message
        // so the UI can render rich preview cards when the user reopens
        // the thread. Null when the turn didn't call a source-producing
        // tool (keeps the JSON payload small for casual chat).
        var persistedSources = !response.SuppressSourceCardsUi && response.Sources.Count > 0
            ? response.Sources
                .Select(s => new ChatMessageSource(
                    Url: s.Url,
                    Title: s.Title,
                    Domain: s.Domain,
                    Excerpt: s.Excerpt,
                    Favicon: s.Favicon,
                    Thumbnail: s.Thumbnail,
                    PublishedAt: s.PublishedAt))
                .ToArray()
            : null;
        var message = new RuntimeChatMessage(
            messageId,
            ChatRole.Assistant,
            finalText,
            DateTimeOffset.UtcNow,
            Sources: persistedSources);

        try
        {
            await _store.AppendMessageAsync(threadId, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "lmstudio_assistant.persist_failed thread={ThreadId} message={MessageId}",
                threadId, messageId);
        }

        await _publisher.PublishCompleteAsync(
                threadId,
                messageId,
                finalText,
                cancelled,
                persistedSources,
                CancellationToken.None)
            .ConfigureAwait(false);
        RoutingLatencyTrace.Mark(_logger, latencyTrace, "assistant_turn_complete_event");
        return message;
    }

    /// <summary>
    /// Builds the per-turn chat pipeline. Steps are stateless so most are
    /// cheap to construct. Step order matters:
    /// feature extraction before the puzzle scaffold, scaffold before the
    /// footman, footman before the tool loop, post-process before the
    /// composer.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> (exposed to <c>Thaddeus.Runtime.Tests</c> via
    /// <c>InternalsVisibleTo</c>) so composition tests can assert step order —
    /// notably that <see cref="SelfConsistencyStep"/> sits immediately before
    /// the tool loop — without spinning up a full turn.
    /// </remarks>
    internal ChatPipeline BuildTurnPipeline(IMcpToolClient mcp, IChatEventSink sink)
    {
        var sanitize = new Func<TurnContext, string, string>((_, draft) =>
            AssistantResponseSanitizer.CleanChatReply(draft));
        Action<string, string>? latencyLog = IsLatencyTracingEnabled()
            ? (action, message) => _logger.LogInformation("{Action} {Message}", action, message)
            : null;

        var toolLoop = new ToolLoopStep(
            _llm, mcp, sink,
            // AuditedMcpToolClient is the single permission-enforcement
            // boundary. Gating again here would prompt twice for every tool.
            permissionGate: null,
            groupClassifier: RuntimeToolGroupClassifier.Instance,
            interceptors: Array.Empty<IToolCallInterceptor>(),
            argsRewriters:
            [
                new LocationAwarePlacesArgsRewriter(() => LocationHint),
                new FactSearchArgsRewriter(),
                new ExistenceSearchArgsRewriter()
            ],
            maxRoundTrips: MaxRoundTrips,
            log: latencyLog);

        var steps = new List<ITurnStep>
        {
            // Safety boundary runs FIRST. High-risk illicit-instruction
            // prompts get a canned safe-redirect response before any
            // other step touches the turn — no LLM, no memory read, no
            // tool loop. Matches the legacy orchestrator's line 182-192
            // safety short-circuit byte-for-byte.
            new SafetyBoundaryStep(() => PersonalityRuntime?.Snapshot.Profile.Id),

            // Utility fast-path. Deterministic matches (unit conversion,
            // percent-of, simple arithmetic, classic reasoning tripwires)
            // terminate the turn before any LLM round-trip or feature
            // extraction. Non-matches fall through unchanged.
            new UtilityFastPathStep(),

            // Benign fallback: canned replies for a tight set of trivial
            // prompts (greetings, classic-reasoning probes). Only fires
            // when the prompt isn't tool-eligible, so legitimate tool
            // requests are never stolen.
            new BenignFallbackStep(),

            // Personality wraps the base system prompt. No-op when
            // PersonalityRuntime is null (desktop runtime sans profile).
            new PersonalityInjectionStep(PersonalityRuntime),

            new FeatureExtractorStep(),
            new LogicPuzzleScaffoldStep(),

            // Memory context injects [REMEMBERED CONTEXT]. Also sets
            // TurnContext.IsNewUser from the provider's onboarding
            // signal so the next step can fire on cold starts. No-op
            // when MemoryContextProvider is null.
            new MemoryContextStep(
                MemoryContextProvider,
                onRecalled: async (n, ct) =>
                {
                    await _publisher.PublishMemoryRecalledAsync(
                        n.ThreadId,
                        n.MessageId,
                        n.FactsCount,
                        n.EventsCount,
                        n.ChunksCount,
                        n.NuggetsCount,
                        n.Preview,
                        n.DurationMs,
                        ct).ConfigureAwait(false);
                }),

            // Core memory: always-in-prompt [CORE MEMORY] block carrying
            // the user's display name + top user-pinned nuggets. Reads
            // IMemoryStore directly — no MCP roundtrip, no LLM call.
            // No-op when MemoryStore is null or no items qualify.
            new CoreMemoryStep(MemoryStore),

            // Onboarding injection: appends the cold-introduction
            // suffix when the memory provider signals no profile facts
            // are known yet. No-op on warm users / when memory is off.
            new OnboardingInjectionStep(ctx => ctx.IsNewUser
                ? OnboardingMode.Cold
                : OnboardingMode.NotNeeded),

            // Dialogue state: appends a [CONVERSATION CONTEXT] block
            // with the previous turn's topic / location / time-scope so
            // the model can resolve follow-ups ("what about tomorrow?")
            // without the user re-stating context. No-op on fresh
            // threads.
            new DialogueStateStep(DialogueStateAccessor),

            // Existence-check nudge: when the user asks "does X exist" /
            // "was X released" etc., remind the model to verify via
            // web_search before answering from (stale) training memory.
            // No-op on other prompt shapes.
            new ExistenceVerificationHintStep(),

            new FootmanRouterStep(
                Footman,
                sink,
                alwaysAllowToolNames: Array.Empty<string>()),

            // Guardrails: first-principles scaffold for reasoning-heavy
            // questions. Terminates the turn with a synthesized answer
            // when the detector fires; no-op otherwise.
            new GuardrailsStep(GuardrailsPipeline),

            // Freshness router (Layer A of the confidence system): for
            // clearly fresh / existence / recency / pricing questions,
            // force tool_choice=web_search on the first tool-loop round
            // so the model can't answer from stale training memory. The
            // soft hint above motivates; this enforces.
            new FreshnessRouterStep(),

            // Self-consistency (opt-in via ST_SELF_CONSISTENCY=N): for strict-answer
            // reasoning items, sample the model N times step-by-step and return the
            // majority-vote answer — stabilizes the variance a single sample shows
            // on multi-step problems. No-op when disabled or for non-strict prompts,
            // so production behavior is byte-identical when ST_SELF_CONSISTENCY is
            // unset. The tool loop is passed as an optional collaborator so tool-aware
            // mode (ST_SELF_CONSISTENCY_TOOLS=1) can vote over full tool-loop runs for
            // compute-bound problems. It stays inert unless that flag is also set.
            // Mirrors BuildPipelineBackedOrchestrator so the desktop UI and the CLI
            // harness exercise the same lever at the same pipeline position.
            new SelfConsistencyStep(_llm, toolLoop: toolLoop),

            toolLoop,
            new PostProcessStep(sanitize, "PostProcess:Sanitize"),

            // Completion validation + repair: checks the post-processed
            // draft actually answered the question; runs one targeted
            // repair if the validator flags a miss. No-op when either
            // collaborator is null.
            new CompletionValidationStep(CompletionValidator, CompletionRepairLoop, latencyLog),

            // Search fallback: replaces refusal drafts with a retry when
            // user prompt has web-lookup signals. No-op when executor null.
            new SearchFallbackStep(
                SearchFallbackExecutor,
                buildRequest: ctx =>
                {
                    if (!ctx.ToolDefs.Any(def =>
                            string.Equals(def.Function?.Name, ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase)))
                    {
                        return null;
                    }

                    var draft = ctx.AssistantDraft ?? string.Empty;
                    if (LooksLikeCompletedWeatherNewsEvidenceDraft(draft))
                        return null;

                    var refusal = RefusalDetector.HasRefusalOrUncertaintySignals(draft, draft);
                    // Layer B: hedge detection catches "I believe ... as of
                    // my training data" drafts on factual prompts — same
                    // fallback path as refusals, same grounded repair.
                    var hedged = HedgeSignalDetector.ShouldVerify(draft, ctx.UserText);
                    if (!refusal && !hedged)
                        return null;
                    return new SearchFallbackRequest
                    {
                        UserMessage = ctx.UserText ?? string.Empty,
                        History = ctx.LlmMessages.ToList(),
                        ToolCallsMade = ctx.ToolCallsMade.ToList(),
                        HasRefusalOrUncertaintySignals = true,
                    };
                }),

            new PostProcessStep(sanitize, "PostProcess:SearchFallbackSanitize"),

            // Fire-and-forget user + assistant memory writes. No-op when
            // AutoMemoryExtractor is null.
            new AutoMemoryExtractStep(AutoMemoryExtractor),

            new ResponseComposerStep(),
        };

        if (TurnPlanShadowStep.IsEnabled)
        {
            var featureIndex = steps.FindIndex(step => step is FeatureExtractorStep);
            steps.Insert(
                featureIndex + 1,
                new TurnPlanShadowStep((action, message) =>
                    _logger.LogInformation("{Action} {Message}", action, message)));
        }

        return new ChatPipeline(steps, latencyLog);
    }

    private static bool IsLatencyTracingEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase);
    }


    /// <summary>
    /// Queries the MCP server for its advertised tools and reshapes them
    /// into the OpenAI function-calling structure expected by the LLM.
    /// </summary>
    private async Task<IReadOnlyList<ToolDefinition>> BuildToolDefinitionsAsync(CancellationToken ct)
    {
        IReadOnlyList<McpToolInfo> mcpTools;
        try
        {
            mcpTools = await _mcp.ListToolsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mcp.list_tools_failed");
            return Array.Empty<ToolDefinition>();
        }

        if (mcpTools.Count == 0) return Array.Empty<ToolDefinition>();

        var defs = new List<ToolDefinition>(mcpTools.Count);
        foreach (var t in mcpTools)
        {
            if (OfflineMode && RuntimeToolGroupClassifier.Instance.Classify(t.Name) == ToolGroup.Web.ToString())
                continue;

            defs.Add(new ToolDefinition
            {
                Function = new FunctionDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    // Parameters schema is an arbitrary JSON object; the MCP
                    // client already returns it in the right shape.
                    Parameters = t.InputSchema,
                }
            });
        }
        return defs;
    }

    private IEnumerable<LlmChatMessage> BuildHistory(ChatThread? thread)
    {
        if (thread is null) yield break;
        var msgs = thread.Messages;
        var start = Math.Max(0, msgs.Count - HistoryTurns);
        for (var i = start; i < msgs.Count; i++)
        {
            var m = msgs[i];
            switch (m.Role)
            {
                case ChatRole.User:
                    yield return LlmChatMessage.User(m.Text ?? string.Empty);
                    break;
                case ChatRole.Assistant:
                    yield return LlmChatMessage.Assistant(m.Text ?? string.Empty);
                    break;
                case ChatRole.System:
                    yield return LlmChatMessage.System(m.Text ?? string.Empty);
                    break;
            }
        }
    }

    private static IEnumerable<string> Chunkify(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                yield return text.Substring(start, i - start + 1);
                start = i + 1;
            }
        }
        if (start < text.Length) yield return text.Substring(start);
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
