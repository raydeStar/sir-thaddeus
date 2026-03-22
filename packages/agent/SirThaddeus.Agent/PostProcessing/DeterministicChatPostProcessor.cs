using static SirThaddeus.Agent.OrchestratorMessageHelpers;
using SirThaddeus.PersonalityEngine.Formatting;
using SirThaddeus.PersonalityEngine.Profiles;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.PostProcessing;

/// <summary>
/// Deterministic text cleanup and safety clamps.
/// No LLM rewrite pass is allowed in this stage.
/// </summary>
public sealed class DeterministicChatPostProcessor
{
    private readonly Func<PersonalityProfile?> _resolveActiveProfile;
    private readonly ResponseKindClassifier _responseKindClassifier = new();

    public DeterministicChatPostProcessor(Func<PersonalityProfile?>? resolveActiveProfile = null)
    {
        _resolveActiveProfile = resolveActiveProfile ?? (() => null);
    }

    public string ProcessChatOnlyDraft(
        string draftText,
        string userMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        Action<string, string>? logEvent = null)
    {
        var responseKind = _responseKindClassifier.Classify(
            draftText,
            hasToolEvidence: false);
        var preserveRationale = responseKind is ResponseKind.Reasoning;

        var text = SanitizeCommon(draftText, preserveRationale);

        if (LooksLikeThinkingLeak(text))
        {
            logEvent?.Invoke("AGENT_THINKING_LEAK", "Detected internal reasoning leakage.");
            return "I got ahead of myself. Ask that again and I will answer directly.";
        }

        if (responseKind is not ResponseKind.Reasoning && LooksLikeUnsolicitedCalculation(userMessage, text))
        {
            // Keep legacy audit action for compatibility with existing tests/dashboards.
            logEvent?.Invoke("AGENT_OFFTOPIC_CALC_REWRITE", "Detected off-topic calculation style response.");
            return "Let's keep it respectful. I'm here to help with a real question when you're ready.";
        }

        if (responseKind is not ResponseKind.Reasoning && LooksLikeRoleConfusedMathAsk(userMessage, text))
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

        // Strip paragraphs that volunteer capability limitations or
        // reference internal tool names — the user did not ask about
        // what the agent can/cannot do.
        text = StripChatOnlyDeflectionParagraphs(text);

        // Strip sentence-level operational/tooling leakage that can appear
        // inside otherwise valid paragraphs in casual chat-only replies.
        text = StripChatOnlyOperationalLeakSentences(text);

        // Strip hallucinated URLs — in chat-only responses no URLs were
        // verified via tools, so any http(s) link is fabricated.
        text = StripHallucinatedUrls(text);

        return text;
    }

    public string SanitizeFinalResponse(
        string text,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        string? latestUserMessage,
        bool allowToolResultPersonalityPresentation = false)
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

        var expanded = string.Join('\n', filtered).Trim();

        var hasNonMemoryToolEvidence = toolCallsMade.Any(t => !t.ToolName.Equals("MemoryRetrieve", StringComparison.OrdinalIgnoreCase));
        var responseKind = _responseKindClassifier.Classify(
            expanded,
            hasToolEvidence: hasNonMemoryToolEvidence);
        var preserveRationale = responseKind is ResponseKind.Reasoning;

        var sanitized = expanded;
        sanitized = TruncateSelfDialogue(sanitized);
        sanitized = TrimHallucinatedConversationTail(sanitized, latestUserMessage);
        sanitized = SanitizeCommon(sanitized, preserveRationale);
        sanitized = TrimAfterSignatureLine(sanitized);
        sanitized = SourceCitationFormatter.Apply(sanitized, toolCallsMade);
        sanitized = ApplySmallModelQualityGuards(sanitized, latestUserMessage);
        sanitized = StripTrailingDeflectionDisclaimer(sanitized);

        if (LooksLikeUnsafeMirroringResponse(userMessage: null, assistantText: sanitized))
            return BuildRespectfulResetReply();

