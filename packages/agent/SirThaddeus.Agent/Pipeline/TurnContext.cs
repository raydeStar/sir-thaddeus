using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Immutable per-turn state that flows through the chat pipeline. Each
/// pipeline step reads a context, decides what (if anything) to change,
/// and returns either a new context (<see cref="StepResult.Continue"/>)
/// or a final response (<see cref="StepResult.Terminate"/>).
///
/// <para>The context is a plain data record. Steps never hold references to
/// it across turns — it's scoped to a single user message. Cross-turn state
/// (thread history, dialogue state, personality cache) lives in the runtime
/// facade that builds the context, never here.</para>
///
/// <para>Fields marked as nullable / defaulted to empty are populated by
/// specific steps further down the pipeline. For example
/// <see cref="Features"/> starts null and is filled in by a feature-extractor
/// step; <see cref="AssistantDraft"/> stays null until a step that calls the
/// LLM produces one.</para>
/// </summary>
public sealed record TurnContext
{
    /// <summary>Opaque thread id, supplied by the runtime facade.</summary>
    public required string ThreadId { get; init; }

    /// <summary>Id assigned to the assistant message this turn will produce.
    /// Stable across the pipeline — used for event correlation (chips,
    /// footman decisions, streaming deltas).</summary>
    public required string MessageId { get; init; }

    /// <summary>The user's request verbatim.</summary>
    public required string UserText { get; init; }

    /// <summary>True when this turn was triggered by an automation run
    /// rather than a direct user message. Certain steps (e.g. the
    /// propose-automation virtual tool) short-circuit on this flag.</summary>
    public bool IsAutomationRun { get; init; }

    /// <summary>
    /// Per-turn memory policy. Ephemeral turns neither read durable memory nor
    /// enqueue automatic writes, and their tool list must exclude memory tools.
    /// </summary>
    public TurnMemoryAccess MemoryAccess { get; init; } = TurnMemoryAccess.Enabled;

    /// <summary>
    /// Existing Wiki state explicitly selected by the user as the only
    /// mutation scope for this turn. Null preserves ordinary tool behavior.
    /// </summary>
    public WikiMutationTarget? WikiMutationTarget { get; init; }

    /// <summary>
    /// Optional absolute workflow deadline supplied by a budget-owning host.
    /// It never overrides the caller's cancellation token.
    /// </summary>
    public DateTimeOffset? WorkflowDeadlineUtc { get; init; }

    /// <summary>Deterministic heuristic signals over the user message.
    /// Populated by a feature-extractor step. Null before that step runs.</summary>
    public RoutingFeatures? Features { get; init; }

    /// <summary>LLM-facing message list, typically seeded with a system
    /// prompt + recent history by the facade and mutated-by-replacement in
    /// steps that inject scaffolds (logic-puzzle mode, automation suffix,
    /// memory context, etc.).</summary>
    public IReadOnlyList<ChatMessage> LlmMessages { get; init; } = [];

    /// <summary>Tool definitions offered to the primary model. A footman
    /// step may narrow this list; the tool loop consumes it.</summary>
    public IReadOnlyList<ToolDefinition> ToolDefs { get; init; } = [];

    /// <summary>Assistant draft text produced by the tool-loop step, before
    /// post-processing. Null before the tool loop completes.</summary>
    public string? AssistantDraft { get; init; }

    /// <summary>
    /// Runtime-owned provenance for a deterministic completion produced from
    /// one independently verified file effect. Later pipeline steps may trust
    /// it only while <see cref="AssistantDraft"/> still exactly matches the
    /// attested text and the underlying receipt still validates.
    /// </summary>
    public VerifiedFileEffectCompletionAttestation? VerifiedFileEffectCompletion { get; init; }

    /// <summary>Tool calls executed during this turn, in order. Grows as the
    /// tool-loop step appends calls; final <see cref="AgentResponse"/>
    /// inherits this list.</summary>
    public IReadOnlyList<ToolCallRecord> ToolCallsMade { get; init; } = [];

    /// <summary>
    /// True when the user is new/unknown — no profile facts stored.
    /// Populated by <c>MemoryContextStep</c> from the provider's
    /// <see cref="SirThaddeus.Agent.Memory.MemoryContextResult.OnboardingNeeded"/>
    /// signal, and consumed by <c>OnboardingInjectionStep</c> to decide
    /// whether to inject the warm-introduction suffix. Defaults to false
    /// — runtimes without a memory provider never trigger onboarding.
    /// </summary>
    public bool IsNewUser { get; init; }

    /// <summary>
    /// When set, <c>ToolLoopStep</c> passes this as <c>tool_choice</c> on
    /// the <b>first</b> LLM round so the model is forced to invoke the
    /// named tool before producing any prose. Used by steps that detect a
    /// structural need for a tool call (e.g. <c>FreshnessRouterStep</c>
    /// for existence/recency queries — "does the iPhone 15 exist?" must
    /// verify via <c>web_search</c>, never from stale training memory).
    ///
    /// <para>Subsequent rounds in the same turn fall back to
    /// <c>tool_choice: "auto"</c> so the model can summarize or chain
    /// follow-up tools normally.</para>
    ///
    /// <para>Null means no forcing — the default behavior.</para>
    /// </summary>
    public string? ForcedTool { get; init; }
}

/// <summary>
/// Typed provenance for a verified single-file-effect completion. This is
/// deliberately not a general validation-bypass flag.
/// </summary>
public sealed record VerifiedFileEffectCompletionAttestation(string Text);

public enum TurnMemoryAccess
{
    Enabled,
    Disabled,
    Ephemeral,
}
