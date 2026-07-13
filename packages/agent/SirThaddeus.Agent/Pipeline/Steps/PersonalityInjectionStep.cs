using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine;
using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that applies the user's active personality profile to the
/// outgoing request:
/// <list type="bullet">
///   <item>Wraps the existing system message content via
///         <see cref="IPersonalityRuntime.BuildSystemPrompt"/>, layering
///         tone / formality / warmth modifiers onto the base task instruction.</item>
///   <item>Injects the profile's few-shot examples as alternating
///         user/assistant messages between the system block and the
///         actual chat history.</item>
/// </list>
///
/// <para>No-op when no runtime is supplied — UI runtimes that haven't
/// hooked up personality skip this step entirely. The base system prompt
/// passes through unchanged.</para>
///
/// <para>Place this step <b>before</b> <c>MemoryContextStep</c> and the
/// intent-injection steps so personality-wrapped prompt is the foundation
/// everything else appends to.</para>
/// </summary>
public sealed class PersonalityInjectionStep : ITurnStep
{
    private const string FinalTaskFocusBlock = """

        [TurnFocus:latest_unresolved]
        When the user supplies completed examples, solved items, or reference material followed by an unfinished request, answer only the final unresolved request. Do not re-answer or summarize completed examples unless the user asks. Follow the user's explicit safe output format exactly.
        [/TurnFocus:latest_unresolved]
        """;

    private readonly IPersonalityRuntime? _runtime;

    public PersonalityInjectionStep(IPersonalityRuntime? runtime)
    {
        _runtime = runtime;
    }

    public string Name => "PersonalityInjection";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_runtime is null)
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var messages = context.LlmMessages.ToList();
        WrapFirstSystemMessage(messages, _runtime);
        if (ExplicitResponseContractDetector.IsNoToolDirectAnswer(context.UserText))
            AppendFinalTaskFocus(messages);
        InjectFewShotExamples(messages, _runtime.Snapshot.Profile.Instructions.FewShotExamples);

        return Task.FromResult<StepResult>(new StepResult.Continue(context with { LlmMessages = messages }));
    }

    private static void WrapFirstSystemMessage(List<ChatMessage> messages, IPersonalityRuntime runtime)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (!string.Equals(messages[i].Role, "system", StringComparison.OrdinalIgnoreCase))
                continue;

            var task = messages[i].Content ?? string.Empty;
            var wrapped = runtime.BuildSystemPrompt(task);
            messages[i] = ChatMessage.System(wrapped);
            return;
        }

        // No system message seeded — create one from personality alone.
        // Uncommon case (facade should always seed the base prompt) but
        // keeps the step safe under future reorderings.
        messages.Insert(0, ChatMessage.System(runtime.BuildSystemPrompt(taskInstruction: string.Empty)));
    }

    private static void AppendFinalTaskFocus(List<ChatMessage> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (!string.Equals(messages[i].Role, "system", StringComparison.OrdinalIgnoreCase))
                continue;

            messages[i] = ChatMessage.System(
                (messages[i].Content ?? string.Empty).TrimEnd() + FinalTaskFocusBlock);
            return;
        }
    }

    // Few-shot injection delegates to the shared PersonalityFewShotInjector
    // so this step and the tool-loop executor can't drift from each other.
    private static void InjectFewShotExamples(
        List<ChatMessage> messages,
        IReadOnlyList<PersonalityEngine.Profiles.PersonalityFewShotExample>? examples)
        => PersonalityFewShotInjector.InjectInPlace(messages, examples);
}
