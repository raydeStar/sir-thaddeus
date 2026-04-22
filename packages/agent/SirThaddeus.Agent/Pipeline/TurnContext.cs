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
}
