using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.Tools;
using SirThaddeus.Agent.Validation;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine;
using SirThaddeus.RuntimeHost.Harness;
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
public sealed partial class LmStudioAssistant : IAssistant
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
    /// Optional isolated-run capture for exact tool evidence. It is populated
    /// only when the runtime was launched in harness mode.
    /// </summary>
    public HarnessToolEvidenceStore? HarnessToolEvidence { get; init; }

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

    /// <summary>Configured output-token budget for the primary model call.</summary>
    public int MaxOutputTokens { get; init; } = 1024;

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
        if (latencyTrace is not null)
        {
            _logger.LogInformation(
                "EXPERIMENT_ACTIVATION turn_id={TurnId} event=local_wiki_evidence_packet decision={Decision}",
                messageId,
                latencyTrace.LocalWikiEvidencePacketActivated ? "activated" : "inactive");
        }
        await _publisher.PublishStartAsync(threadId, messageId, ct).ConfigureAwait(false);
        RoutingLatencyTrace.Mark(_logger, latencyTrace, "assistant_turn_start_event");

        var thread = await _store.GetAsync(threadId, ct).ConfigureAwait(false);
        var llmMessages = new List<LlmChatMessage>(HistoryTurns + 2)
        {
            LlmChatMessage.System(ProductionPromptComposer.ComposeBaseSystemPrompt(
                SystemPrompt,
                DateTimeOffset.Now,
                LocationHint,
                preferredUnits: PreferredUnits,
                offlineMode: OfflineMode)),
        };
        llmMessages.AddRange(BuildHistory(thread, userText));

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

        IMcpToolClient pipelineMcp = auditedMcp;
        if (HarnessControlPlane.IsHarnessReuseEnabled() && HarnessToolEvidence is not null)
        {
            pipelineMcp = new HarnessEvidenceMcpToolClient(
                auditedMcp,
                HarnessToolEvidence,
                messageId);
        }

        var pipeline = BuildTurnPipeline(pipelineMcp, sink);

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
    /// Queries the MCP server for its advertised tools and reshapes them
    /// into the OpenAI function-calling structure expected by the LLM.
    /// </summary>
    private async Task<IReadOnlyList<ToolDefinition>> BuildToolDefinitionsAsync(CancellationToken ct)
    {
        var builder = new ToolDefinitionBuilder(_mcp);
        var definitions = await builder.BuildAsync(
            memoryEnabled: true,
            panicModeEnabled: false,
            safeModeEnabled: false,
            logEvent: (_, message) => _logger.LogDebug("tool.discovery {Message}", message),
            cancellationToken: ct).ConfigureAwait(false);

        if (!OfflineMode)
            return definitions;

        return definitions
            .Where(definition =>
                RuntimeToolGroupClassifier.Instance.Classify(definition.Function.Name)
                != ToolGroup.Web.ToString())
            .ToArray();
    }

    private IEnumerable<LlmChatMessage> BuildHistory(ChatThread? thread, string currentUserText)
    {
        if (thread is null || thread.Messages.Count == 0)
        {
            yield return LlmChatMessage.User(currentUserText);
            yield break;
        }

        var msgs = thread.Messages;
        var start = Math.Max(0, msgs.Count - HistoryTurns);
        for (var i = start; i < msgs.Count; i++)
        {
            var m = msgs[i];
            switch (m.Role)
            {
                case ChatRole.User:
                    // The API persists the user-visible request before invoking the
                    // assistant, but may pass a richer model-facing prompt for the
                    // same turn (for example, attached local Wiki evidence). Keep
                    // the stored conversation clean while making that authoritative
                    // prompt the final user message sent to the model.
                    yield return LlmChatMessage.User(
                        i == msgs.Count - 1 ? currentUserText : m.Text ?? string.Empty);
                    break;
                case ChatRole.Assistant:
                    yield return LlmChatMessage.Assistant(m.Text ?? string.Empty);
                    break;
                case ChatRole.System:
                    yield return LlmChatMessage.System(m.Text ?? string.Empty);
                    break;
            }
        }

        if (msgs[^1].Role != ChatRole.User)
            yield return LlmChatMessage.User(currentUserText);
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
