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

        // ── Automations (propose_automation) ─────────────────────────────
        // When the user asks to be reminded, to save a recurring task, or
        // to automate something, the model should use the propose_automation
        // tool so the UI can pop a confirmation card. The tool description
        // carries the exact schema; keep this nudge short.
        "When the user asks you to remind them, schedule a task, or save an " +
        "automation (e.g. 'remind me tomorrow at 9 about the meeting', " +
        "'every weekday at 8:15 AM check the weather'), call the " +
        "propose_automation tool with a short name, the ordered steps, and a " +
        "schedule. Use 'one-shot' only for a single future time. For recurring " +
        "requests like 'every day', 'daily', 'every weekday', 'every Monday', " +
        "or 'every month', use a cron schedule instead of one-shot. Do not try " +
        "to set reminders with other tools. When the user gave an explicit " +
        "cadence or time, do not omit the schedule. For example, 'every day at " +
        "9 AM' should be a cron schedule like '0 9 * * *', not manual. " +

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

    /// <summary>
    /// Appended to the base <see cref="SystemPrompt"/> when the current thread
    /// is an automation run. Small local models default to "I can't do that,
    /// would you like me to..." refusal loops when asked to "go to" a site,
    /// because their RLHF teaches them they have no browser. That's wrong
    /// here — they DO have tools. This block forces action-first behavior.
    /// </summary>
    private const string AutomationRunSystemPromptSuffix =
        "\n\n" +
        "═══ AUTOMATION MODE ═══\n" +
        "You are running inside a SCRIPTED automation. No human is watching. " +
        "You MUST act on each step using your tools — never ask the user to " +
        "do anything, never offer to search later, never wait for confirmation. " +
        "\n\n" +
        "Strictly forbidden phrasings (do NOT emit these — they break the run):\n" +
        "  • \"I can't open websites\" / \"I can't open a new tab\" / \"I can't navigate\"\n" +
        "  • \"Would you like me to...\" / \"Do you want me to...\"\n" +
        "  • \"Let me know if...\" / \"Just tell me and I'll...\"\n" +
        "  • \"I'll check up on ... after your message\" / \"I can't wait for you\"\n" +
        "\n" +
        "Your tools DO give you the capability. Use them:\n" +
        "  • Step says \"go to X\" or \"check X\" → call browser_navigate(url) or web_search\n" +
        "  • Step mentions a URL / domain → call browser_navigate on that URL\n" +
        "  • Step asks a factual question → call web_search\n" +
        "  • Step mentions the screen → call screen_capture\n" +
        "\n" +
        "browser_navigate FETCHES page content for you to read; it does not open " +
        "a tab in the user's browser. Describe what you did as \"fetched\" or " +
        "\"read\", never \"navigated your browser\". \n\n" +
        "When a tool RETURNS AN ERROR (e.g. \"Error reading https://…\", 403, 503, " +
        "timeout) that is a specific fetch failure, NOT a limitation of your " +
        "tool. Do NOT conclude \"I can't access external URLs\" or \"this tool " +
        "doesn't allow that\" — the tool does allow it, the site just refused " +
        "or timed out. Report the specific failure in one short sentence, " +
        "suggest an alternate approach (different URL, web_search instead, etc.), " +
        "and move on. \n\n" +
        "Do NOT retry the same tool call after an error. Do NOT fall back to " +
        "web_search in a loop when browser_navigate fails — pick ONE alternative, " +
        "try it at most once, then produce your final answer from whatever data " +
        "you already have. Spinning on the same tool will hit the round-trip " +
        "cap and leave the user with an empty summary. \n\n" +
        "When multiple steps reference the same URL or topic, reuse what the " +
        "previous step already fetched. If step 1 already captured an Amazon " +
        "listing URL, step 2 should call browser_navigate with that exact URL " +
        "— do not re-search for it. \n\n" +
        "If a tool truly cannot help with a step, say so in ONE short sentence " +
        "and stop — do not loop with more apologies.";

    private string ComposeSystemPrompt(bool isAutomationRun)
    {
        var text = SystemPrompt;
        if (isAutomationRun) text += AutomationRunSystemPromptSuffix;

        // Logic-puzzle scaffold moved out of here — LogicPuzzleScaffoldStep
        // in the pipeline handles it after FeatureExtractorStep classifies
        // the turn. Keeping both would double-inject the suffix.
        //
        // Existence-verification hint also lives in the pipeline now
        // (ExistenceVerificationHintStep) so UI and CLI share the same
        // pattern-gated injection instead of duplicating it here.

        var dateBlock = BuildDateBlock();
        var locBlock = BuildLocationBlock();
        var preamble = string.Join("\n\n",
            new[] { dateBlock, locBlock }.Where(s => !string.IsNullOrEmpty(s)));
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
    /// when the user has a configured home location. Mirrors the AgentOrchestrator
    /// behavior so weather / local-search queries resolve to the user's city
    /// instead of the model's geographic default.
    /// </summary>
    private string BuildLocationBlock()
    {
        if (string.IsNullOrWhiteSpace(LocationHint)) return string.Empty;
        var units = string.IsNullOrWhiteSpace(PreferredUnits) ? "" : $" Preferred units: {PreferredUnits!.Trim()}.";
        return
            $"The user's home location is: {LocationHint!.Trim()}.{units} " +
            "Use this as the default area when they ask about weather, local places, " +
            "news, or times without specifying a location. Pass it to weather_geocode " +
            "and similar location-scoped tools verbatim. Do not announce that you know " +
            "their location — just use it naturally.";
    }

    public LmStudioAssistant(
        ILlmClient llm,
        IMcpToolClient mcp,
        ToolPermissionGate gate,
        IThreadStore store,
        ChatTurnPublisher publisher,
        ILogger<LmStudioAssistant> logger)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RuntimeChatMessage> RespondAsync(string threadId, string userText, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentException.ThrowIfNullOrEmpty(userText);

        var messageId = "msg_" + Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 8))
            .ToLowerInvariant();
        await _publisher.PublishStartAsync(threadId, messageId, ct).ConfigureAwait(false);

        // Automation runs explicitly suppress `propose_automation` from the
        // advertised list (the user is executing a saved automation, not
        // building a new one — the inline confirmation card makes no sense
        // during a run). Defense-in-depth against small models hallucinating
        // the call from memory lives in the pipeline interceptor.
        var thread = await _store.GetAsync(threadId, ct).ConfigureAwait(false);
        var isAutomationRun = _gate.IsAutomationRunThread(threadId);
        var llmMessages = new List<LlmChatMessage>(HistoryTurns + 2)
        {
            LlmChatMessage.System(ComposeSystemPrompt(isAutomationRun)),
        };
        llmMessages.AddRange(BuildHistory(thread));

        // Fetch available tools from the MCP server and shape them for the
        // OpenAI function-calling API. Empty list means "no tools" — the
        // model will just answer from knowledge.
        var toolDefs = await BuildToolDefinitionsAsync(!isAutomationRun, ct).ConfigureAwait(false);

        // Build the per-turn pipeline. Steps are cheap to construct; the
        // long-lived collaborators (LLM client, MCP client, footman) are
        // reused. The permission-gate adapter is per-turn because it
        // captures (threadId, turnId) at construction.
        var sink = new ChatTurnPublisherEventSink(
            _publisher, NullLogger<ChatTurnPublisherEventSink>.Instance);
        var gateAdapter = new RuntimePermissionGateAdapter(_gate, threadId, messageId);
        var pipeline = BuildTurnPipeline(sink, gateAdapter);

        var initialContext = new TurnContext
        {
            ThreadId = threadId,
            MessageId = messageId,
            UserText = userText,
            IsAutomationRun = isAutomationRun,
            LlmMessages = llmMessages,
            ToolDefs = toolDefs,
        };

        AgentResponse response;
        try
        {
            response = await pipeline.RunAsync(initialContext, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _publisher.PublishCompleteAsync(threadId, messageId, string.Empty, cancelled: true,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (HttpRequestException)
        {
            await _publisher.PublishCompleteAsync(threadId, messageId, string.Empty, cancelled: true,
                CancellationToken.None).ConfigureAwait(false);
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
        var message = new RuntimeChatMessage(messageId, ChatRole.Assistant, finalText, DateTimeOffset.UtcNow);

        try
        {
            await _store.AppendMessageAsync(threadId, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "lmstudio_assistant.persist_failed thread={ThreadId} message={MessageId}",
                threadId, messageId);
        }

        await _publisher.PublishCompleteAsync(threadId, messageId, finalText, cancelled, CancellationToken.None)
            .ConfigureAwait(false);
        return message;
    }

    /// <summary>
    /// Builds the per-turn chat pipeline. Steps are stateless so most are
    /// cheap to construct; the permission-gate adapter is per-turn because
    /// it captures (threadId, turnId) at construction. Step order matters:
    /// feature extraction before the puzzle scaffold, scaffold before the
    /// footman, footman before the tool loop, post-process before the
    /// composer.
    /// </summary>
    private ChatPipeline BuildTurnPipeline(IChatEventSink sink, IToolPermissionGate permissionGate)
    {
        var sanitize = new Func<TurnContext, string, string>((ctx, draft) =>
        {
            // Scrub harmony / template-token leaks and <think> scaffolding —
            // raw markers would otherwise feed back into the next turn's
            // history. Automation runs additionally collapse "I can't /
            // would you like me to" refusal loops since small local models
            // emit them even when they have the capability.
            var cleaned = AssistantResponseSanitizer.CleanChatReply(draft);
            if (ctx.IsAutomationRun)
                cleaned = AssistantResponseSanitizer.CollapseAutomationRefusalLoop(cleaned);
            return cleaned;
        });

        var toolLoop = new ToolLoopStep(
            _llm, _mcp, sink,
            permissionGate: permissionGate,
            groupClassifier: RuntimeToolGroupClassifier.Instance,
            interceptors: new IToolCallInterceptor[]
            {
                new ProposeAutomationInterceptor(_publisher, _gate),
            },
            argsRewriters: new IToolArgsRewriter[]
            {
                new AutomationSearchRecencyRewriter(),
            },
            maxRoundTrips: MaxRoundTrips);

        return new ChatPipeline(new ITurnStep[]
        {
            // Safety boundary runs FIRST. High-risk illicit-instruction
            // prompts get a canned safe-redirect response before any
            // other step touches the turn — no LLM, no memory read, no
            // tool loop. Matches the legacy orchestrator's line 182-192
            // safety short-circuit byte-for-byte.
            new SafetyBoundaryStep(),

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
            new MemoryContextStep(MemoryContextProvider),

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
                alwaysAllowToolNames: new[] { ProposeAutomationTool.ToolName }),

            // Guardrails: first-principles scaffold for reasoning-heavy
            // questions. Terminates the turn with a synthesized answer
            // when the detector fires; no-op otherwise.
            new GuardrailsStep(GuardrailsPipeline),

            toolLoop,
            new PostProcessStep(sanitize, "PostProcess:Sanitize"),

            // Completion validation + repair: checks the post-processed
            // draft actually answered the question; runs one targeted
            // repair if the validator flags a miss. No-op when either
            // collaborator is null.
            new CompletionValidationStep(CompletionValidator, CompletionRepairLoop),

            // Search fallback: replaces refusal drafts with a retry when
            // user prompt has web-lookup signals. No-op when executor null.
            new SearchFallbackStep(
                SearchFallbackExecutor,
                buildRequest: ctx =>
                {
                    var draft = ctx.AssistantDraft ?? string.Empty;
                    if (!AgentOrchestrator.HasRefusalOrUncertaintySignals(draft, draft))
                        return null;
                    return new SearchFallbackRequest
                    {
                        UserMessage = ctx.UserText ?? string.Empty,
                        History = ctx.LlmMessages.ToList(),
                        ToolCallsMade = ctx.ToolCallsMade.ToList(),
                        HasRefusalOrUncertaintySignals = true,
                    };
                }),

            // Fire-and-forget user + assistant memory writes. No-op when
            // AutoMemoryExtractor is null.
            new AutoMemoryExtractStep(AutoMemoryExtractor),

            new ResponseComposerStep(),
        });
    }


    /// <summary>
    /// Queries the MCP server for its advertised tools and reshapes them
    /// into the OpenAI function-calling structure expected by the LLM.
    /// When <paramref name="includeProposeAutomation"/> is false, the
    /// runtime-side virtual tool is omitted (see the automation-run guard
    /// in <see cref="RespondAsync"/>).
    /// </summary>
    private async Task<IReadOnlyList<ToolDefinition>> BuildToolDefinitionsAsync(
        bool includeProposeAutomation,
        CancellationToken ct)
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

        if (mcpTools.Count == 0)
        {
            return includeProposeAutomation
                ? new[] { ProposeAutomationTool.BuildDefinition() }
                : Array.Empty<ToolDefinition>();
        }

        var defs = new List<ToolDefinition>(mcpTools.Count + (includeProposeAutomation ? 1 : 0));
        foreach (var t in mcpTools)
        {
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

        // Virtual (runtime-side) tool: the assistant can emit this to pop an
        // inline confirmation card in the chat. It is NOT sent to the MCP
        // server — LmStudioAssistant intercepts the call and publishes a
        // typed event instead. See ProposeAutomationTool.
        if (includeProposeAutomation)
        {
            defs.Add(ProposeAutomationTool.BuildDefinition());
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

}
