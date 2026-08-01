using Microsoft.Extensions.Logging;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using Thaddeus.Runtime.Chat.Pipeline;
using Thaddeus.Runtime.Tools;

namespace Thaddeus.Runtime.Chat;

public sealed partial class LmStudioAssistant
{
    /// <summary>
    /// Builds the per-turn chat pipeline. Steps are stateless so most are
    /// cheap to construct. Step order matters:
    /// feature extraction before the puzzle scaffold, scaffold before the
    /// footman, footman before the tool loop, post-process before the
    /// composer.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> (exposed to <c>Thaddeus.Runtime.Tests</c> via
    /// <c>InternalsVisibleTo</c>) so composition tests can assert security and
    /// ordering invariants without spinning up a full turn.
    /// </remarks>
    internal ChatPipeline BuildTurnPipeline(IMcpToolClient mcp, IChatEventSink sink)
    {
        var sanitize = new Func<TurnContext, string, string>((ctx, draft) =>
            AssistantResponseSanitizer.CleanChatReply(draft, ctx.UserText));
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
            maxOutputTokens: MaxOutputTokens,
            log: latencyLog,
            executionControl: ExecutionControl);

        var steps = new List<ITurnStep>
        {
            // Safety boundary runs FIRST. High-risk illicit-instruction
            // prompts get a canned safe-redirect response before any
            // other step touches the turn — no LLM, no memory read, no
            // tool loop. Matches the legacy orchestrator's line 182-192
            // safety short-circuit byte-for-byte.
            new SafetyBoundaryStep(() => PersonalityRuntime?.Snapshot.Profile.Id),

            // Audited read-only projection for explicit questions about one
            // live runtime policy field. Ambiguous requests fall through.
            new PolicyStateUtilityStep(mcp, sink, latencyLog),

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

        return new ChatPipeline(steps, latencyLog, ExecutionControl);
    }

    private static bool IsLatencyTracingEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase);
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
