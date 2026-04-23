using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that conditionally appends a "verify existence via web_search"
/// suffix to the system prompt when the user message looks like an existence
/// or release check (e.g. "Does iPhone 15 exist?", "Was X released?").
///
/// <para>This matters because local-LLM training cutoffs are months to years
/// stale. A 4B-class model will confidently answer "yes iPhone 15 exists"
/// from memory, but the correct product behavior for existence-shaped
/// questions is to verify against live search before claiming release status
/// of any specific product, event, or entity.</para>
///
/// <para>The step is pattern-gated on purpose — an earlier version injected
/// the verification hint unconditionally and pushed 4B models to call
/// web_search on casual questions like "tell me about your favorite topic",
/// which regressed tool-hygiene tests. Keep the pattern narrow.</para>
///
/// <para>Place this step anywhere between <c>PersonalityInjectionStep</c>
/// and <c>FootmanRouterStep</c> so the hint is visible before tool
/// classification, but after personality wrapping so voice stays intact.</para>
/// </summary>
public sealed class ExistenceVerificationHintStep : ITurnStep
{
    public string Name => "ExistenceVerificationHint";

    // Kept deliberately tight. Matches shapes like:
    //   "does X exist(s/ed)?"
    //   "did X come out / release / debut?"
    //   "is/was/were X real / released / available / announced?"
    //   "has X been released / announced?"
    // Rejects general "is it raining", "was the meeting good", etc. by
    // requiring the existence/release vocabulary.
    private static readonly Regex ExistencePattern = new(
        @"\b(does|did|has|have|is|was|were)\s+" +
        @"(?:a\s+|an\s+|the\s+|some\s+)?[\w\-\.]+.*?\b" +
        @"(exist|exists|existed|real|released?|come\s+out|available|announce[ds]?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string HintSuffix =
        "This turn looks like an existence/release check. ALWAYS call " +
        "web_search to verify before answering — your training cutoff may " +
        "be months or years stale, so even confident recall must be " +
        "checked against live data.";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.UserText))
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        if (!ExistencePattern.IsMatch(context.UserText))
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var updated = PromptSuffixAppender.Append(context.LlmMessages, HintSuffix);
        return Task.FromResult<StepResult>(
            new StepResult.Continue(context with { LlmMessages = updated }));
    }
}
