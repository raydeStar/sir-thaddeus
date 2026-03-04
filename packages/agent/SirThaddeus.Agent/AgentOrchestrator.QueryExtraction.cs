using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static SirThaddeus.Agent.OrchestratorMessageHelpers;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.PostProcessing;
using SirThaddeus.Agent.ConversationSegmentation;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.ToolLoop;
using SirThaddeus.Agent.Tools;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine.Formatting;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
    private static string NormalizeQueryText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var input = text.Trim();
        var sb = new StringBuilder(input.Length);
        var lastWasSpace = false;

        foreach (var c in input)
        {
            // Keep letters/digits. Convert most punctuation to spaces so
            // tokens like "thadds!" become "thadds" for filtering.
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasSpace = false;
                continue;
            }

            // Keep a few token-internal characters.
            if (c is '\'' or '-' or '+')
            {
                sb.Append(c);
                lastWasSpace = false;
                continue;
            }

            if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static bool IsBannedSearchToken(string tokenLower)
    {
        if (string.IsNullOrWhiteSpace(tokenLower))
            return true;

        // Assistant name variants (common greetings / nicknames).
        if (tokenLower == "thaddeus" || tokenLower.StartsWith("thadd"))
            return true;

        // Greetings, casual filler, discourse markers, pronouns, and
        // request-framing verbs. Anything that isn't a real search topic.
        return tokenLower is
            // ── Greetings / salutations ───────────────────────────
            "sir" or "hey" or "hi" or "hello" or "yo" or "sup" or
            "homie" or "buddy" or "pal" or
            "good" or "morning" or "afternoon" or "evening" or
            // ── Discourse markers / interjections ─────────────────
            "well" or "ok" or "okay" or "alright" or "so" or
            "anyway" or "actually" or "basically" or "like" or
            "heck" or "hell" or "gosh" or "gee" or
            // ── Speech fillers ────────────────────────────────────
            "um" or "uh" or "hmm" or "huh" or "er" or "ah" or
            // ── Pronouns / contractions ───────────────────────────
            "i" or "im" or "i'm" or "we" or "our" or "us" or
            "you" or "me" or "my" or "he" or "she" or "it" or
            "its" or "it's" or "they" or "them" or "their" or
            // ── Modals / auxiliaries ──────────────────────────────
            "can" or "could" or "would" or "will" or "shall" or
            "should" or "might" or "may" or "do" or "does" or
            "did" or "is" or "are" or "was" or "were" or "been" or
            "being" or "have" or "has" or "had" or
            // ── Request framing verbs ─────────────────────────────
            "want" or "wanted" or "need" or "needed" or "check" or
            "look" or "up" or "search" or "find" or "pull" or
            "show" or "get" or "bring" or "grab" or "fetch" or
            "tell" or "give" or
            // ── Polite filler ─────────────────────────────────────
            "please" or "plz" or "thanks" or "thank" or
            "danke" or "dank" or
            // ── Prepositions / articles / connectors ──────────────
            "for" or "to" or "on" or "about" or "into" or "in" or
            "at" or "of" or "with" or "from" or "by" or "or" or
            "and" or "but" or "if" or "then" or "than" or
            "the" or "a" or "an" or "this" or "that" or
            "there" or "here" or "some" or "any" or
            // ── Other low-signal words ────────────────────────────
            "just" or "really" or "very" or "also" or "too" or
            "what" or "how" or "when" or "where" or "know" or
            "think" or "see" or "go" or "going" or "went";
    }

    private static bool LooksLikeLogicPuzzlePrompt(string lower)
        => IntentFeatureExtractor.LooksLikeLogicPuzzlePrompt(lower);

    private static bool LooksLikeIdentityLookup(string lower)
        => IntentFeatureExtractor.LooksLikeIdentityLookup(lower);

    private static string IdentityPrefix(string lower)
    {
        // Default to "who is" unless the user clearly asked "what is".
        if (string.IsNullOrWhiteSpace(lower))
            return "who is";

        return (lower.Contains("what is ") || lower.Contains("what's ") || lower.Contains("whats ") ||
                lower.Contains("define ") || lower.Contains("meaning of ") || lower.Contains("what does "))
            ? "what is"
            : "who is";
    }

    private static bool TryExtractIdentitySubject(string userMessage, out string subject)
    {
        subject = "";
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var trimmed = userMessage.Trim();
        var lower = trimmed.ToLowerInvariant();

        string[] markers =
        [
            "who the heck is ",
            "who the hell is ",
            "who is ",
            "who's ",
            "whos ",
            "who was ",
            "what is ",
            "what's ",
            "whats ",
            "define ",
            "meaning of ",
            "what does "
        ];

        foreach (var marker in markers)
        {
            var idx = lower.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
                continue;

            var start = idx + marker.Length;
            if (start >= trimmed.Length)
                continue;

            subject = trimmed[start..].Trim(
                ' ', '?', '!', '.', ',', ':', ';', '"', '\'', '(', ')', '[', ']', '{', '}');

            if (subject.Length > 0)
                return true;
        }

        return false;
    }

    private static string CleanSearchQuery(string query)
    {
        var normalized = NormalizeQueryText(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(tokens.Length);

        foreach (var token in tokens)
        {
            var lower = token.ToLowerInvariant();
            if (IsBannedSearchToken(lower))
                continue;

            kept.Add(token);
            if (kept.Count >= 6) // enforce the 2–6 keyword guideline
                break;
        }

        return string.Join(' ', kept).Trim();
    }

    private static bool WantsUsRegion(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        // Prefer explicit punctuation/casing so we don't confuse pronoun "us"
        // (e.g. "look this up for us") with the region "US".
        if (userMessage.Contains("U.S.", StringComparison.Ordinal) ||
            userMessage.Contains("U.S", StringComparison.Ordinal))
            return true;

        if (userMessage.Contains(" US ", StringComparison.Ordinal) ||
            userMessage.EndsWith(" US", StringComparison.Ordinal) ||
            userMessage.StartsWith("US ", StringComparison.Ordinal))
            return true;

        var lower = userMessage.ToLowerInvariant();
        return lower.Contains("united states") ||
               lower.Contains("usa") ||
               lower.Contains("u.s") ||
               lower.Contains("u s");
    }

    private static bool IsGenericHeadlineQuery(string queryLower)
    {
        var q = (queryLower ?? "").Trim();
        return q is
            "headline" or "headlines" or
            "news" or "latest news" or "breaking news" or
            "latest headlines" or "breaking headlines" or
            "top headlines";
    }

    // ─────────────────────────────────────────────────────────────────
    // Vague follow-up query detection + topic resolution
    //
    // Small local models frequently fail to resolve conversational
    // references ("that", "it", "more") during search query extraction
    // and echo back the user's vague wording instead. These helpers
    // catch that case and pull the real topic from the last assistant
    // response — entirely deterministic, no extra LLM call.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the extracted query looks like an unresolved
    /// follow-up reference — words that only make sense in context
    /// but carry zero topical signal for a search engine.
    /// </summary>
    private static bool IsVagueFollowUpQuery(string query)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();

        // Very short queries that are clearly contextual
        if (q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3)
        {
            // Direct matches: the model literally echoed the filler
            string[] vaguePatterns =
            [
                "more info",
                "more information",
                "more on that",
                "more about that",
                "more about it",
                "more on it",
                "more details",
                "tell me more",
                "go deeper",
                "elaborate",
                "that topic",
                "the topic",
                "that story",
                "the story",
                "that article",
                "that",
                "it",
                "this"
            ];

            foreach (var pattern in vaguePatterns)
            {
                if (q == pattern || q.Contains(pattern))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts to extract a concrete topic from the most recent
    /// assistant message in history. Uses the first sentence (the
    /// "bottom line") which typically contains the core subject.
    /// Returns false if no usable topic can be extracted.
    /// </summary>
    private bool TryExtractTopicFromLastAssistant(out string topic)
    {
        topic = "";

        // Walk history backwards to find the last assistant message
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Role != "assistant")
                continue;

            var content = (_history[i].Content ?? "").Trim();
            if (string.IsNullOrWhiteSpace(content))
                continue;

            // Strip common lead-ins: "Bottom line:", "Here's what I found:", etc.
            var cleaned = content;
            string[] leadIns =
            [
                "bottom line:",
                "here's what i found:",
                "here is what i found:",
                "summary:",
                "in short:",
                "tl;dr:"
            ];
            var lower = cleaned.ToLowerInvariant();
            foreach (var lead in leadIns)
            {
                if (lower.StartsWith(lead))
                {
                    cleaned = cleaned[lead.Length..].TrimStart();
                    break;
                }
            }

            // Take the first sentence — that's the core topic.
            // Split on sentence terminators but not on abbreviations.
            var firstSentence = cleaned;
            var sentenceEnd = cleaned.IndexOfAny(['.', '!', '?', '\n']);
            if (sentenceEnd > 10) // require at least a meaningful chunk
                firstSentence = cleaned[..sentenceEnd].Trim();

            // Compress to a search-friendly query: take up to
            // the first ~80 chars and strip excessive whitespace.
            if (firstSentence.Length > 80)
                firstSentence = firstSentence[..80].Trim();

            // Ensure we have something meaningful (>= 5 chars)
            if (firstSentence.Length >= 5)
            {
                topic = firstSentence;
                return true;
            }

            break; // only check the most recent assistant message
        }

        return false;
    }

    /// <summary>
    /// Extracts a search query and recency by asking the LLM to produce
    /// a <c>web_search</c> tool call. The LLM receives the full
    /// conversation history so it can determine the actual topic from
    /// context rather than relying on brittle keyword filtering.
    ///
    /// Post-processing is minimal: strip assistant name references,
    /// apply identity/headline defaults, and validate bounds. Everything
    /// else is the model's job — it has the context to get it right.
    ///
    /// Falls back to deterministic cleanup only if the LLM fails to
    /// produce a tool call at all (e.g., model error, text-only response).
    /// </summary>
    private async Task<(string Query, string Recency)> ExtractSearchViaToolCallAsync(
        string userMessage, string memoryPackText, CancellationToken cancellationToken)
    {
        const string defaultRecency = "any";
        var lowerMsg = (userMessage ?? "").ToLowerInvariant();
        var useConversationContext =
            SearchModeRouter.IsFollowUpMessage(lowerMsg) ||
            SearchModeRouter.IsReferential(lowerMsg) ||
            LooksLikeFollowUpDepthRequest(userMessage ?? "");
        var wantsUs = WantsUsRegion(userMessage ?? "");
        var isIdentity = LooksLikeIdentityLookup(lowerMsg);
        var identityPrefix = IdentityPrefix(lowerMsg);

        try
        {
            var now = _timeProvider.GetUtcNow();

            // ── Build search extractor prompt ─────────────────────────
            // Tiny local models can over-anchor on prior turns when we
            // always send full history. Only do that for true follow-ups.
            var systemContent =
                "You are a search query extractor.\n" +
                (useConversationContext
                    ? "Read the FULL conversation history and determine what the user wants to search for.\n"
                    : "Treat this as a NEW standalone question. Use ONLY the latest user message as the topic source and ignore prior turns.\n") +
                "Call the web_search tool with the appropriate query and recency.\n\n" +
                $"Today's date is {now:yyyy-MM-dd} (UTC). The current year is {now.Year}.\n\n" +
                "Rules:\n" +
                "- Extract the TOPIC the user wants to look up — 2 to 6 keywords.\n" +
                "- CRITICAL: If the user uses pronouns or vague references like " +
                "'that', 'it', 'this', 'more info', 'tell me more', 'more about', " +
                "'go deeper', 'elaborate', you MUST resolve the reference.\n" +
                "  Look at the PREVIOUS assistant message to find the actual topic.\n" +
                "  Example: if the last answer was about 'UK Prime Minister staff " +
                "resignations' and the user says 'more on that', " +
                "the query is 'UK Prime Minister staff resignations', NOT 'more info'.\n" +
                "- NEVER use the user's vague wording as the query. " +
                "Always resolve to the concrete topic from the conversation.\n" +
                "- Ignore greetings, filler, discourse markers (well, ok, so...), " +
                "and the assistant's name. These are NEVER search terms.\n" +
                "- If the user asks for 'news', 'headlines', or 'latest', " +
                "set recency to 'day'.\n" +
                "- For generic news requests with no specific topic, " +
                "use query: \"top headlines\".\n" +
                "- If the user asks an evergreen fact question (e.g., \"who won X\"), " +
                "do NOT guess a year. Prefer queries like \"most recent X winner\".\n" +
                "- ALWAYS call the tool. Never reply with text.";

            if (!string.IsNullOrWhiteSpace(memoryPackText))
                systemContent += "\n\n" + memoryPackText;

            var messages = new List<ChatMessage> { ChatMessage.System(systemContent) };
            if (useConversationContext)
            {
                foreach (var msg in _history)
                {
                    if (msg.Role is "system") continue; // already have ours
                    messages.Add(msg);
                }

                // If the latest user message isn't already in history
                // (it can vary depending on caller timing), add it.
                if (_history.Count == 0 ||
                    _history[^1].Role != "user" ||
                    _history[^1].Content != userMessage)
                {
                    messages.Add(ChatMessage.User(userMessage ?? ""));
                }
            }
            else
            {
                messages.Add(ChatMessage.User(userMessage ?? ""));
            }

            LogEvent(
                "AGENT_QUERY_SCOPE",
                useConversationContext ? "context=full_history" : "context=latest_message_only");

            var response = await _llm.ChatAsync(
                messages, SearchExtractionTools, maxTokensOverride: 80, cancellationToken);

            // ── Parse the tool call response ──────────────────────────
            if (response.ToolCalls is { Count: > 0 })
            {
                var args = response.ToolCalls[0].Function.Arguments;
                using var doc = JsonDocument.Parse(args);
                var root = doc.RootElement;

                var query = root.TryGetProperty("query", out var q)
                    ? (q.GetString() ?? "").Trim()
                    : "";
                var recency = root.TryGetProperty("recency", out var r)
                    ? NormalizeRecency(r.GetString() ?? "")
                    : defaultRecency;

                // ── Minimal safety net: strip assistant name only ─────
                // The LLM has full context and should produce a clean
                // query. We only strip references to the assistant's
                // name (the one thing the model might echo back).
                var cleanedQuery = StripAssistantName(query);

                // Prefer explicit recency hints from the user message
                // (deterministic override — the LLM sometimes misses these).
                var recencyFromUser = DetectRecencyFallback(userMessage ?? "");
                if (recencyFromUser != "any" && recencyFromUser != recency)
                    recency = recencyFromUser;

                // Generic "headlines"/"news" → stable default.
                if (IsGenericHeadlineQuery(cleanedQuery.ToLowerInvariant()))
                    cleanedQuery = wantsUs ? "U.S. top headlines" : "top headlines";

                // Generic headlines with no recency specified should default to day.
                if (IsGenericHeadlineQuery(cleanedQuery.ToLowerInvariant()) && recency == "any")
                    recency = "day";

                // Follow-up: if we already have sources from the prior search,
                // prefer a concrete title over a generic query like "more X news".
                if (LooksLikeFollowUpDepthRequest(userMessage ?? "") &&
                    _searchOrchestrator.Session.LastResults.Count > 0)
                {
                    var candidates = PickRelevantSources(userMessage ?? "", _searchOrchestrator.Session.LastResults.Select(r => (r.Url, r.Title)).ToList(), maxUrls: 1);
                    if (candidates.Count > 0 && !string.IsNullOrWhiteSpace(candidates[0].Title))
                    {
                        var titleQuery = candidates[0].Title.Trim();
                        if (titleQuery.Length > 120) titleQuery = titleQuery[..120].Trim();

                        LogEvent("AGENT_QUERY_RESOLVE",
                            $"Follow-up query resolved from prior sources: \"{cleanedQuery}\" → \"{titleQuery}\"");
                        cleanedQuery = titleQuery;
                    }

                    if (recency == "any" && (_searchOrchestrator.Session.LastRecency ?? "any") != "any")
                        recency = _searchOrchestrator.Session.LastRecency!;
                }

                // Identity queries: prepend "who is"/"what is" if needed.
                if (isIdentity && !string.IsNullOrWhiteSpace(cleanedQuery))
                {
                    var ql = cleanedQuery.ToLowerInvariant();
                    if (!ql.StartsWith("who is") && !ql.StartsWith("what is") &&
                        !ql.StartsWith("who's")  && !ql.StartsWith("whos") &&
                        !ql.StartsWith("what's") && !ql.StartsWith("whats"))
                    {
                        cleanedQuery = $"{identityPrefix} {cleanedQuery}".Trim();
                    }

                    recency = "any";
                }

                (cleanedQuery, recency) = ApplyTemporalSanityChecks(
                    userMessage ?? "", cleanedQuery, recency);

                // ── Vague follow-up detection ────────────────────────
                // Small models often echo the user's vague wording
                // ("more info", "that topic") instead of resolving
                // the reference from history. When the extracted query
                // looks like a follow-up placeholder and we have prior
                // context, replace it with the actual topic.
                if (IsVagueFollowUpQuery(cleanedQuery) &&
                    TryExtractTopicFromLastAssistant(out var resolvedTopic))
                {
                    LogEvent("AGENT_QUERY_RESOLVE",
                        $"Vague query \"{cleanedQuery}\" → " +
                        $"resolved to \"{resolvedTopic}\" from prior context");
                    cleanedQuery = resolvedTopic;
                }

                // Accept if non-empty and within bounds.
                if (!string.IsNullOrWhiteSpace(cleanedQuery) &&
                    cleanedQuery.Length >= 2 && cleanedQuery.Length <= 120)
                {
                    LogEvent("AGENT_QUERY_EXTRACT",
                        $"Tool call: query=\"{cleanedQuery}\", recency={recency}");
                    return (cleanedQuery, recency);
                }

                LogEvent("AGENT_QUERY_EXTRACT",
                    $"Tool call returned empty/invalid query \"{query}\" " +
                    "— falling through to deterministic cleanup");
            }
            else
            {
                LogEvent("AGENT_QUERY_EXTRACT",
                    "LLM did not produce a tool call — using deterministic fallback");
            }
        }
        catch (Exception ex)
        {
            LogEvent("AGENT_QUERY_EXTRACT_FAIL",
                $"Tool-call extraction failed: {ex.Message}");
        }

        // ── Deterministic fallback ────────────────────────────────────
        // Only reached when the LLM fails to produce a usable tool call
        // (model error, text-only response, empty output). This is the
        // safety net, not the primary path.
        var fallbackQuery = CleanSearchQuery(StripConversationalFiller(userMessage ?? ""));
        var fallbackRecency = DetectRecencyFallback(userMessage ?? "");

        // Apply the same vague follow-up resolution here too.
        if (IsVagueFollowUpQuery(fallbackQuery) &&
            TryExtractTopicFromLastAssistant(out var fallbackResolvedTopic))
        {
            LogEvent("AGENT_QUERY_RESOLVE",
                $"Deterministic fallback: vague \"{fallbackQuery}\" → " +
                $"\"{fallbackResolvedTopic}\" from prior context");
            fallbackQuery = fallbackResolvedTopic;
        }

        if (isIdentity)
        {
            if (TryExtractIdentitySubject(userMessage ?? "", out var subject))
            {
                var cleanSubject = CleanSearchQuery(subject);
                if (!string.IsNullOrWhiteSpace(cleanSubject))
                    fallbackQuery = $"{identityPrefix} {cleanSubject}".Trim();
            }
            else if (!string.IsNullOrWhiteSpace(fallbackQuery))
            {
                fallbackQuery = $"{identityPrefix} {fallbackQuery}".Trim();
            }

            fallbackRecency = "any";
        }

        if (string.IsNullOrWhiteSpace(fallbackQuery))
        {
            if (lowerMsg.Contains("headline") || lowerMsg.Contains("headlines") ||
                lowerMsg.Contains("news") || lowerMsg.Contains("latest") || lowerMsg.Contains("breaking"))
            {
                fallbackQuery = wantsUs ? "U.S. top headlines" : "top headlines";
            }
        }

        (fallbackQuery, fallbackRecency) = ApplyTemporalSanityChecks(
            userMessage ?? "", fallbackQuery, fallbackRecency);

        if (IsGenericHeadlineQuery(fallbackQuery.ToLowerInvariant()))
            fallbackQuery = wantsUs ? "U.S. top headlines" : "top headlines";

        return (fallbackQuery, fallbackRecency);
    }

    /// <summary>
    /// Applies narrow, deterministic sanity checks to reduce obvious
    /// time-related mistakes from local models (e.g., injecting an
    /// arbitrary year the user never asked for).
    /// </summary>
    private (string Query, string Recency) ApplyTemporalSanityChecks(
        string userMessage, string query, string recency)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (query, recency);

        var lowerMsg = (userMessage ?? "").ToLowerInvariant();
        var lowerQuery = query.ToLowerInvariant();

        // If the user specified a year (2024) or a relative-year hint ("last year"),
        // do not override — they know what they asked for.
        if (ContainsExplicitYear(lowerMsg) || ContainsRelativeYearHint(lowerMsg))
            return (query, recency);

        // Super Bowl winner questions are common and local models tend to
        // hallucinate the last year they "remember". Force a stable query.
        if (LooksLikeSuperBowlWinnerQuestion(lowerMsg, lowerQuery))
        {
            if (TryExtractYear(query, out var year))
            {
                var nowYear = _timeProvider.GetUtcNow().Year;
                if (year != nowYear && year != nowYear - 1)
                    LogEvent("AGENT_TEMPORAL_FIXUP",
                        $"Replacing guessed year {year} in query \"{query}\"");
            }

            return ("most recent Super Bowl winner", "any");
        }

        return (query, recency);
    }

    private static bool LooksLikeSuperBowlWinnerQuestion(string lowerMsg, string lowerQuery)
    {
        var mentionsSuperBowl = lowerMsg.Contains("super bowl") || lowerMsg.Contains("superbowl") ||
                                lowerQuery.Contains("super bowl") || lowerQuery.Contains("superbowl");

        if (!mentionsSuperBowl) return false;

        var winnerIntent =
            lowerMsg.Contains("who won") ||
            lowerMsg.Contains("winner") ||
            lowerMsg.Contains("won the super bowl") ||
            lowerMsg.Contains("won the superbowl") ||
            lowerQuery.Contains("winner");

        return winnerIntent;
    }

    private static bool ContainsRelativeYearHint(string lower)
        => lower.Contains("last year") ||
           lower.Contains("this year") ||
           lower.Contains("years ago") ||
           lower.Contains("year ago") ||
           lower.Contains("previous year") ||
           lower.Contains("prior year");

    private static bool ContainsExplicitYear(string text)
        => TryExtractYear(text, out _);

    private static bool TryExtractYear(string text, out int year)
    {
        year = 0;
        if (string.IsNullOrEmpty(text) || text.Length < 4)
            return false;

        for (var i = 0; i <= text.Length - 4; i++)
        {
            if (!char.IsDigit(text[i]) ||
                !char.IsDigit(text[i + 1]) ||
                !char.IsDigit(text[i + 2]) ||
                !char.IsDigit(text[i + 3]))
                continue;

            if (!int.TryParse(text.AsSpan(i, 4), out var candidate))
                continue;

            if (candidate is >= 1900 and <= 2100)
            {
                year = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Strips only assistant name references from a query string.
    /// This is the only deterministic post-processing applied to the
    /// LLM's tool call output — everything else is the model's job.
    /// </summary>
    private static string StripAssistantName(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "";

        var normalized = NormalizeQueryText(query);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(tokens.Length);

        foreach (var token in tokens)
        {
            var lower = token.ToLowerInvariant();

            // Only filter assistant name variants — nothing else.
            if (lower == "thaddeus" || lower.StartsWith("thadd") || lower == "sir")
                continue;

            kept.Add(token);
        }

        return string.Join(' ', kept).Trim();
    }

    /// <summary>
    /// Quick keyword-based recency detection used when the LLM call
    /// is skipped or fails. Keeps things working even without the model.
    /// </summary>
    private static string DetectRecencyFallback(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("today") || lower.Contains("this morning") ||
            lower.Contains("right now") || lower.Contains("just happened"))
            return "day";
        if (lower.Contains("this week") || lower.Contains("past week") ||
            lower.Contains("last week") || lower.Contains("last few days"))
            return "week";
        if (lower.Contains("this month") || lower.Contains("past month") ||
            lower.Contains("recently"))
            return "month";
        if (lower.Contains("breaking") || lower.Contains("headline") || lower.Contains("headlines") ||
            lower.Contains("top stories") ||
            (lower.Contains("latest") &&
             (lower.Contains("news") || lower.Contains("headline") || lower.Contains("headlines") ||
              lower.Contains("update") || lower.Contains("updates") || lower.Contains("happening"))))
            return "day";
        return "any";
    }

    /// <summary>
    /// Strips conversational filler from a user message to produce a
    /// cleaner search query when the LLM extraction fails. Removes
    /// leading phrases like "can you check", "I want to look up", etc.
    /// and trailing noise like "for me", "please".
    ///
    /// Example: "Can you check up news on the US stock market this week?
    /// its been a crazy week, what happened?"
    ///   → "news on the US stock market this week"
    /// </summary>
    private static string StripConversationalFiller(string input)
    {
        var text = input.Trim(' ', '?', '!', '.', ',');
        var lower = text.ToLowerInvariant();

        // ── Strip leading greetings / salutations ─────────────────────
        // Users often open with "hey sir thaddeus!" or "hello!" before
        // stating their actual request. Peel those off first.
        string[] greetingPrefixes =
        [
            "hey sir thaddeus",   "hi sir thaddeus",
            "hello sir thaddeus", "yo sir thaddeus",
            "hey thaddeus",       "hi thaddeus",
            "hello thaddeus",     "yo thaddeus",
            "good morning",       "good afternoon",
            "good evening",       "hey there",
            "hi there",           "hello there",
            "hey",                "hi",
            "hello",              "yo",
            // ── Discourse markers / hedges ────────────────────────
            // Users often open with these before their real request.
            // "Well. I wanted to check..." → "I wanted to check..."
            "well",               "ok so",
            "okay so",            "alright so",
            "so",                 "ok",
            "okay",               "alright",
            "anyway",             "actually",
            "basically",
        ];

        foreach (var greet in greetingPrefixes)
        {
            if (lower.StartsWith(greet))
            {
                text  = text[greet.Length..].TrimStart(' ', ',', '!', '.', '-');
                lower = text.ToLowerInvariant();
                break;
            }
        }

        // ── Strip assistant name prefix variants ──────────────────────
        // After removing "hey/hi/hello", users often have a name token
        // next ("thadds!") which is pure salutation, not search topic.
        string[] assistantNamePrefixes =
        [
            "sir thaddeus",
            "thaddeus",
            "thadds",
            "thaddy",
            "thadd"
        ];

        foreach (var name in assistantNamePrefixes)
        {
            if (lower.StartsWith(name))
            {
                text  = text[name.Length..].TrimStart(' ', ',', '!', '.', '?', '-', ':');
                lower = text.ToLowerInvariant();
                break;
            }
        }

        // ── Strip "how are you" / chit-chat follow-ups ────────────────
        string[] chitChat =
        [
            "how the heck are you today",
            "how the heck are you",
            "how are you doing today",
            "how are you doing",
            "how are you today",
            "how are you",
            "how's it going",
            "hows it going",
            "what's up",
            "whats up",
        ];

        foreach (var cc in chitChat)
        {
            if (lower.StartsWith(cc))
            {
                text  = text[cc.Length..].TrimStart(' ', ',', '!', '.', '?', '-');
                lower = text.ToLowerInvariant();
                break;
            }
        }

        // ── Leading filler phrases (order matters: longest first) ────
        string[] leadPhrases =
        [
            "actually, can you check up",
            "actually can you check up",
            "actually, can you check",
            "actually can you check",
            "can you check up the news on",
            "can you check up news on",
            "can you check up on",
            "can you check the news today",
            "can you look up the news today",
            "can you search for",   "can you search up",
            "can you look up",      "can you look into",
            "can you find out",     "can you find me",
            "can you pull up",      "can you check on",
            "can you check up",     "can you check",
            "can you get me",
            "could you search for", "could you look up",
            "could you find",       "could you check",
            "please search for",    "please look up",
            "please find",          "please check",
            "i want to look up information on how",
            "i want to look up information on",
            "i want to look up information about",
            "i want to look up info on",
            "i want to look up",    "i want to search for",
            "i want to find out about",
            "i want to find out",   "i want to find",
            "i want to know about", "i want to know",
            "i want to see whats happening with",
            "i want to see what's happening with",
            "i want to see",
            "i need to look up",    "i need to find",
            "i'd like to know about", "i'd like to know",
            "tell me about",        "tell me what",
            "show me",              "get me",
            "look up",              "search for",
            "search up",            "pull up the news on",
            "pull up the news about",
            "pull up news on",      "pull up news about",
            "pull up the news",     "pull up news",
            "pull up",              "find out about",
            "find out",             "check on",
            "check the",            "what's going on with",
            "whats going on with",  "what is going on with",
            "what happened with",   "what happened to",
            "what's happening with", "whats happening with",
            "how has",              "how is",
        ];

        foreach (var phrase in leadPhrases)
        {
            if (lower.StartsWith(phrase))
            {
                text  = text[phrase.Length..].TrimStart(' ', ',', ':', '-');
                lower = text.ToLowerInvariant();
                break; // Only strip one leading phrase
            }
        }

        // ── Trailing filler ─────────────────────────────────────────
        string[] trailPhrases =
        [
            "for me please", "for me", "please", "right now",
            "if you can", "if possible", "when you get a chance"
        ];

        foreach (var phrase in trailPhrases)
        {
            if (lower.EndsWith(phrase))
            {
                text  = text[..^phrase.Length].TrimEnd(' ', ',', '.');
                lower = text.ToLowerInvariant();
                break;
            }
        }

        // ── Sentence splitting: if multiple sentences remain,
        // keep the one with the most topic signal (proper nouns,
        // domain-specific words) rather than emotional commentary ─────
        var sentences = text.Split(['.', '?', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 2)
            .ToArray();

        if (sentences.Length > 1)
        {
            // Words that are pure emotional/conversational filler —
            // they tell us nothing about what to search for.
            string[] fillerWords = ["i", "you", "can", "could", "want",
                "wanted", "need", "needed",
                "to", "the", "a", "an", "do", "does", "is", "are",
                "was", "were", "been", "have", "has", "had",
                "please", "check", "look", "find", "search", "up",
                "me", "my", "on", "about", "information", "info",
                "it", "its", "it's", "been", "what", "how", "so",
                "just", "really", "actually", "basically", "totally",
                // Discourse markers / hedges — must be penalized so
                // single-word sentences like "Well" never win.
                "well", "ok", "okay", "alright", "anyway", "right",
                "sure", "yes", "no", "yeah", "yep", "nope",
                "there", "here", "this", "that"];

            // Words that signal "this sentence has a real topic."
            // Sentences with uppercase words (proper nouns) or domain
            // keywords score higher.
            string[] topicSignals = ["news", "market", "stock", "crypto",
                "price", "weather", "score", "game", "election",
                "update", "latest", "recent", "happening",
                "headlines", "breaking", "sports", "politics",
                "tech", "technology", "science", "war", "economy",
                "finance", "results", "recap", "forecast"];

            var best = sentences
                .OrderByDescending(s =>
                {
                    var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var lowerWords = words.Select(w => w.ToLowerInvariant()).ToArray();

                    // Count uppercase-start words (likely proper nouns)
                    var properNouns = words.Count(w =>
                        w.Length > 1 && char.IsUpper(w[0]));

                    // Count topic signal words
                    var topicHits = lowerWords.Count(w =>
                        topicSignals.Contains(w));

                    // Penalize high filler ratio
                    var fillerRatio = words.Length > 0
                        ? (double)lowerWords.Count(w => fillerWords.Contains(w)) / words.Length
                        : 1.0;

                    // Score: more proper nouns & topic words = better,
                    // high filler ratio = worse
                    return (properNouns * 3) + (topicHits * 2) - (fillerRatio * 5);
                })
                .ThenByDescending(s => s.Length)
                .First();

            text = best;
        }

        // Final trim
        text = text.Trim(' ', '?', '!', '.', ',');

        // If we stripped everything, fall back to the original
        return text.Length >= 3 ? text : input.Trim(' ', '?', '!', '.', ',');
    }

    /// <summary>
    /// Normalizes an LLM-returned recency string to a known value.
    /// </summary>
    private static string NormalizeRecency(string raw)
    {
        var r = (raw ?? "any").Trim().ToLowerInvariant();
        return r switch
        {
            "day" or "today" or "24h"   => "day",
            "week" or "7d"              => "week",
            "month" or "30d"            => "month",
            _                           => "any"
        };
    }
}