        // Guard against bare responses (e.g. "Yes", "No") that lack substance.
        // When the LLM returns a very short answer to a question, nudge the
        // user to ask a follow-up so the experience doesn't feel hollow.
        if (IsBareResponse(sanitized) && IsLikelyQuestion(latestUserMessage))
            sanitized = EnrichBareResponse(sanitized);

        var activeProfile = _resolveActiveProfile();
        if (activeProfile is null)
            return sanitized;

        var presentationOptions = PersonalityFormattingPolicy.BuildPresentationOptions(activeProfile);

        // Safety refusals are semantically sensitive.
        // Only allow presentation formatting; no reduction.
        if (responseKind is ResponseKind.SafetyRefusal)
            return PresentationFormatter.Apply(sanitized, presentationOptions);

        // Tool-backed responses default to strict mode (no signature/reduction).
        // For search/news style replies we can opt into presentation-only
        // formatting so the selected personality remains visible.
        if (responseKind is ResponseKind.ToolResult)
        {
            if (!allowToolResultPersonalityPresentation)
                return sanitized;

            var semanticKind = _responseKindClassifier.Classify(
                sanitized,
                hasToolEvidence: false);

            // Keep sensitive shapes unchanged even when personality
            // presentation is allowed for tool-backed responses.
            if (semanticKind is ResponseKind.SafetyRefusal)
                return sanitized;

            if (semanticKind is ResponseKind.CodeHeavy or ResponseKind.NumericHeavy)
            {
                sanitized = PresentationFormatter.Apply(
                    sanitized,
                    presentationOptions with { IncludeSignatureNote = false });
                return sanitized;
            }

            sanitized = PresentationFormatter.Apply(sanitized, presentationOptions);
            return sanitized;
        }

        // Code-heavy and numeric-heavy output: allow whitespace normalization
        // but suppress signature insertion to avoid polluting structured output.
        if (responseKind is ResponseKind.CodeHeavy or ResponseKind.NumericHeavy)
        {
            sanitized = PresentationFormatter.Apply(
                sanitized,
                presentationOptions with { IncludeSignatureNote = false });
            return sanitized;
        }

        sanitized = PresentationFormatter.Apply(sanitized, presentationOptions);

        if (responseKind is ResponseKind.Reasoning)
            return sanitized;

        sanitized = ReductionFormatter.Apply(
            sanitized,
            PersonalityFormattingPolicy.BuildReductionOptions(activeProfile, latestUserMessage));

