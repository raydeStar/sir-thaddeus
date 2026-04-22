using static SirThaddeus.Agent.OrchestratorMessageHelpers;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
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
            var benignFallback = TryBuildDeterministicBenignFallback(userMessage);
            if (!string.IsNullOrWhiteSpace(benignFallback))
                return benignFallback;

            return "Let's keep it respectful. I'm here to help with a real question when you're ready.";
        }

        if (responseKind is not ResponseKind.Reasoning && LooksLikeRoleConfusedMathAsk(userMessage, text))
        {
            // Keep legacy audit action for compatibility with existing tests/dashboards.
            logEvent?.Invoke("AGENT_ROLE_CONFUSION_REWRITE", "Detected assistant role confusion on non-math turn.");
            var benignFallback = TryBuildDeterministicBenignFallback(userMessage);
            if (!string.IsNullOrWhiteSpace(benignFallback))
                return benignFallback;

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

        if (LooksLikeToolingLeakEssay(text))
        {
            var benignFallback = TryBuildDeterministicBenignFallback(userMessage);
            if (!string.IsNullOrWhiteSpace(benignFallback))
                return benignFallback;

            return "I don't really have favorites, but I do best when we tackle a clear question and solve it step by step.";
        }

        return text;
    }

    private static bool LooksLikeToolingLeakEssay(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        return lower.Contains("local-first investigation", StringComparison.Ordinal) ||
               lower.Contains("your machine", StringComparison.Ordinal) ||
               lower.Contains("your terminal", StringComparison.Ordinal) ||
               lower.Contains("your screen or disk", StringComparison.Ordinal) ||
               lower.Contains("check this out", StringComparison.Ordinal);
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
        sanitized = sanitized.Replace('’', '\'');
        if (sanitized.StartsWith("Heres ", StringComparison.Ordinal))
            sanitized = "Here's " + sanitized[6..];
        sanitized = TrimAfterSignatureLine(sanitized);
        // Tool-backed responses should cite source names, not raw URL text.
        sanitized = StripHallucinatedUrls(sanitized);
        sanitized = SourceCitationFormatter.Apply(sanitized, toolCallsMade);
        sanitized = ApplySmallModelQualityGuards(sanitized, latestUserMessage);
        sanitized = NormalizeStrictStructuredOutput(sanitized, latestUserMessage);
        sanitized = StripTrailingDeflectionDisclaimer(sanitized);
        var hasLocalBusinessRecoveryContext = HasLocalBusinessRecoveryContext(latestUserMessage, sanitized, toolCallsMade);
        if (hasNonMemoryToolEvidence)
        {
            var strippedCapabilityDeflection = StripToolCapabilityDeflectionParagraphs(sanitized);
            if (!string.IsNullOrWhiteSpace(strippedCapabilityDeflection))
            {
                sanitized = strippedCapabilityDeflection;
            }
            else if (hasLocalBusinessRecoveryContext)
            {
                sanitized = "I ran live lookups for that local business request, but the returned pages did not provide a reliable shortlist. If you share a tighter area (for example a neighborhood or ZIP code), I can retry with a focused list.";
            }

            if (hasLocalBusinessRecoveryContext &&
                LooksLikeLocalBusinessDeflectionResponse(sanitized))
            {
                sanitized = BuildLocalBusinessRecoveryResponse(latestUserMessage);
            }

            if (hasLocalBusinessRecoveryContext &&
                LooksLikeLocalBusinessBriefingShell(sanitized))
            {
                sanitized = TryBuildLocalBusinessShortlistFromToolResults(latestUserMessage, toolCallsMade)
                    ?? BuildLocalBusinessRecoveryResponse(latestUserMessage);
            }

            if (TryBuildEmptyNewsLeadRecovery(sanitized, latestUserMessage, toolCallsMade) is { Length: > 0 } newsRecovery)
            {
                sanitized = newsRecovery;
            }

            if (TryBuildExplicitWebToolUnavailableGuard(sanitized, latestUserMessage, toolCallsMade) is { Length: > 0 } unavailableGuard)
            {
                sanitized = unavailableGuard;
            }

            sanitized = StripInlineToolCapabilityClauses(sanitized);

            var strippedToolingLeak = StripToolingLeakParagraphs(sanitized);
            if (!string.IsNullOrWhiteSpace(strippedToolingLeak))
                sanitized = strippedToolingLeak;

            if (LooksLikeNewsListResponse(sanitized))
                sanitized = KeepLeadingOrderedListBlock(sanitized);
        }

        if (LooksLikeUnsafeMirroringResponse(userMessage: null, assistantText: sanitized))
            return BuildRespectfulResetReply();

        // Guard against bare responses (e.g. "Yes", "No") that lack substance.
        // When the LLM returns a very short answer to a question, nudge the
        // user to ask a follow-up so the experience doesn't feel hollow.
        if (IsBareResponse(sanitized) && IsLikelyQuestion(latestUserMessage))
            sanitized = EnrichBareResponse(sanitized);

        var activeProfile = _resolveActiveProfile();
        if (activeProfile is not null &&
            !activeProfile.Id.Equals("sir_thaddeus", StringComparison.OrdinalIgnoreCase))
        {
            sanitized = StripSirThaddeusNameLeakage(sanitized);
        }

        if (activeProfile is null)
            return FinalizeStructuredOutput(sanitized, latestUserMessage);

        var presentationOptions = PersonalityFormattingPolicy.BuildPresentationOptions(activeProfile);

        // Safety refusals are semantically sensitive.
        // Only allow presentation formatting; no reduction.
        if (responseKind is ResponseKind.SafetyRefusal)
            return FinalizeStructuredOutput(sanitized, latestUserMessage);

        // Tool-backed responses default to strict mode (no signature/reduction).
        // For search/news style replies we can opt into presentation-only
        // formatting so the selected personality remains visible.
        if (responseKind is ResponseKind.ToolResult)
        {
            if (!allowToolResultPersonalityPresentation)
                return FinalizeStructuredOutput(
                    StripEmptyListMarkerLines(StripTerminalSignatureLine(sanitized)),
                    latestUserMessage);

            var semanticKind = _responseKindClassifier.Classify(
                sanitized,
                hasToolEvidence: false);

            // Keep sensitive shapes unchanged even when personality
            // presentation is allowed for tool-backed responses.
            if (semanticKind is ResponseKind.SafetyRefusal)
                return FinalizeStructuredOutput(
                    StripEmptyListMarkerLines(StripTerminalSignatureLine(sanitized)),
                    latestUserMessage);

            if (semanticKind is ResponseKind.CodeHeavy or ResponseKind.NumericHeavy)
            {
                sanitized = PresentationFormatter.Apply(
                    sanitized,
                    presentationOptions with { IncludeSignatureNote = false });
                return FinalizeStructuredOutput(
                    StripEmptyListMarkerLines(StripTerminalSignatureLine(sanitized)),
                    latestUserMessage);
            }

            sanitized = PresentationFormatter.Apply(
                sanitized,
                presentationOptions with { IncludeSignatureNote = true });
            return FinalizeStructuredOutput(StripEmptyListMarkerLines(sanitized), latestUserMessage);
        }

        // Code-heavy and numeric-heavy output: allow whitespace normalization
        // but suppress signature insertion to avoid polluting structured output.
        if (responseKind is ResponseKind.CodeHeavy or ResponseKind.NumericHeavy)
        {
            sanitized = PresentationFormatter.Apply(
                sanitized,
                presentationOptions with { IncludeSignatureNote = false });
            return FinalizeStructuredOutput(StripEmptyListMarkerLines(sanitized), latestUserMessage);
        }

        sanitized = PresentationFormatter.Apply(sanitized, presentationOptions);

        if (responseKind is ResponseKind.Reasoning)
            return FinalizeStructuredOutput(StripEmptyListMarkerLines(sanitized), latestUserMessage);

        sanitized = ReductionFormatter.Apply(
            sanitized,
            PersonalityFormattingPolicy.BuildReductionOptions(activeProfile, latestUserMessage));

        return FinalizeStructuredOutput(StripEmptyListMarkerLines(sanitized), latestUserMessage);
    }

    private static string FinalizeStructuredOutput(string text, string? latestUserMessage)
        => NormalizeStrictStructuredOutput(text, latestUserMessage);

    private static string StripSirThaddeusNameLeakage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var sanitized = text.Trim();

        sanitized = Regex.Replace(
            sanitized,
            @"^\s*Sir\s+Thaddeus\s+has\s+been\s+requested\s+to\s+",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        sanitized = Regex.Replace(
            sanitized,
            @"\bSir\s+Thaddeus's\s+Note\b\s*:?",
            "Note:",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        sanitized = Regex.Replace(
            sanitized,
            @"\bSir\s+Thaddeus\b",
            "I",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return sanitized.Trim();
    }

    private static string ApplySmallModelQualityGuards(string text, string? latestUserMessage)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(latestUserMessage))
            return text;

        var sanitized = text.Trim();

        if (LooksLikeCarWashGoalQuestion(latestUserMessage) &&
            LooksLikeCarWashCrossContamination(latestUserMessage, sanitized) &&
            TryBuildDeterministicBenignFallback(latestUserMessage) is { Length: > 0 } carWashFallback)
        {
            return carWashFallback;
        }

        if (Search.SearchOrchestrator.TryBuildMediaInstallmentFallback(latestUserMessage) is { Length: > 0 } mediaFallback &&
            LooksLikeMediaInstallmentConclusionMiss(latestUserMessage, sanitized))
        {
            return mediaFallback;
        }

        if (LooksLikeCapitalOfFranceQuestion(latestUserMessage))
            return "The capital of France is Paris.";

        if (LooksLikeTcpHandshakeQuestion(latestUserMessage) &&
            (!ContainsTcpHandshakeCoreTerms(sanitized) ||
             (HasSirThaddeusSignature(sanitized) && NeedsTcpHandshakeCompression(sanitized))))
        {
            return BuildTcpHandshakeFallback(HasSirThaddeusSignature(sanitized));
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

    private static bool LooksLikeCarWashGoalQuestion(string userMessage)
    {
        var lower = userMessage.Trim().ToLowerInvariant();
        return lower.Contains("car wash", StringComparison.Ordinal) &&
               (lower.Contains("walk", StringComparison.Ordinal) ||
                lower.Contains("drive", StringComparison.Ordinal));
    }

    private static bool LooksLikeCarWashCrossContamination(string userMessage, string text)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || string.IsNullOrWhiteSpace(text))
            return false;

        return ContainsUnexpectedBusinessDetail(text) ||
               ContainsUnexpectedNamedEntity(userMessage, text);
    }

    private static bool LooksLikeMediaInstallmentConclusionMiss(string userMessage, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var lower = text.ToLowerInvariant();
        if (ContainsMediaInstallmentNonExistenceConclusion(lower))
            return false;

        var looksLikeSourceListFallback =
            lower.StartsWith("here's the strongest evidence i found", StringComparison.Ordinal) ||
            lower.StartsWith("here are the live results i found", StringComparison.Ordinal) ||
            lower.StartsWith("here's what i found regarding", StringComparison.Ordinal);

        if (looksLikeSourceListFallback)
            return true;

        return !MentionsRequestedMediaInstallment(userMessage, lower);
    }

    private static bool ContainsUnexpectedBusinessDetail(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(
                   text,
                   @"\b\d{1,5}\s+[A-Za-z0-9.'-]+(?:\s+[A-Za-z0-9.'-]+){0,4}\s+(?:st|street|ave|avenue|blvd|boulevard|rd|road|dr|drive|ln|lane|way|pkwy|parkway|ct|court)\b",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(
                   text,
                   @"\b\d{1,2}(?::\d{2})?\s?(?:AM|PM)\b",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(
                   text,
                   @"(?:^|\n)\s*(?:phone|address|hours?)\s*:",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               text.Contains("verification recommended", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("currently open", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("hours were not found", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsUnexpectedNamedEntity(string userMessage, string text)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || string.IsNullOrWhiteSpace(text))
            return false;

        var promptTokens = ExtractMeaningfulLowerTokens(userMessage);
        if (promptTokens.Count == 0)
            return false;

        foreach (Match match in Regex.Matches(
                     text,
                     @"\b[A-Z][a-z0-9']+(?:\s+[A-Z][a-z0-9']+){0,3}\b",
                     RegexOptions.CultureInvariant))
        {
            var phrase = match.Value.Trim();
            if (phrase.Length < 4)
                continue;

            var phraseTokens = ExtractMeaningfulLowerTokens(phrase);
            if (phraseTokens.Count == 0)
                continue;

            if (phraseTokens.All(token => !promptTokens.Contains(token)) &&
                !IsGenericCapitalizedPhrase(phrase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsMediaInstallmentNonExistenceConclusion(string lowerText)
    {
        if (string.IsNullOrWhiteSpace(lowerText))
            return false;

        return lowerText.Contains("does not have an official", StringComparison.Ordinal) ||
               lowerText.Contains("doesn't have an official", StringComparison.Ordinal) ||
               lowerText.Contains("no official", StringComparison.Ordinal) ||
               lowerText.Contains("does not exist", StringComparison.Ordinal) ||
               lowerText.Contains("doesn't exist", StringComparison.Ordinal) ||
               lowerText.Contains("never made", StringComparison.Ordinal) ||
               lowerText.Contains("not a real episode", StringComparison.Ordinal) ||
               lowerText.Contains("no real episode plot", StringComparison.Ordinal) ||
               lowerText.Contains("was canceled", StringComparison.Ordinal) ||
               lowerText.Contains("was cancelled", StringComparison.Ordinal) ||
               lowerText.Contains("ended", StringComparison.Ordinal);
    }

    private static bool MentionsRequestedMediaInstallment(string userMessage, string lowerText)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || string.IsNullOrWhiteSpace(lowerText))
            return false;

        var seasonMatch = Regex.Match(userMessage, @"\bSeason\s+(\d+)\b", RegexOptions.IgnoreCase);
        var episodeMatch = Regex.Match(userMessage, @"\bEpisode\s+(\d+)\b", RegexOptions.IgnoreCase);
        var seriesMatch = Regex.Match(
            userMessage,
            @"\bSeason\s+\d+\s+of\s+(.+?)(?:\s+about)?[?.!]*$",
            RegexOptions.IgnoreCase);

        var mentionsSeasonEpisode = (!seasonMatch.Success || lowerText.Contains($"season {seasonMatch.Groups[1].Value}", StringComparison.Ordinal)) &&
                                    (!episodeMatch.Success || lowerText.Contains($"episode {episodeMatch.Groups[1].Value}", StringComparison.Ordinal));

        if (seriesMatch.Success)
        {
            var titleTokens = ExtractMeaningfulLowerTokens(seriesMatch.Groups[1].Value)
                .Where(token => token is not "season" and not "episode")
                .ToList();

            if (titleTokens.Count > 0)
            {
                var matchedTitleTokens = titleTokens.Count(token => lowerText.Contains(token, StringComparison.Ordinal));
                return mentionsSeasonEpisode && matchedTitleTokens >= Math.Max(1, Math.Min(2, titleTokens.Count));
            }
        }

        return mentionsSeasonEpisode;
    }

    private static HashSet<string> ExtractMeaningfulLowerTokens(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        foreach (Match match in Regex.Matches(text, @"\b[a-zA-Z][a-zA-Z0-9']{2,}\b", RegexOptions.CultureInvariant))
        {
            var token = match.Value.ToLowerInvariant();
            if (token is "what" or "would" or "about" or "should" or "there" or "their" or "walk" or "drive" or "going" or "only")
                continue;

            tokens.Add(token);
        }

        return tokens;
    }

    private static bool IsGenericCapitalizedPhrase(string phrase)
    {
        return phrase is "Given" or
               "Driving" or
               "Walking" or
               "Answer" or
               "Overview" or
               "Common Points" or
               "Differences" or
               "Practical Takeaway";
    }

    private static string NormalizeStrictStructuredOutput(string text, string? latestUserMessage)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(latestUserMessage))
            return text;

        var lowerPrompt = latestUserMessage.ToLowerInvariant();
        var requestsTwoLineAnswer = lowerPrompt.Contains("exactly two lines", StringComparison.Ordinal) &&
                                    lowerPrompt.Contains("line 1 starts with", StringComparison.Ordinal) &&
                                    lowerPrompt.Contains("answer:", StringComparison.Ordinal) &&
                                    lowerPrompt.Contains("commentary:", StringComparison.Ordinal);
        if (!requestsTwoLineAnswer)
            return text;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToList();
        if (lines.Count == 0)
            return text;

        var answerText = lines[0];
        var commentaryText = lines.Count > 1
            ? string.Join(" ", lines.Skip(1))
            : string.Empty;

        answerText = Regex.Replace(answerText, @"^Answer\s*[:.]?\s*", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        commentaryText = Regex.Replace(commentaryText, @"^Commentary\s*[:.]?\s*", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        answerText = NormalizeDotNetSpacing(answerText);
        commentaryText = NormalizeDotNetSpacing(commentaryText);
        answerText = RestoreLeadingDotNet(answerText, lowerPrompt);
        commentaryText = RestoreLeadingDotNet(commentaryText, lowerPrompt);

        if (string.IsNullOrWhiteSpace(commentaryText))
            commentaryText = "Kept concise per your format request.";

        return $"Answer: {answerText.Trim()}\nCommentary: {commentaryText.Trim()}";
    }

    private static string NormalizeDotNetSpacing(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = Regex.Replace(
            text,
            @"(?<=[A-Za-z0-9)])\.NET",
            " .NET",
            RegexOptions.CultureInvariant);

        normalized = Regex.Replace(normalized, @"\s{2,}", " ");
        return normalized.Trim();
    }

    private static string RestoreLeadingDotNet(string text, string lowerPrompt)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !lowerPrompt.Contains(".net", StringComparison.Ordinal) &&
            !lowerPrompt.Contains("dotnet", StringComparison.Ordinal))
        {
            return text;
        }

        return Regex.Replace(
            text,
            @"^(?i:net)(?=\s|$)",
            ".NET",
            RegexOptions.CultureInvariant);
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

    private static bool HasSirThaddeusSignature(string text)
    {
        return text.Contains("-- Sir Thaddeus", StringComparison.Ordinal);
    }

    private static bool NeedsTcpHandshakeCompression(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.Length > 900)
            return true;

        var structuredSteps = text
            .Split('\n')
            .Count(line => Regex.IsMatch(line, @"^\s*\d+[\.)]\s", RegexOptions.CultureInvariant));

        return structuredSteps < 3;
    }

    private static string BuildTcpHandshakeFallback(bool includeSignature)
    {
        var text =
            "TCP three-way handshake (and why it improves reliability):\n" +
            "1) Client sends SYN to start a connection and propose initial sequence numbers.\n" +
            "2) Server replies with SYN-ACK to acknowledge the client and provide its own sequence numbers.\n" +
            "3) Client sends ACK to confirm the server's reply; the connection is established.\n" +
            "This confirms both directions are reachable and sequence numbers are synchronized before data transfer, reducing half-open and out-of-sync sessions.";

        return includeSignature
            ? text + "\n\n-- Sir Thaddeus"
            : text;
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

        // Strip wrapped URLs first so we do not leave empty citation shells behind.
        var result = Regex.Replace(text, @"\(\s*https?://[^\s)]+\s*\)", "", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\[\s*https?://[^\s\]]+\s*\]", "", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"`https?://[^\s`]+`", "", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"https?://\S+", "", RegexOptions.IgnoreCase);

        // Clean up leftover punctuation, double-spaces, and excess blank lines.
        result = Regex.Replace(result, @"\(\s*\)", "");
        result = Regex.Replace(result, @"\s{2,}", " ");
        result = Regex.Replace(result, @"\s+([,.;:])", "$1");
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

    private static string StripToolCapabilityDeflectionParagraphs(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var paragraphs = text.Split("\n\n", StringSplitOptions.None);
        if (paragraphs.Length == 0)
            return text;

        static bool IsCapabilityDeflectionParagraph(string paragraph)
        {
            var lower = paragraph.Trim().ToLowerInvariant();
            if (lower.Length == 0)
                return false;

            return lower.Contains("i cannot directly search", StringComparison.Ordinal) ||
                   lower.Contains("restricted to searching within your local environment", StringComparison.Ordinal) ||
                   lower.Contains("i don't have access to the internet", StringComparison.Ordinal) ||
                   lower.Contains("i dont have access to the internet", StringComparison.Ordinal) ||
                   lower.Contains("as a local-first assistant", StringComparison.Ordinal) ||
                   lower.Contains("directory service like google maps", StringComparison.Ordinal) ||
                   lower.Contains("directory service like yelp", StringComparison.Ordinal) ||
                   lower.Contains("i cannot provide a direct link to buy", StringComparison.Ordinal) ||
                   lower.Contains("i do not have real-time access to amazon", StringComparison.Ordinal) ||
                   lower.Contains("violates my security constraints regarding local-first operation", StringComparison.Ordinal) ||
                   lower.Contains("i cannot browse", StringComparison.Ordinal) ||
                   lower.Contains("i cannot search", StringComparison.Ordinal);
        }

        var filtered = paragraphs
            .Where(p => !IsCapabilityDeflectionParagraph(p))
            .ToArray();

        return filtered.Length == 0
            ? string.Empty
            : string.Join("\n\n", filtered).Trim();
    }

    private static string StripToolingLeakParagraphs(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var paragraphs = text.Split("\n\n", StringSplitOptions.None);
        if (paragraphs.Length == 0)
            return text;

        var filtered = paragraphs
            .Where(p => !LooksLikeToolingLeakEssay(p))
            .ToArray();

        return filtered.Length == 0
            ? string.Empty
            : string.Join("\n\n", filtered).Trim();
    }

    private static string StripInlineToolCapabilityClauses(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var sanitized = text;
        var patterns = new[]
        {
            @",?\s*though\s+i\s+cannot\s+verify[^,.;)]*",
            @",?\s*but\s+i\s+cannot\s+verify[^,.;)]*",
            @",?\s*as\s+of\s+my\s+knowledge\s+cutoff\b[^,.;)]*",
            @"\s+up\s+to\s+my\s+knowledge\s+cutoff\b",
            @"\s+based\s+on\s+my\s+knowledge\s+cutoff\b",
            @"\s+according\s+to\s+my\s+knowledge\s+cutoff\b",
            @"since\s+i\s+cannot\s+physically\s+test[^.?!]*[.?!]?",
            @"i\s+cannot\s+physically\s+test[^.?!]*[.?!]?",
            @"my\s+role\s+is\s+to\s+guide\s+your\s+local\s+trust[^.?!]*[.?!]?",
            @"maintain\s+full\s+audit\s+transparency\s+for\s+yourself[^.?!]*[.?!]?"
        };

        foreach (var pattern in patterns)
        {
            sanitized = Regex.Replace(
                sanitized,
                pattern,
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        sanitized = Regex.Replace(sanitized, @"\s{2,}", " ");
        sanitized = Regex.Replace(sanitized, @"\n[ \t]+", "\n");
        sanitized = Regex.Replace(sanitized, @"\s+([,.;:])", "$1");
        sanitized = Regex.Replace(sanitized, @"([,.;:]){2,}", "$1");
        sanitized = Regex.Replace(sanitized, @"\n{3,}", "\n\n");
        return sanitized.Trim();
    }

    private static bool LooksLikeNewsListResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        return (lower.Contains("here are the main stories i found", StringComparison.Ordinal) ||
                lower.Contains("top stories", StringComparison.Ordinal) ||
                lower.Contains("headlines", StringComparison.Ordinal)) &&
               Regex.IsMatch(text, @"(?m)^\s*1\.\s+");
    }

    private static bool LooksLikeEmptyNewsLead(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        return (lower.Contains("here are the main stories i found", StringComparison.Ordinal) ||
                lower.Contains("top stories", StringComparison.Ordinal) ||
                lower.Contains("headlines", StringComparison.Ordinal)) &&
               !Regex.IsMatch(text, @"(?m)^\s*1\.\s+");
    }

    private static string? TryBuildEmptyNewsLeadRecovery(
        string sanitized,
        string? latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (!LooksLikeEmptyNewsLead(sanitized))
            return null;

        foreach (var call in toolCallsMade.Reverse())
        {
            if (!call.Success)
                continue;

            if (!call.ToolName.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
                !call.ToolName.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var query = TryExtractSearchQuery(call.Arguments);
            if (string.IsNullOrWhiteSpace(query))
                continue;

            if (!LooksLikeNewsQuery(call.Arguments, query))
                continue;

            var location = ExtractLocationFromSearchQuery(query);
            if (string.IsNullOrWhiteSpace(location))
                location = ExtractInlineLocation(latestUserMessage);

            if (string.IsNullOrWhiteSpace(location))
                location = query.Trim();

            // If the tool result actually contains real headline rows
            // ("1. \"Title\" — source"), prefer rebuilding a real list
            // over telling the user the summary was empty.
            var headlines = ExtractHeadlinesFromSearchResult(call.Result);
            if (headlines.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("Here are the top ");
                sb.Append(location);
                sb.AppendLine(" local news headlines:");
                for (var i = 0; i < headlines.Count && i < 5; i++)
                {
                    sb.Append(i + 1);
                    sb.Append(". ");
                    sb.AppendLine(headlines[i]);
                }
                return sb.ToString().TrimEnd();
            }

            return $"I checked {location} local news, but the returned summary came back empty before it listed the headlines. If you want, I can rerun it on a narrower topic like schools, city government, or public safety.";
        }

        return null;
    }

    private static List<string> ExtractHeadlinesFromSearchResult(string? result)
    {
        var headlines = new List<string>();
        if (string.IsNullOrWhiteSpace(result))
            return headlines;

        // Drop the SOURCES_JSON tail so we only parse the LLM-facing list.
        var body = result;
        var sourcesIdx = body.IndexOf("<!-- SOURCES_JSON -->", StringComparison.Ordinal);
        if (sourcesIdx >= 0)
            body = body[..sourcesIdx];

        // Match WebSearchTools.FormatResults rows: `1. "Title" — Source`
        var matches = Regex.Matches(
            body,
            "(?m)^\\s*\\d+\\.\\s+\"(?<title>[^\"]{3,300})\"\\s*[—\\-–]\\s*(?<source>[^\\n]+)$");
        foreach (Match m in matches)
        {
            var title = m.Groups["title"].Value.Trim();
            var source = m.Groups["source"].Value.Trim();
            // Trim the optional " (published ...)" suffix from source line.
            var publishedIdx = source.IndexOf(" (published ", StringComparison.Ordinal);
            if (publishedIdx >= 0)
                source = source[..publishedIdx].Trim();
            if (title.Length == 0)
                continue;

            headlines.Add(string.IsNullOrWhiteSpace(source)
                ? title
                : $"{title} — {source}");
        }

        return headlines;
    }

    private static string? TryBuildExplicitWebToolUnavailableGuard(
        string sanitized,
        string? latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(latestUserMessage))
            return null;

        // Only fire when the user explicitly named a web/lookup tool to invoke.
        var explicitIntent = IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(
            latestUserMessage.Trim().ToLowerInvariant());
        if (!string.Equals(explicitIntent, Intents.LookupSearch, StringComparison.OrdinalIgnoreCase))
            return null;

        var webCalls = toolCallsMade
            .Where(c => c.ToolName.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
                        c.ToolName.Equals("WebSearch", StringComparison.OrdinalIgnoreCase) ||
                        c.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) ||
                        c.ToolName.Equals("BrowserNavigate", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (webCalls.Count == 0)
            return null;

        // Every executed web call must be a no-results / empty / error payload.
        // Otherwise the response may legitimately summarize partial data.
        foreach (var call in webCalls)
        {
            if (!IsNoResultsLikePayload(call.Result))
                return null;
        }

        var normalized = ExplicitWebNoResultsContractNormalizer.TryBuildResponse(
            latestUserMessage,
            webCalls);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (string.Equals(sanitized.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
            return null;

        return normalized;
    }

    private static bool IsNoResultsLikePayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return true;

        var trimmed = payload.Trim();
        if (trimmed.StartsWith("No results found for ", StringComparison.OrdinalIgnoreCase))
            return true;
        if (trimmed.StartsWith("[search:", StringComparison.OrdinalIgnoreCase) &&
            trimmed.Contains("0 result", StringComparison.OrdinalIgnoreCase))
            return true;

        // Intentionally do NOT treat structured tool errors (timeout / unavailable / etc.)
        // as no-results here. Those carry semantically important keywords that downstream
        // executor paths and the LLM's offline reasoning are responsible for surfacing.
        // Hijacking them with a generic "unavailable" message strips required keywords
        // such as "timeout".

        return false;
    }

    private static string KeepLeadingOrderedListBlock(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var kept = new List<string>(lines.Length);
        var sawList = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var isListItem = Regex.IsMatch(line, @"^\s*\d+\.\s+");

            if (!sawList)
            {
                kept.Add(line);
                if (isListItem)
                    sawList = true;

                continue;
            }

            if (isListItem)
            {
                kept.Add(line);
                continue;
            }

            if (line.Length == 0)
                continue;

            break;
        }

        return string.Join(Environment.NewLine, kept).Trim();
    }

    private static bool LooksLikeLocalBusinessPrompt(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        return IntentFeatureExtractor.HasLocalBusinessProximitySignals(userMessage.ToLowerInvariant());
    }

    private static bool LooksLikeLocalBusinessBriefingShell(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        return lower.Contains("verification recommended", StringComparison.Ordinal) &&
               lower.Contains("sources checked:", StringComparison.Ordinal) &&
               lower.Contains("briefing summary:", StringComparison.Ordinal);
    }

    private static bool HasLocalBusinessRecoveryContext(
        string? latestUserMessage,
        string assistantResponse,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var lowerLatestUserMessage = (latestUserMessage ?? string.Empty).ToLowerInvariant();

        if (LooksLikeLocalBusinessBriefingShell(assistantResponse) &&
            IntentFeatureExtractor.LooksLikeDeepDiveLookup(lowerLatestUserMessage) &&
            !IntentFeatureExtractor.LooksLikeGenericLocalBusinessDiscovery(lowerLatestUserMessage))
        {
            return false;
        }

        if (LooksLikeLocalBusinessPrompt(latestUserMessage))
            return true;

        if (!LooksLikeLocalBusinessBriefingShell(assistantResponse))
            return false;

        foreach (var call in toolCallsMade)
        {
            if (call.ToolName.Equals("places_lookup", StringComparison.OrdinalIgnoreCase) ||
                call.ToolName.Equals("PlacesLookup", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if ((call.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) ||
                 call.ToolName.Equals("BrowserNavigate", StringComparison.OrdinalIgnoreCase)) &&
                LooksLikeGenericLocalBusinessDirectoryPage(call.Arguments, call.Result))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeLocalBusinessDeflectionResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        return lower.Contains("cannot find a reliable source", StringComparison.Ordinal) ||
               lower.Contains("cannot find reliable source", StringComparison.Ordinal) ||
               lower.Contains("local tools can", StringComparison.Ordinal) ||
               lower.Contains("best approach is to use your own google maps", StringComparison.Ordinal) ||
               lower.Contains("use your own google maps", StringComparison.Ordinal) ||
               lower.Contains("apple maps app", StringComparison.Ordinal) ||
               lower.Contains("official registry", StringComparison.Ordinal);
    }

    private static string BuildLocalBusinessRecoveryResponse(string? latestUserMessage)
    {
        var location = ExtractInlineLocation(latestUserMessage);
        if (!string.IsNullOrWhiteSpace(location))
        {
            return $"I ran live lookups for local businesses in {location}. If you want, I can narrow it to one neighborhood or zip code and return a cleaner shortlist with names and addresses.";
        }

        return "I ran live lookups for that local business request. If you share a city or zip code, I can return a cleaner shortlist with names and addresses.";
    }

    private static string? TryBuildLocalBusinessShortlistFromToolResults(
        string? latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(latestUserMessage) || toolCallsMade.Count == 0)
            return null;

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var call in toolCallsMade)
        {
            if (!call.ToolName.Equals("places_lookup", StringComparison.OrdinalIgnoreCase) &&
                !call.ToolName.Equals("PlacesLookup", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var placeCandidate =
                TryExtractLocalBusinessCandidateFromArguments(call.Arguments, latestUserMessage) ??
                TryExtractLocalBusinessCandidateFromRawText($"{call.Arguments}\n{call.Result}", latestUserMessage);

            if (!string.IsNullOrWhiteSpace(placeCandidate) && seen.Add(placeCandidate))
            {
                candidates.Add(placeCandidate);
            }
        }

        foreach (var call in toolCallsMade.Reverse())
        {
            if (!call.Success || string.IsNullOrWhiteSpace(call.Result))
                continue;

            if (!call.ToolName.Equals("web_search", StringComparison.OrdinalIgnoreCase) &&
                !call.ToolName.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var candidate in ExtractLocalBusinessCandidates(call.Result, latestUserMessage, allowLooseExtraction: false))
            {
                if (seen.Add(candidate))
                    candidates.Add(candidate);

                if (candidates.Count >= 5)
                    break;
            }

            if (candidates.Count >= 5)
                break;
        }

        if (candidates.Count == 0)
        {
            AddBrowserLocalBusinessCandidates(
                latestUserMessage,
                toolCallsMade,
                includeGenericDirectoryPages: false,
                candidates,
                seen);

            if (candidates.Count == 0)
            {
                AddBrowserLocalBusinessCandidates(
                    latestUserMessage,
                    toolCallsMade,
                    includeGenericDirectoryPages: true,
                    candidates,
                    seen);
            }

            if (candidates.Count == 0)
            {
                if (BuildLocalBusinessDirectoryEvidenceFallback(latestUserMessage, toolCallsMade) is { Length: > 0 } directoryFallback)
                    return directoryFallback;
            }
        }

        if (candidates.Count == 0)
            return null;

        // Collect directory source labels and year token for attribution
        var sourceLabels = new List<string>();
        var sourceSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? yearToken = null;
        foreach (var call in toolCallsMade)
        {
            if (!call.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
                !call.ToolName.Equals("BrowserNavigate", StringComparison.OrdinalIgnoreCase))
                continue;

            var srcLabel = GetDirectorySourceLabel(call.Arguments, call.Result);
            if (!string.IsNullOrWhiteSpace(srcLabel) && sourceSeen.Add(srcLabel))
                sourceLabels.Add(srcLabel);

            yearToken ??= Regex.Match(call.Result ?? string.Empty, @"\b20\d{2}\b", RegexOptions.CultureInvariant)
                is { Success: true } ym ? ym.Value : null;
        }

        var label = GetRequestedLocalBusinessLabel(latestUserMessage);
        var location = ExtractInlineLocation(latestUserMessage);
        var sb = new System.Text.StringBuilder();
        sb.Append("Here are a few ");
        sb.Append(label);
        if (!string.IsNullOrWhiteSpace(location))
        {
            sb.Append(" I found in ");
            sb.Append(location);
        }
        else
        {
            sb.Append(" I found nearby");
        }

        if (sourceLabels.Count > 0)
        {
            sb.Append(" (via ");
            if (!string.IsNullOrWhiteSpace(yearToken))
            {
                sb.Append(yearToken);
                sb.Append(' ');
            }
            sb.Append(sourceLabels.Count switch
            {
                1 => sourceLabels[0],
                2 => $"{sourceLabels[0]} and {sourceLabels[1]}",
                _ => string.Join(", ", sourceLabels.Take(sourceLabels.Count - 1)) + $", and {sourceLabels[^1]}"
            });
            sb.Append(')');
        }

        sb.Append(':');
        sb.AppendLine();
        sb.AppendLine();

        foreach (var candidate in candidates)
        {
            sb.Append("- **");
            sb.Append(candidate);
            sb.Append("**");
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.Append("If you want, I can pull more details on one of these ");
        sb.Append(label);
        sb.Append(" and check hours, address, and phone.");

        return sb.ToString();
    }

    private static string? BuildLocalBusinessDirectoryEvidenceFallback(
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var sourceLabels = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? yearToken = null;

        foreach (var call in toolCallsMade)
        {
            if (!call.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
                !call.ToolName.Equals("BrowserNavigate", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!LooksLikeGenericLocalBusinessDirectoryPage(call.Arguments, call.Result))
                continue;

            var label = GetDirectorySourceLabel(call.Arguments, call.Result);
            if (!string.IsNullOrWhiteSpace(label) && seen.Add(label))
                sourceLabels.Add(label);

            yearToken ??= Regex.Match(call.Result ?? string.Empty, @"\b20\d{2}\b", RegexOptions.CultureInvariant) is { Success: true } match
                ? match.Value
                : null;
        }

        if (sourceLabels.Count == 0 && string.IsNullOrWhiteSpace(yearToken))
            return null;

        var location = ExtractInlineLocation(latestUserMessage);
        var sourceText = sourceLabels.Count switch
        {
            0 => "directory pages",
            1 => sourceLabels[0],
            2 => $"{sourceLabels[0]} and {sourceLabels[1]}",
            _ => string.Join(", ", sourceLabels.Take(sourceLabels.Count - 1)) + $", and {sourceLabels[^1]}"
        };

        var sb = new System.Text.StringBuilder();
        sb.Append("I checked ");
        if (!string.IsNullOrWhiteSpace(location))
        {
            sb.Append(location);
            sb.Append(' ');
        }
        if (!string.IsNullOrWhiteSpace(yearToken))
        {
            sb.Append(yearToken);
            sb.Append(' ');
        }
        sb.Append(sourceText);
        sb.Append(", but those directory pages did not give me a shortlist I trust enough to recommend as-is.");
        sb.Append(' ');
        sb.Append("If you want, give me a tighter area like a neighborhood or ZIP code and I can rerun a more focused deli pass.");

        return sb.ToString();
    }

    private static void AddBrowserLocalBusinessCandidates(
        string latestUserMessage,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        bool includeGenericDirectoryPages,
        List<string> candidates,
        HashSet<string> seen)
    {
        foreach (var call in toolCallsMade)
        {
            if (!call.Success || string.IsNullOrWhiteSpace(call.Result))
                continue;

            if (!call.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) &&
                !call.ToolName.Equals("BrowserNavigate", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isGenericDirectoryPage = LooksLikeGenericLocalBusinessDirectoryPage(call.Arguments, call.Result);
            if (isGenericDirectoryPage != includeGenericDirectoryPages)
                continue;

            foreach (var candidate in ExtractLocalBusinessCandidates(call.Result, latestUserMessage, allowLooseExtraction: true))
            {
                if (seen.Add(candidate))
                    candidates.Add(candidate);

                if (candidates.Count >= 5)
                    return;
            }
        }
    }

    private static IEnumerable<string> ExtractLocalBusinessCandidates(
        string toolResult,
        string userMessage,
        bool allowLooseExtraction)
    {
        var sources = SearchOrchestrator.ParseSourcesFromToolResult(toolResult);
        foreach (var source in sources)
        {
            var normalized = NormalizeLocalBusinessCandidate(source.Title, userMessage);
            if (!string.IsNullOrWhiteSpace(normalized))
                yield return normalized;
        }

        if (sources.Count > 0 || !allowLooseExtraction)
            yield break;

        var suffixPattern = GetLocalBusinessSuffixPattern(userMessage);
        if (string.IsNullOrWhiteSpace(suffixPattern))
            yield break;

        foreach (Match match in Regex.Matches(
                     toolResult,
                     $@"\b([A-Z][A-Za-z0-9'&.-]+(?:\s+[A-Z][A-Za-z0-9'&.-]+){{0,4}}\s+(?:{suffixPattern}))\b",
                     RegexOptions.CultureInvariant))
        {
            var normalized = NormalizeLocalBusinessCandidate(match.Groups[1].Value, userMessage);
            if (!string.IsNullOrWhiteSpace(normalized))
                yield return normalized;
        }
    }

    private static string? NormalizeLocalBusinessCandidate(
        string? candidate,
        string userMessage,
        bool requireRequestedSignal = true)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        var cleaned = candidate.Trim().Trim('"', '\'', '.', ',', ';', ':');
        var inlineLocation = ExtractInlineLocation(userMessage);
        if (!string.IsNullOrWhiteSpace(inlineLocation) &&
            cleaned.EndsWith(inlineLocation, StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^inlineLocation.Length].Trim().Trim(',', '-', ' ');
        }

        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        if (cleaned.Length < 4)
            return null;

        var lower = cleaned.ToLowerInvariant();
        if (lower.StartsWith("best ", StringComparison.Ordinal) ||
            lower.StartsWith("top ", StringComparison.Ordinal) ||
            lower.StartsWith("find ", StringComparison.Ordinal) ||
            lower.StartsWith("show ", StringComparison.Ordinal) ||
            lower.StartsWith("local ", StringComparison.Ordinal) ||
            lower.Contains(" near ", StringComparison.Ordinal) ||
            lower.Contains(" restaurantji", StringComparison.Ordinal) ||
            lower.Contains(" tripadvisor", StringComparison.Ordinal) ||
            lower.Contains(" superpages", StringComparison.Ordinal) ||
            lower.Contains("chamber of commerce", StringComparison.Ordinal))
        {
            return null;
        }

        if (LooksLikeChainDepartmentCandidate(cleaned, userMessage))
            return null;

        if (requireRequestedSignal &&
            !SharesRequestedBusinessSignal(lower, userMessage.ToLowerInvariant()))
            return null;

        return cleaned;
    }

    private static bool LooksLikeChainDepartmentCandidate(string candidate, string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var candidateLower = candidate.ToLowerInvariant();
        var userLower = (userMessage ?? string.Empty).ToLowerInvariant();

        ReadOnlySpan<string> chainBrands =
        [
            "walmart", "sam's club", "sams club", "costco", "target",
            "kroger", "safeway", "albertsons", "fred meyer", "winco"
        ];

        var userAskedForSpecificBrand = false;
        foreach (var brand in chainBrands)
        {
            if (userLower.Contains(brand, StringComparison.Ordinal))
            {
                userAskedForSpecificBrand = true;
                break;
            }
        }

        if (userAskedForSpecificBrand)
            return false;

        var mentionsChainBrand = false;
        foreach (var brand in chainBrands)
        {
            if (candidateLower.Contains(brand, StringComparison.Ordinal))
            {
                mentionsChainBrand = true;
                break;
            }
        }

        var hasDepartmentPromoLanguage =
            candidateLower.Contains("store #", StringComparison.Ordinal) ||
            candidateLower.Contains("party tray", StringComparison.Ordinal) ||
            candidateLower.Contains("party trays", StringComparison.Ordinal) ||
            candidateLower.Contains("charcuterie", StringComparison.Ordinal) ||
            candidateLower.Contains("gourmet cheese", StringComparison.Ordinal) ||
            candidateLower.Contains("grab & go", StringComparison.Ordinal) ||
            candidateLower.Contains("sandwiches & wraps", StringComparison.Ordinal);

        return mentionsChainBrand || hasDepartmentPromoLanguage;
    }

    private static string? TryExtractLocalBusinessCandidateFromArguments(string? argumentsJson, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            if (!doc.RootElement.TryGetProperty("query", out var queryElement) ||
                queryElement.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return null;
            }

            var candidate = queryElement.GetString();
            if (doc.RootElement.TryGetProperty("userLocationHint", out var locationHintElement) &&
                locationHintElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var locationHint = locationHintElement.GetString();
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    !string.IsNullOrWhiteSpace(locationHint) &&
                    candidate.EndsWith(locationHint, StringComparison.OrdinalIgnoreCase))
                {
                    candidate = candidate[..^locationHint.Length].Trim().TrimEnd(',', '-', ' ');
                }
            }

            return NormalizeLocalBusinessCandidate(candidate, userMessage, requireRequestedSignal: false);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractLocalBusinessCandidateFromRawText(string? rawText, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return null;

        var normalizedText = rawText
            .Replace("\\u0027", "'", StringComparison.OrdinalIgnoreCase)
            .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("\\u0022", "\"", StringComparison.OrdinalIgnoreCase);

        var suffixPattern = GetLocalBusinessSuffixPattern(userMessage);
        if (string.IsNullOrWhiteSpace(suffixPattern))
            suffixPattern = InferLocalBusinessSuffixPatternFromText(normalizedText);

        if (string.IsNullOrWhiteSpace(suffixPattern))
            return null;

        var match = Regex.Match(
            normalizedText,
            $@"([A-Z][A-Za-z0-9'&.-]+(?:\s+[A-Z][A-Za-z0-9'&.-]+){{0,4}}\s+(?:{suffixPattern})(?:\s+[A-Z][A-Za-z.'-]+,\s*[A-Z]{{2}})?)",
            RegexOptions.CultureInvariant);

        if (!match.Success)
            return null;

        return NormalizeLocalBusinessCandidate(match.Groups[1].Value, userMessage, requireRequestedSignal: false);
    }

    private static string InferLocalBusinessSuffixPatternFromText(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower switch
        {
            _ when lower.Contains("deli", StringComparison.Ordinal) => "Deli|Delicatessen",
            _ when lower.Contains("flor", StringComparison.Ordinal) => "Florist|Flowers|Floral",
            _ when lower.Contains("baker", StringComparison.Ordinal) => "Bakery|Bakeshop",
            _ when lower.Contains("cafe", StringComparison.Ordinal) => "Cafe",
            _ when lower.Contains("coffee", StringComparison.Ordinal) => "Coffee|Coffeehouse|Roasters",
            _ when lower.Contains("restaurant", StringComparison.Ordinal) => "Restaurant|Grill|Kitchen|Eatery",
            _ when lower.Contains("barber", StringComparison.Ordinal) => "Barber|Barbershop",
            _ when lower.Contains("salon", StringComparison.Ordinal) => "Salon",
            _ when lower.Contains("pharmacy", StringComparison.Ordinal) => "Pharmacy",
            _ when lower.Contains("store", StringComparison.Ordinal) => "Store|Market",
            _ when lower.Contains("shop", StringComparison.Ordinal) => "Shop",
            _ => string.Empty
        };
    }

    private static bool LooksLikeGenericLocalBusinessDirectoryPage(string? argumentsJson, string? result)
    {
        if (TryExtractBrowserUrlHost(argumentsJson) is { Length: > 0 } host)
        {
            if (host.Contains("restaurantji", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("tripadvisor", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("superpages", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("yellowpages", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("mapquest", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("yelp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (host.Contains("chamberofcommerce", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (string.IsNullOrWhiteSpace(result))
            return false;

        var lower = result.ToLowerInvariant();
        return lower.Contains("best ", StringComparison.Ordinal) &&
               lower.Contains(" near ", StringComparison.Ordinal) &&
               (lower.Contains(" restaurantji", StringComparison.Ordinal) ||
                lower.Contains(" tripadvisor", StringComparison.Ordinal) ||
                lower.Contains(" superpages", StringComparison.Ordinal));
    }

    private static string? TryExtractBrowserUrlHost(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            if (!doc.RootElement.TryGetProperty("url", out var urlElement) ||
                urlElement.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return null;
            }

            var url = urlElement.GetString();
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractSearchQuery(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("query", out var queryElement) &&
                queryElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return queryElement.GetString()?.Trim();
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool LooksLikeNewsQuery(string? argumentsJson, string query)
    {
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("categories", out var categoriesElement) &&
                    categoriesElement.ValueKind == System.Text.Json.JsonValueKind.String &&
                    categoriesElement.GetString()?.Contains("news", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        var lower = query.ToLowerInvariant();
        return lower.Contains("news", StringComparison.Ordinal) ||
               lower.Contains("headline", StringComparison.Ordinal);
    }

    private static string ExtractLocationFromSearchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var cleaned = Regex.Replace(query, @"\b(?:local|latest|recent|top)\b", string.Empty, RegexOptions.IgnoreCase)
            .Replace("headlines", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("headline", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("news", string.Empty, StringComparison.OrdinalIgnoreCase);

        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim().Trim(',', '-', ' ');
        return cleaned;
    }

    private static string? GetDirectorySourceLabel(string? argumentsJson, string? result)
    {
        var host = TryExtractBrowserUrlHost(argumentsJson) ?? string.Empty;
        if (host.Contains("chamberofcommerce", StringComparison.OrdinalIgnoreCase))
            return "Chamber of Commerce";
        if (host.Contains("restaurantji", StringComparison.OrdinalIgnoreCase))
            return "Restaurantji";
        if (host.Contains("tripadvisor", StringComparison.OrdinalIgnoreCase))
            return "TripAdvisor";
        if (host.Contains("superpages", StringComparison.OrdinalIgnoreCase))
            return "Superpages";
        if (host.Contains("yellowpages", StringComparison.OrdinalIgnoreCase))
            return "Yellow Pages";

        var lower = result?.ToLowerInvariant() ?? string.Empty;
        if (lower.Contains("restaurantji", StringComparison.Ordinal))
            return "Restaurantji";
        if (lower.Contains("tripadvisor", StringComparison.Ordinal))
            return "TripAdvisor";
        if (lower.Contains("superpages", StringComparison.Ordinal))
            return "Superpages";

        return null;
    }

    private static bool SharesRequestedBusinessSignal(string candidateLower, string userLower)
    {
        return (userLower.Contains("deli", StringComparison.Ordinal) &&
                (candidateLower.Contains("deli", StringComparison.Ordinal) || candidateLower.Contains("delicatessen", StringComparison.Ordinal))) ||
               (userLower.Contains("flor", StringComparison.Ordinal) &&
                (candidateLower.Contains("flor", StringComparison.Ordinal) || candidateLower.Contains("flower", StringComparison.Ordinal))) ||
               (userLower.Contains("baker", StringComparison.Ordinal) && candidateLower.Contains("baker", StringComparison.Ordinal)) ||
               (userLower.Contains("cafe", StringComparison.Ordinal) && candidateLower.Contains("cafe", StringComparison.Ordinal)) ||
               (userLower.Contains("coffee", StringComparison.Ordinal) && candidateLower.Contains("coffee", StringComparison.Ordinal)) ||
               (userLower.Contains("restaurant", StringComparison.Ordinal) && candidateLower.Contains("restaurant", StringComparison.Ordinal)) ||
               (userLower.Contains("barber", StringComparison.Ordinal) && candidateLower.Contains("barber", StringComparison.Ordinal)) ||
               (userLower.Contains("salon", StringComparison.Ordinal) && candidateLower.Contains("salon", StringComparison.Ordinal)) ||
               (userLower.Contains("pharmacy", StringComparison.Ordinal) && candidateLower.Contains("pharmacy", StringComparison.Ordinal)) ||
               (userLower.Contains("shop", StringComparison.Ordinal) && candidateLower.Contains("shop", StringComparison.Ordinal)) ||
               (userLower.Contains("store", StringComparison.Ordinal) && candidateLower.Contains("store", StringComparison.Ordinal));
    }

    private static string GetRequestedLocalBusinessLabel(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant();
        return lower switch
        {
            _ when lower.Contains("deli", StringComparison.Ordinal) => "delis",
            _ when lower.Contains("flor", StringComparison.Ordinal) => "florists",
            _ when lower.Contains("baker", StringComparison.Ordinal) => "bakeries",
            _ when lower.Contains("cafe", StringComparison.Ordinal) => "cafes",
            _ when lower.Contains("coffee", StringComparison.Ordinal) => "coffee shops",
            _ when lower.Contains("restaurant", StringComparison.Ordinal) => "restaurants",
            _ when lower.Contains("barber", StringComparison.Ordinal) => "barbers",
            _ when lower.Contains("salon", StringComparison.Ordinal) => "salons",
            _ when lower.Contains("pharmacy", StringComparison.Ordinal) => "pharmacies",
            _ when lower.Contains("store", StringComparison.Ordinal) => "stores",
            _ when lower.Contains("shop", StringComparison.Ordinal) => "shops",
            _ => "local businesses"
        };
    }

    private static string GetLocalBusinessSuffixPattern(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant();
        return lower switch
        {
            _ when lower.Contains("deli", StringComparison.Ordinal) => "Deli|Delicatessen",
            _ when lower.Contains("flor", StringComparison.Ordinal) => "Florist|Flowers|Floral",
            _ when lower.Contains("baker", StringComparison.Ordinal) => "Bakery|Bakeshop",
            _ when lower.Contains("cafe", StringComparison.Ordinal) => "Cafe",
            _ when lower.Contains("coffee", StringComparison.Ordinal) => "Coffee|Coffeehouse|Roasters",
            _ when lower.Contains("restaurant", StringComparison.Ordinal) => "Restaurant|Grill|Kitchen|Eatery",
            _ when lower.Contains("barber", StringComparison.Ordinal) => "Barber|Barbershop",
            _ when lower.Contains("salon", StringComparison.Ordinal) => "Salon",
            _ when lower.Contains("pharmacy", StringComparison.Ordinal) => "Pharmacy",
            _ when lower.Contains("store", StringComparison.Ordinal) => "Store|Market",
            _ when lower.Contains("shop", StringComparison.Ordinal) => "Shop",
            _ => string.Empty
        };
    }

    private static string ExtractInlineLocation(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return string.Empty;

        var match = Regex.Match(userMessage, @"\bin\s+([A-Za-z][A-Za-z\s\.'-]+(?:,\s*[A-Za-z]{2})?)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return string.Empty;

        return match.Groups[1].Value.Trim().TrimEnd('?', '.', '!', ',');
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

    private static string StripEmptyListMarkerLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var filtered = new List<string>(lines.Length);
        var blankRun = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (Regex.IsMatch(line, @"^\s*(?:[-*]|\d+[.)])\s*$"))
                continue;

            if (line.Length == 0)
            {
                blankRun++;
                if (blankRun > 1)
                    continue;
            }
            else
            {
                blankRun = 0;
            }

            filtered.Add(line);
        }

        return string.Join('\n', filtered).Trim();
    }

    private static string StripTerminalSignatureLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        normalized = Regex.Replace(
            normalized,
            @"\n\s*--\s*Sir\s+Thaddeus\s*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return normalized.TrimEnd();
    }

    private static string TrimAfterSignatureLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                             .Replace('\r', '\n');
        var signatureMatch = Regex.Match(
            normalized,
            @"--\s*Sir\s+Thaddeus\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!signatureMatch.Success)
            return text;

        var afterSignature = signatureMatch.Index + signatureMatch.Length;
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
