using System.Text.RegularExpressions;
using System.Text.Json;
using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Agent;

/// <summary>
/// Public-facing wrapper around the internal response-scrubbing helpers in
/// <see cref="OrchestratorMessageHelpers"/>. Callers outside this assembly
/// (e.g. the runtime-side chat pipeline in <c>LmStudioAssistant</c>) use this
/// to strip chain-of-thought scaffolding and chat-template token leaks from
/// model output before streaming it to the UI or persisting it to history.
/// </summary>
public static class AssistantResponseSanitizer
{
    /// <summary>
    /// Removes &lt;think&gt; blocks and labelled "Thinking: … Answer:" sections
    /// emitted by chain-of-thought models. When <paramref name="preserveRationale"/>
    /// is true the text is returned unchanged.
    /// </summary>
    public static string StripThinkingScaffold(
        string text,
        bool preserveRationale = false,
        bool preserveFinalAnswerLabel = false)
        => OrchestratorMessageHelpers.StripThinkingScaffold(
            text,
            preserveRationale,
            preserveFinalAnswerLabel);

    /// <summary>
    /// Strips raw chat-template tokens (harmony / Llama / Mistral / ChatML)
    /// that small models sometimes leak as literal text — including the
    /// bare-bracket variant (<c>&lt;channel&gt;thought &lt;channel&gt;...</c>)
    /// seen on some gpt-oss builds.
    /// </summary>
    public static string StripRawTemplateTokens(string text)
        => OrchestratorMessageHelpers.StripRawTemplateTokens(text);

    /// <summary>
    /// Convenience: run the full chat-reply cleanup pipeline. Safe to call
    /// on empty input.
    /// </summary>
    public static string CleanChatReply(string text)
        => CleanChatReply(text, latestUserMessage: null);

    /// <summary>
    /// Runs the full cleanup pipeline while preserving an explicitly requested
    /// labeled final-answer line. Ordinary replies retain the historical
    /// behavior of removing that presentation label.
    /// </summary>
    public static string CleanChatReply(string text, string? latestUserMessage)
    {
        var preserveFinalAnswerLabel =
            ExplicitResponseContractDetector.RequiresLabeledFinalAnswerLine(latestUserMessage);
        text = StripThinkingScaffold(
            text,
            preserveFinalAnswerLabel: preserveFinalAnswerLabel);
        text = StripRawTemplateTokens(text);
        return text;
    }

    public static string NormalizeJsonOnlyReply(string text, string? latestUserMessage)
    {
        if (string.IsNullOrWhiteSpace(text) || !PromptRequestsJsonOnly(latestUserMessage))
            return text;

        var trimmed = text.Trim();
        var candidate = ExtractFencedJson(trimmed) ?? trimmed;
        if (!IsValidJson(candidate))
            return text;

        return candidate.Trim();
    }

    private static bool PromptRequestsJsonOnly(string? latestUserMessage)
    {
        if (string.IsNullOrWhiteSpace(latestUserMessage))
            return false;

        var lower = latestUserMessage.ToLowerInvariant();
        return lower.Contains("json", StringComparison.Ordinal) &&
               (lower.Contains("return only", StringComparison.Ordinal) ||
                lower.Contains("reply only", StringComparison.Ordinal) ||
                lower.Contains("only valid json", StringComparison.Ordinal) ||
                lower.Contains("json only", StringComparison.Ordinal));
    }

    private static string? ExtractFencedJson(string text)
    {
        var match = Regex.Match(
            text,
            @"^\s*```(?:json)?\s*(?<json>[\s\S]*?)\s*```\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["json"].Value : null;
    }

    private static bool IsValidJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Phrases a small model defaults to when it thinks it has no tools —
    // even though browser_navigate / web_search / screen_capture exist and
    // (in the observed cases) just ran successfully a moment earlier. Only
    // matches at the START of a paragraph so legitimate content that
    // mentions the phrase in passing is left alone.
    private static readonly Regex AutomationRefusalLeadRegex = new(
        @"^\s*(?:" +
        @"i\s+can'?t|i\s+cannot|i\s+am\s+unable|i'?m\s+unable|i\s+am\s+not\s+able|i'?m\s+not\s+able|" +
        @"unfortunately,?\s+i\s+(?:can'?t|cannot|am\s+unable)|" +
        @"i\s+don'?t\s+have\s+(?:the\s+ability|access|a\s+way)|" +
        @"i\s+do\s+not\s+have\s+(?:the\s+ability|access|a\s+way)|" +
        @"would\s+you\s+like\s+me\s+to|do\s+you\s+want\s+me\s+to|" +
        @"if\s+you'?d\s+like\s+me\s+to|if\s+you\s+would\s+like\s+me\s+to|" +
        @"what\s+would\s+you\s+like\s+me\s+to|" +
        @"let\s+me\s+know\s+if|just\s+tell\s+me\s+and|" +
        @"i\s+can'?t\s+wait\s+for\s+you|i'?ll\s+check\s+up\s+on" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The terse stand-in when literally all the model produced was refusal.
    // Public const so tests can assert against it without stringly-typing.
    public const string AutomationRefusalPlaceholder =
        "_(step completed, but the model declined to use its tools)_";

    /// <summary>
    /// Collapses automation-run refusal output. Small local models regularly
    /// emit "I can't / I cannot / I'm unable / would you like me to…" even
    /// immediately after a tool call that actually succeeded — leaving the
    /// automation thread full of confusing apologies. During automation runs
    /// we strip every refusal paragraph; whatever real content remains is
    /// the step's output. If every paragraph is refusal we substitute a
    /// short italic placeholder so the bubble isn't empty.
    ///
    /// Applies to single-paragraph replies too (the original was too cautious
    /// and let single-paragraph refusals through unchanged).
    /// </summary>
    public static string CollapseAutomationRefusalLoop(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var paragraphs = text
            .Replace("\r\n", "\n")
            .Split("\n\n", StringSplitOptions.None)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        if (paragraphs.Count == 0)
            return text;

        var kept = new List<string>(paragraphs.Count);
        foreach (var p in paragraphs)
        {
            if (AutomationRefusalLeadRegex.IsMatch(p)) continue;
            kept.Add(p);
        }

        if (kept.Count == 0)
            return AutomationRefusalPlaceholder;

        // If the only thing we removed was whitespace-normalization, return
        // the original to avoid needlessly munging multi-line formatting.
        if (kept.Count == paragraphs.Count) return text;

        return string.Join("\n\n", kept);
    }
}