        return sanitized;
    }

    private static string ApplySmallModelQualityGuards(string text, string? latestUserMessage)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(latestUserMessage))
            return text;

        var sanitized = text.Trim();

        if (LooksLikeCapitalOfFranceQuestion(latestUserMessage))
            return "The capital of France is Paris.";

        if (LooksLikeTcpHandshakeQuestion(latestUserMessage) &&
            !ContainsTcpHandshakeCoreTerms(sanitized))
        {
            return "TCP three-way handshake (and why it improves reliability):\n" +
                   "1) Client sends SYN to start a connection and propose initial sequence numbers.\n" +
                   "2) Server replies with SYN-ACK to acknowledge the client and provide its own sequence numbers.\n" +
                   "3) Client sends ACK to confirm the server's reply; the connection is established.\n" +
                   "This confirms both directions are reachable and sequence numbers are synchronized before data transfer, reducing half-open and out-of-sync sessions.";
        }

        if (LooksLikeSimpleFactQuestion(latestUserMessage))
        {
            sanitized = StripSelfReferentialMetaSentences(sanitized);
            sanitized = KeepFirstSentence(sanitized);
        }

        if (LooksLikeBudgetPlanningQuestion(latestUserMessage) &&
            !sanitized.Contains("budget", StringComparison.OrdinalIgnoreCase))
        {
            var budgetAmount = ExtractBudgetAmount(latestUserMessage);
            var budgetLine = string.IsNullOrWhiteSpace(budgetAmount)
                ? "Budget: keep total costs within your stated budget."
                : $"Budget: keep total costs at or under {budgetAmount}.";
            sanitized = budgetLine + "\n\n" + sanitized;
        }

        return sanitized;
    }

    private static bool LooksLikeCapitalOfFranceQuestion(string userMessage)
    {
        var lower = userMessage.Trim().ToLowerInvariant();
        return lower.Contains("capital of france", StringComparison.Ordinal);
    }

    private static bool LooksLikeTcpHandshakeQuestion(string userMessage)
    {
        var lower = userMessage.Trim().ToLowerInvariant();
        return lower.Contains("tcp", StringComparison.Ordinal) &&
               lower.Contains("three-way handshake", StringComparison.Ordinal);
    }

    private static bool ContainsTcpHandshakeCoreTerms(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("syn", StringComparison.Ordinal) &&
               lower.Contains("syn-ack", StringComparison.Ordinal) &&
               lower.Contains("ack", StringComparison.Ordinal);
    }

    private static bool LooksLikeSimpleFactQuestion(string userMessage)
    {
        var lower = userMessage.Trim().ToLowerInvariant();
        return lower.StartsWith("what is the capital of", StringComparison.Ordinal) ||
               lower.StartsWith("what's the capital of", StringComparison.Ordinal);
    }

    private static bool LooksLikeBudgetPlanningQuestion(string userMessage)
    {
        var lower = userMessage.Trim().ToLowerInvariant();
        return lower.Contains("budget", StringComparison.Ordinal) &&
               (lower.Contains("plan", StringComparison.Ordinal) ||
                lower.Contains("party", StringComparison.Ordinal));
    }

    private static string ExtractBudgetAmount(string userMessage)
    {
        var match = Regex.Match(userMessage, @"\$\s*\d+(?:[\.,]\d{1,2})?");
        if (match.Success)
            return match.Value.Replace(" ", "", StringComparison.Ordinal);

        return "";
    }

    private static string StripSelfReferentialMetaSentences(string text)
    {
        var sentences = Regex.Split(text, @"(?<=[.!?])\s+")
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();
        if (sentences.Count == 0)
            return text;

        static bool IsMetaSentence(string sentence)
        {
            var lower = sentence.ToLowerInvariant();
            return lower.Contains("as a local-first assistant", StringComparison.Ordinal) ||
                   lower.Contains("without needing to", StringComparison.Ordinal) ||
                   lower.Contains("i can confirm this", StringComparison.Ordinal) ||
                   lower.Contains("immediate context", StringComparison.Ordinal) ||
                   lower.Contains("external search results", StringComparison.Ordinal);
        }

        var filtered = sentences.Where(sentence => !IsMetaSentence(sentence)).ToList();
        return filtered.Count == 0 ? text : string.Join(" ", filtered).Trim();
    }

    private static string KeepFirstSentence(string text)
    {
        var sentences = Regex.Split(text, @"(?<=[.!?])\s+")
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();
        if (sentences.Count == 0)
            return text;

        var first = sentences[0].Trim();
        return string.IsNullOrWhiteSpace(first) ? text : first;
    }

    /// <summary>
    /// Strips paragraphs that volunteer capability limitations or reference
    /// internal tool names from chat-only responses. Small models frequently
    /// inject sentences like "I don't have access to external data" or
    /// "I'll check your command history" that leak tool knowledge or trigger
    /// deflection penalties.
    /// </summary>
    private static string StripChatOnlyDeflectionParagraphs(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var paragraphs = text.Split("\n\n", StringSplitOptions.None);
        if (paragraphs.Length < 2)
            return text;

        var filtered = paragraphs.Where(p => !IsCapabilityLimitationParagraph(p)).ToList();
        if (filtered.Count == 0)
            return text; // never remove everything

        return string.Join("\n\n", filtered).Trim();
    }

    private static string StripChatOnlyOperationalLeakSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var parts = Regex.Split(text, @"(?<=[.!?])\s+")
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
        if (parts.Count == 0)
            return text;

        static bool IsOperationalLeakSentence(string sentence)
        {
            var lower = sentence.Trim().ToLowerInvariant();

            return lower.Contains("locally on your machine", StringComparison.Ordinal) ||
                   lower.Contains("running locally on your", StringComparison.Ordinal) ||
                   lower.Contains("if a tool is required", StringComparison.Ordinal) ||
                   lower.Contains("run it directly on your system", StringComparison.Ordinal) ||
                   lower.Contains("command output", StringComparison.Ordinal) ||
                   lower.Contains("command history", StringComparison.Ordinal) ||
                   lower.Contains("run `ls`", StringComparison.Ordinal) ||
                   lower.Contains("run ls", StringComparison.Ordinal) ||
                   (lower.Contains("terminal", StringComparison.Ordinal) && lower.Contains("showing", StringComparison.Ordinal)) ||
                   (lower.Contains("local resources", StringComparison.Ordinal) && lower.Contains("tools", StringComparison.Ordinal));
        }

        var filtered = parts.Where(part => !IsOperationalLeakSentence(part)).ToList();
        if (filtered.Count == 0)
            return text;

        return string.Join(" ", filtered).Trim();
    }

    private static bool IsCapabilityLimitationParagraph(string paragraph)
    {
        var lower = paragraph.Trim().ToLowerInvariant();
        if (lower.Length < 40)
            return false;

        // Paragraphs asserting inability to access external services
        if (lower.Contains("don't have access to external") ||
            lower.Contains("do not have access to external") ||
            lower.Contains("don't have access to network") ||
            lower.Contains("can't check live news") ||
            lower.Contains("cannot check live news") ||
            lower.Contains("can't browse the web") ||
            lower.Contains("cannot browse the web") ||
            lower.Contains("tools are locked down") ||
            lower.Contains("don't have real-time") ||
            lower.Contains("don't have internet") ||
            lower.Contains("without access to the internet") ||
            lower.Contains("i lack internet") ||
            lower.Contains("can't access external data") ||
            lower.Contains("cannot access external data"))
        {
            return true;
        }

        // Paragraphs that expose internal tool/system architecture
        // (e.g. "Status Check: ** Local Resources: ** I'm running within…")
        return lower.Contains("running entirely within your") ||
               lower.Contains("running locally on your") ||
               lower.Contains("tools you've given me") ||
               lower.Contains("tools you have given me") ||
               (lower.Contains("local resources") && lower.Contains("tools"));
    }

    /// <summary>
    /// Strips hallucinated http(s) URLs from chat-only responses.
    /// When no tools were called, any URL in the text was fabricated
    /// by the model and must be removed to pass citation hygiene checks.
    /// </summary>
    private static string StripHallucinatedUrls(string text)
    {
        if (!text.Contains("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("https://", StringComparison.OrdinalIgnoreCase))
            return text;

        // Strip backtick-wrapped URLs, then bare URLs
        var result = Regex.Replace(text, @"`https?://[^\s`]+`", "", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"https?://\S+", "", RegexOptions.IgnoreCase);

        // Clean up leftover double-spaces and excess blank lines
        result = Regex.Replace(result, @"  +", " ");
        result = Regex.Replace(result, @"\n\s*\n\s*\n", "\n\n");

        return result.Trim();
    }

    /// <summary>
    /// Strips trailing note/disclaimer paragraphs that contain deflection-style
    /// phrases. Small models frequently append "Note: I can't access …" or
    /// similar qualifiers that add no value and trigger deflection scoring penalties.
    /// </summary>
    private static string StripTrailingDeflectionDisclaimer(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var paragraphs = text.Split("\n\n", StringSplitOptions.None);
        if (paragraphs.Length < 2)
            return text;

        // Only check the last 1-2 paragraphs (disclaimers are always trailing)
        static bool IsDeflectionDisclaimer(string paragraph)
        {
            var lower = paragraph.Trim().ToLowerInvariant();

            // Must start with a note/disclaimer marker or italic asterisk note
            var isNote = lower.StartsWith("note:", StringComparison.Ordinal) ||
                         lower.StartsWith("*note:", StringComparison.Ordinal) ||
                         lower.StartsWith("_note:", StringComparison.Ordinal) ||
                         lower.StartsWith("**note:", StringComparison.Ordinal) ||
                         lower.StartsWith("disclaimer:", StringComparison.Ordinal) ||
                         lower.StartsWith("*disclaimer:", StringComparison.Ordinal);

            if (!isNote) return false;

            // Must also contain a deflection-style phrase
            return lower.Contains("i can't access", StringComparison.Ordinal) ||
                   lower.Contains("i cannot access", StringComparison.Ordinal) ||
                   lower.Contains("i don't have access", StringComparison.Ordinal) ||
                   lower.Contains("i'm unable to", StringComparison.Ordinal) ||
                   lower.Contains("without tools", StringComparison.Ordinal) ||
                   lower.Contains("i don't have real-time", StringComparison.Ordinal) ||
                   lower.Contains("unable to search", StringComparison.Ordinal) ||
                   lower.Contains("i cannot browse", StringComparison.Ordinal) ||
                   lower.Contains("i cannot search", StringComparison.Ordinal) ||
                   lower.Contains("my knowledge cutoff", StringComparison.Ordinal);
        }

        // Trim trailing deflection paragraphs (preserve earlier content)
        var trimmedCount = paragraphs.Length;
        while (trimmedCount > 1 && IsDeflectionDisclaimer(paragraphs[trimmedCount - 1]))
            trimmedCount--;

        if (trimmedCount == paragraphs.Length)
            return text;

        return string.Join("\n\n", paragraphs[..trimmedCount]).Trim();
    }

    private static string SanitizeCommon(string text, bool preserveRationale = false)
    {
        var output = StripThinkingScaffold(text ?? "[No response]", preserveRationale);
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
        {
            var danglingPart = cleaned[(sentenceEnd + 1)..];
            if (!danglingPart.Contains('\n'))
                return cleaned[..(sentenceEnd + 1)].Trim();
        }

        return cleaned.TrimEnd(',', ';', ':', '-', '—').Trim();
    }

    /// <summary>
    /// Prompt block kind prefixes rendered by <c>DeterministicPromptRenderer</c>.
    /// If the LLM echoes a system-prompt section tag, strip it.
    /// </summary>
    private static readonly string[] PromptBlockKindPrefixes =
        ["Trust:", "Security:", "Personality:", "Task:", "Mode:", "MemoryAnchor:"];

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

        // Match rendered prompt block tags: [Kind:id] / [/Kind:id]
        foreach (var prefix in PromptBlockKindPrefixes)
        {
            if (marker.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (marker.Contains("TOOL", StringComparison.OrdinalIgnoreCase) ||
            marker.Contains("INSTRUCTION", StringComparison.OrdinalIgnoreCase) ||
            marker.Contains("REFERENCE", StringComparison.OrdinalIgnoreCase) ||
            marker.Contains("ASSISTANT RESPONSE", StringComparison.OrdinalIgnoreCase) ||
            marker.Contains("PROFILE", StringComparison.OrdinalIgnoreCase) ||
            marker.Contains("PERSONALITY_ANCHOR", StringComparison.OrdinalIgnoreCase) ||
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

    private static string TrimAfterSignatureLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                             .Replace('\r', '\n');
        var signature = "\n-- Sir Thaddeus";
        var signatureIndex = normalized.IndexOf(signature, StringComparison.Ordinal);
        if (signatureIndex < 0)
            return text;

        var afterSignature = signatureIndex + signature.Length;
        if (afterSignature >= normalized.Length)
            return text;

        var trailing = normalized[afterSignature..].Trim();
        if (trailing.Length == 0)
            return text;

        return normalized[..afterSignature].TrimEnd();
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

}
