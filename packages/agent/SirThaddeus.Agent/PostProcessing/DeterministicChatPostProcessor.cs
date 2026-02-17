using static SirThaddeus.Agent.OrchestratorMessageHelpers;
using SirThaddeus.Agent.Search;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.PostProcessing;

/// <summary>
/// Deterministic text cleanup and safety clamps.
/// No LLM rewrite pass is allowed in this stage.
/// </summary>
public sealed class DeterministicChatPostProcessor
{
    public string ProcessChatOnlyDraft(
        string draftText,
        string userMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        Action<string, string>? logEvent = null)
    {
        var text = SanitizeCommon(draftText);

        if (LooksLikeThinkingLeak(text))
        {
            logEvent?.Invoke("AGENT_THINKING_LEAK", "Detected internal reasoning leakage.");
            return "I got ahead of myself. Ask that again and I will answer directly.";
        }

        if (LooksLikeUnsolicitedCalculation(userMessage, text))
        {
            // Keep legacy audit action for compatibility with existing tests/dashboards.
            logEvent?.Invoke("AGENT_OFFTOPIC_CALC_REWRITE", "Detected off-topic calculation style response.");
            return "Let's keep it respectful. I'm here to help with a real question when you're ready.";
        }

        if (LooksLikeRoleConfusedMathAsk(userMessage, text))
        {
            // Keep legacy audit action for compatibility with existing tests/dashboards.
            logEvent?.Invoke("AGENT_ROLE_CONFUSION_REWRITE", "Detected assistant role confusion on non-math turn.");
            return "I'm doing well, thanks for checking in. How can I help you right now?";
        }

        if (LooksLikeUnsafeMirroringResponse(userMessage, text))
        {
            logEvent?.Invoke("AGENT_SAFETY_OVERRIDE", "Detected unsafe mirrored language.");
            return BuildRespectfulResetReply();
        }

        if (LooksLikeAbusiveUserTurn(userMessage))
        {
            logEvent?.Invoke("AGENT_ABUSIVE_USER_BOUNDARY", "Detected abusive user turn; returning boundary response.");
            return BuildRespectfulResetReply();
        }

        if (TryResolveClassicReasoningOverride(userMessage, out var deterministicOverride))
        {
            logEvent?.Invoke("AGENT_CLASSIC_REASONING_OVERRIDE", "Applied deterministic override for classic reasoning prompt.");
            return deterministicOverride;
        }

        return text;
    }

    public string SanitizeFinalResponse(
        string text,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        string? latestUserMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? "";

        var hasEmailToolEvidence = toolCallsMade.Any(t => LooksLikeEmailToolName(t.ToolName));
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var filtered = new List<string>(lines.Length);
        var removedUnsupportedDispatch = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (IsInternalMarkerLine(trimmed))
                continue;

            if (!hasEmailToolEvidence && LooksLikeUnsupportedEmailDispatchLine(trimmed))
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
            return (text ?? "").Trim();

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
        sanitized = SanitizeCommon(sanitized);
        sanitized = SourceCitationFormatter.Apply(sanitized, toolCallsMade);

        if (LooksLikeUnsafeMirroringResponse(userMessage: null, assistantText: sanitized))
            return BuildRespectfulResetReply();

        // Guard against bare responses (e.g. "Yes", "No") that lack substance.
        // When the LLM returns a very short answer to a question, nudge the
        // user to ask a follow-up so the experience doesn't feel hollow.
        if (IsBareResponse(sanitized) && IsLikelyQuestion(latestUserMessage))
            sanitized = EnrichBareResponse(sanitized);

        return sanitized;
    }

    private static string SanitizeCommon(string text)
    {
        var output = StripThinkingScaffold(text ?? "[No response]");
        output = TruncateSelfDialogue(output);
        output = StripRawTemplateTokens(output);
        output = TrimDanglingIncompleteEnding(output);
        return output;
    }

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

    // ── Bare response detection ────────────────────────────────────
    // Small models sometimes return "Yes", "No", or a single bare
    // sentence. This is a poor experience — enrich them with a nudge.

    private static bool IsBareResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim().TrimEnd('.', '!', '?');
        var lower = trimmed.ToLowerInvariant();

        // Exact bare affirmations/negations (with or without punctuation)
        ReadOnlySpan<string> bareTokens =
        [
            "yes", "no", "yeah", "yep", "nope",
            "sure", "correct", "incorrect",
            "true", "false", "negative", "affirmative"
        ];

        foreach (var token in bareTokens)
        {
            if (lower == token)
                return true;
        }

        return false;
    }

    private static bool IsLikelyQuestion(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var trimmed = userMessage.Trim();
        if (trimmed.EndsWith('?'))
            return true;

        var lower = trimmed.ToLowerInvariant();
        return lower.StartsWith("is ", StringComparison.Ordinal) ||
               lower.StartsWith("are ", StringComparison.Ordinal) ||
               lower.StartsWith("does ", StringComparison.Ordinal) ||
               lower.StartsWith("do ", StringComparison.Ordinal) ||
               lower.StartsWith("can ", StringComparison.Ordinal) ||
               lower.StartsWith("will ", StringComparison.Ordinal) ||
               lower.StartsWith("would ", StringComparison.Ordinal) ||
               lower.StartsWith("should ", StringComparison.Ordinal) ||
               lower.StartsWith("what ", StringComparison.Ordinal) ||
               lower.StartsWith("when ", StringComparison.Ordinal) ||
               lower.StartsWith("where ", StringComparison.Ordinal) ||
               lower.StartsWith("how ", StringComparison.Ordinal);
    }

    private static string EnrichBareResponse(string bareText)
    {
        var trimmed = bareText.Trim();
        var lower = trimmed.ToLowerInvariant().TrimEnd('.', '!');

        // Affirmative bare answers get a helpful follow-up prompt
        if (lower is "yes" or "yeah" or "yep" or "correct" or "true" or "sure")
            return $"{trimmed} — want me to dig up more details on that?";

        // Negative bare answers
        if (lower is "no" or "nope" or "false" or "incorrect" or "negative")
            return $"{trimmed} — would you like me to look into why, or find alternatives?";

        // Generic short answer: append a conversational continuation
        return $"{trimmed}\n\nNeed me to expand on that?";
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

    private static bool TryResolveClassicReasoningOverride(string userMessage, out string answer)
    {
        return ClassicReasoningEngine.TryBuildCarWashReasoning(userMessage, out answer);
    }
}
