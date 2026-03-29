namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Concentrates intent-oriented string heuristics outside the orchestrator.
/// </summary>
public static class IntentFeatureExtractor
{
    public readonly record struct WebLookupHeuristicEvidence(
        double Score,
        string ReasonCode,
        bool ShouldLookup,
        double Confidence);

    public static bool LooksLikeScreenRequest(string lower)
    {
        ReadOnlySpan<string> patterns =
        [
            "what's on my screen",   "whats on my screen",
            "what is on my screen",
            "what's on my screen right now",
            "whats on my screen right now",
            "what is on my screen right now",
            "tell me what's on my screen",
            "tell me whats on my screen",
            "tell me what is on my screen",
            "what can you see",      "what do you see",
            "look at my screen",     "look at the screen",
            "take a screenshot",     "screenshot",
            "capture the screen",    "capture my screen",
            "screen capture",        "what's happening on screen",
            "show me my screen",     "read my screen",
            "what's on the screen",  "whats on the screen",
            "what is on the screen",
            "active window",
            "look at my cursor",     "look at cursor",
            "what's in my editor",   "whats in my editor",
            "look at my editor",     "look at my ide",
            "look at my code",       "look at this code",
            "see my code",           "see what i'm working on",
            "see what im working on",

            // Machine / computer observation
            "what is going on on my machine",
            "what's going on on my machine",
            "whats going on on my machine",
            "what's happening on my machine",
            "whats happening on my machine",
            "what is happening on my computer",
            "what's happening on my computer",
            "what is my computer doing",
            "what's my computer doing",
            "whats my computer doing",
            "what is my machine doing",
            "show me what's happening",
            "show me whats happening",

            // Browser / page observation
            "summarize this page",   "summarize the page",
            "summarize this site",   "summarize this website",
            "what's on this page",   "whats on this page",
            "read this page",        "read the page",
            "what page is this",     "what site is this",
            "what am i looking at",  "what am i reading",
            "what's this page about", "whats this page about",
            "tell me about this page",
            "can you read this",     "can you see this",
            "what does this say",    "what does this page say",
            "describe what i see",   "describe this page",
            "summarize what i'm looking at",
            "summarize what im looking at"
        ];

        return ContainsLoosePhrase(lower, patterns);
    }

    public static bool LooksLikeFileRequest(string lower)
    {
        ReadOnlySpan<string> patterns =
        [
            "read the file",   "read this file",    "read file",
            "file_read",       "file read",
            "open the file",   "open this file",    "open file",
            "list files",      "list the files",    "show files",
            "what's in the file", "whats in the file",
            "file contents",   "show me the file",
            "directory listing", "folder contents",
            "list directory",  "ls ",
            "what's in my folder", "whats in my folder",
            "what is in my folder",
            "what's in this folder", "whats in this folder",
            "what is in this folder",
            "what's in that folder", "whats in that folder",
            "what is in that folder",
            "what's in my personal folder", "whats in my personal folder",
            "what is in my personal folder",
            "read my personal folder",
            "read my folder",
            "read this folder",
            "tell me whats in there",
            "tell me what's in there",
            "can you see what is in my folder",
            "can you see what is in this folder",
            "can you see what is in my personal folder",
            "show me my files", "show me what's in my folder",
            "show me whats in my folder",
            "what files are in"
        ];

        return ContainsLoosePhrase(lower, patterns);
    }

    public static bool LooksLikeSystemCommand(string lower)
    {
        ReadOnlySpan<string> patterns =
        [
            "run the command",     "run this command",
            "run command",         "execute command",
            "execute the command", "execute this",
            "open this program",   "launch ",
            "run this program",    "start the ",
            "system command",      "shell command",
            "terminal command"
        ];

        foreach (var p in patterns)
        {
            if (lower.Contains(p, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Detects when the user explicitly names a tool for invocation,
    /// e.g. "use tool_ping", "call the tool_capabilities tool".
    /// These should route to GeneralTool with high confidence so
    /// the Footman does not override with a chat-only decision.
    /// </summary>
    public static bool LooksLikeExplicitToolInvocation(string lower)
    {
        return TryGetExplicitToolInvocationIntent(lower) is not null;
    }

    /// <summary>
    /// Maps explicit "use/call/run (tool)" prompts to the safest
    /// deterministic route for that tool family.
    /// </summary>
    public static string? TryGetExplicitToolInvocationIntent(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return null;

        ReadOnlySpan<string> actionPhrases =
        [
            "use ", "call ", "invoke ", "run ", "execute ", "try "
        ];

        var hasAction = false;
        foreach (var action in actionPhrases)
        {
            if (lower.Contains(action, StringComparison.Ordinal))
            {
                hasAction = true;
                break;
            }
        }

        if (!hasAction)
            return null;

        if (ContainsAny(lower,
            [
                "file_read", "file read", "file_list", "file list", "file_write", "file write", "document_read", "document read",
                "knowledge_store", "knowledge store", "knowledge_store_create_file", "knowledge_store_append_to_file",
                "knowledge_store_read_file", "knowledge_store_list_files", "knowledge_store_journal_log_entry", "knowledge_store_list_roots"
            ]))
        {
            return Intents.FileTask;
        }

        if (ContainsAny(lower,
            ["screen_capture", "screen capture", "get_active_window", "active window"]))
        {
            return Intents.ScreenObserve;
        }

        if (ContainsAny(lower,
            ["system_execute", "system execute", "shell command", "terminal command", "clipboard_read", "clipboard read", "clipboard_write", "clipboard write"]))
        {
            return Intents.SystemTask;
        }

        if (ContainsAny(lower,
            ["web_search", "web search", "browser_navigate", "browser navigate", "places_lookup", "places lookup"]))
        {
            return Intents.LookupSearch;
        }

        if (ContainsAny(lower,
            ["memory_store", "memory_store_facts", "memory_update_fact", "memory_delete_fact"]))
        {
            return Intents.MemoryWrite;
        }

        if (ContainsAny(lower,
            ["memory_retrieve", "memory_list_facts", "tool_ping", "tool_list_capabilities", "capabilities.describe", "policy.get_state", "health.check", "time_now"]))
        {
            return Intents.GeneralTool;
        }

        return null;
    }

    public static bool LooksLikeBrowseRequest(string lower)
    {
        ReadOnlySpan<string> patterns =
        [
            "go to this url",      "go to this website",
            "go to this page",     "go to this site",
            "navigate to",         "open this url",
            "open this website",   "open this page",
            "open this link",      "visit this",
            "browse to",           "fetch this url",
            "fetch this page"
        ];

        foreach (var p in patterns)
        {
            if (lower.Contains(p, StringComparison.Ordinal))
                return true;
        }

        if (lower.Contains("http://", StringComparison.Ordinal) ||
            lower.Contains("https://", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public static bool LooksLikeGreeting(string lower)
    {
        ReadOnlySpan<string> exact =
        [
            "hi",  "hey", "hello", "yo", "sup",
            "hi!", "hey!", "hello!", "yo!", "sup!",
            "good morning", "good afternoon", "good evening",
            "gm", "morning", "howdy", "hiya", "greetings",
            "what's up", "whats up", "what's good", "whats good"
        ];

        foreach (var g in exact)
        {
            if (lower == g)
                return true;
        }

        if (lower.Length > 40)
            return false;

        ReadOnlySpan<string> prefixes =
        [
            "hi ", "hey ", "hello ", "yo ", "sup ",
            "good morning", "good afternoon", "good evening",
            "howdy ", "hiya ", "greetings"
        ];

        foreach (var p in prefixes)
        {
            if (lower.StartsWith(p, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True for pure greeting/small-talk messages that should stay chat-only.
    /// This intentionally excludes greeting + actionable requests
    /// (e.g. "hello, what's the weather in Seattle?").
    /// </summary>
    public static bool LooksLikeGreetingOnlyOrSmallTalk(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var conversational = LooksLikeConversationalCheckIn(lower);
        var greeting = LooksLikeGreeting(lower);
        if (!conversational && !greeting)
            return false;

        if (LooksLikeMemoryWriteRequest(lower) ||
            LooksLikeScreenRequest(lower) ||
            LooksLikeFileRequest(lower) ||
            LooksLikeSystemCommand(lower) ||
            LooksLikeBrowseRequest(lower))
        {
            return false;
        }

        if (TryGetExplicitToolInvocationIntent(lower) is not null)
            return false;

        if (LooksLikeDeepDiveLookup(lower) ||
            LooksLikeExplicitNewsLookup(lower) ||
            LooksLikeLocalBusinessDiscovery(lower) ||
            LooksLikeIdentityLookup(lower) ||
            LooksLikeWebSearchRequest(lower))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Detects short, non-actionable transcript fragments that are common
    /// when push-to-talk clipping drops leading words (e.g. "world.").
    /// These should default to chat instead of forcing lookup/search.
    /// </summary>
    public static bool LooksLikeStrayTranscriptFragment(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var normalized = NormalizeLoosePhraseInput(lower);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        // If explicit intent/tool/search signals exist, do not suppress.
        if (TryGetExplicitToolInvocationIntent(lower) is not null ||
            LooksLikeMemoryWriteRequest(lower) ||
            LooksLikeScreenRequest(lower) ||
            LooksLikeFileRequest(lower) ||
            LooksLikeSystemCommand(lower) ||
            LooksLikeBrowseRequest(lower) ||
            LooksLikeDeepDiveLookup(lower) ||
            LooksLikeExplicitNewsLookup(lower) ||
            LooksLikeLocalBusinessDiscovery(lower) ||
            LooksLikeIdentityLookup(lower) ||
            LooksLikeWebSearchRequest(lower))
        {
            return false;
        }

        if (normalized.Contains(' '))
            return false;

        // Single-token fragments that frequently appear from clipped STT.
        return normalized.Equals("world", StringComparison.Ordinal) ||
               normalized.Equals("stuff", StringComparison.Ordinal) ||
               normalized.Equals("things", StringComparison.Ordinal) ||
               normalized.Equals("okay", StringComparison.Ordinal) ||
               normalized.Equals("ok", StringComparison.Ordinal) ||
               normalized.Equals("hmm", StringComparison.Ordinal) ||
               normalized.Equals("uh", StringComparison.Ordinal) ||
               normalized.Equals("huh", StringComparison.Ordinal);
    }

    public static bool LooksLikeReasoningFollowUp(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower) || lower.Length > 220)
            return false;

        var ultraShortWhy =
            string.Equals(lower, "why", StringComparison.Ordinal) ||
            string.Equals(lower, "why?", StringComparison.Ordinal) ||
            string.Equals(lower, "but why", StringComparison.Ordinal) ||
            string.Equals(lower, "but why?", StringComparison.Ordinal);

        var asksForReasoning =
            lower.Contains("explain why", StringComparison.Ordinal) ||
            lower.Contains("logic behind", StringComparison.Ordinal) ||
            lower.Contains("reasoning behind", StringComparison.Ordinal) ||
            lower.Contains("what's your reasoning", StringComparison.Ordinal) ||
            lower.Contains("whats your reasoning", StringComparison.Ordinal) ||
            lower.Contains("explain your reasoning", StringComparison.Ordinal) ||
            lower.Contains("explain that reasoning", StringComparison.Ordinal) ||
            lower.Contains("what made you choose", StringComparison.Ordinal) ||
            lower.Contains("how did you decide", StringComparison.Ordinal) ||
            lower.Contains("why that", StringComparison.Ordinal) ||
            lower.Contains("why this", StringComparison.Ordinal) ||
            lower.Contains("why it", StringComparison.Ordinal) ||
            lower.StartsWith("but why", StringComparison.Ordinal) ||
            lower.StartsWith("why ", StringComparison.Ordinal);

        if (!ultraShortWhy && !asksForReasoning)
            return false;

        var hasReferentialCue =
            lower.Contains("that", StringComparison.Ordinal) ||
            lower.Contains("this", StringComparison.Ordinal) ||
            lower.Contains("it", StringComparison.Ordinal) ||
            lower.Contains("your reasoning", StringComparison.Ordinal) ||
            lower.Contains("your decision", StringComparison.Ordinal);

        if (!ultraShortWhy && !hasReferentialCue)
            return false;

        if (lower.Contains("source", StringComparison.Ordinal) ||
            lower.Contains("citation", StringComparison.Ordinal) ||
            lower.Contains("article", StringComparison.Ordinal) ||
            lower.Contains("url", StringComparison.Ordinal) ||
            lower.Contains("link", StringComparison.Ordinal) ||
            lower.Contains("reference", StringComparison.Ordinal) ||
            lower.Contains("evidence", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Detects conversational check-ins that may include temporal words
    /// like "today" but should remain chat-only.
    /// </summary>
    public static bool LooksLikeConversationalCheckIn(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var hasDirectCheckIn =
            lower.Contains("how are you", StringComparison.Ordinal) ||
            lower.Contains("hows it going", StringComparison.Ordinal) ||
            lower.Contains("how's it going", StringComparison.Ordinal) ||
            lower.Contains("how have you been", StringComparison.Ordinal) ||
            lower.Contains("how've you been", StringComparison.Ordinal) ||
            lower.Contains("you doing", StringComparison.Ordinal) ||
            lower.Contains("you been", StringComparison.Ordinal);

        if (hasDirectCheckIn)
            return true;

        var hasGreetingLead =
            lower.StartsWith("hi", StringComparison.Ordinal) ||
            lower.StartsWith("hey", StringComparison.Ordinal) ||
            lower.StartsWith("hello", StringComparison.Ordinal) ||
            lower.StartsWith("good morning", StringComparison.Ordinal) ||
            lower.StartsWith("good afternoon", StringComparison.Ordinal) ||
            lower.StartsWith("good evening", StringComparison.Ordinal);

        if (!hasGreetingLead)
            return false;

        return lower.Contains("hope", StringComparison.Ordinal) ||
               lower.Contains("things are good", StringComparison.Ordinal) ||
               lower.Contains("doing good", StringComparison.Ordinal) ||
               lower.Contains("doing well", StringComparison.Ordinal);
    }

    /// <summary>
    /// Detects short microphone check / dictation test phrases that should
    /// stay in chat mode and never trigger web lookup.
    /// </summary>
    public static bool LooksLikeVoiceMicCheck(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var normalized = NormalizeLoosePhraseInput(lower);
        if (normalized.Length == 0 || normalized.Length > 80)
            return false;

        ReadOnlySpan<string> phrases =
        [
            "testing testing",
            "testing one two three",
            "testing testing one two three",
            "test test",
            "mic check",
            "check one two",
            "check one two three",
            "one two three"
        ];

        foreach (var phrase in phrases)
        {
            if (normalized.Contains(phrase, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static bool LooksLikeMemoryWriteRequest(string lower)
    {
        var normalized = NormalizeLoosePhraseInput(lower);

        ReadOnlySpan<string> storagePhrases =
        [
            "remember that", "remember this", "remember i",
            "remember my", "remember me",
            "please remember", "can you remember",
            "note that", "note this", "make a note",
            "save that", "save this",
            "don't forget", "do not forget",
            "keep in mind", "store that", "store this"
        ];

        foreach (var phrase in storagePhrases)
        {
            if (lower.Contains(phrase, StringComparison.Ordinal) ||
                normalized.Contains(phrase, StringComparison.Ordinal))
                return true;
        }

        ReadOnlySpan<string> correctionPhrases =
        [
            "changed my mind",  "change my mind",
            "i actually",       "actually i",     "actually, i",
            "i decided",        "i've decided",
            "correction:",      "correct that",
            "update my",        "update that",
            "no wait",          "on second thought",
            "scratch that",     "take that back",
            "i was wrong",      "i meant"
        ];

        foreach (var phrase in correctionPhrases)
        {
            if (lower.Contains(phrase, StringComparison.Ordinal) ||
                normalized.Contains(phrase, StringComparison.Ordinal))
                return true;
        }

        ReadOnlySpan<string> revocationPhrases =
        [
            "i no longer",      "i don't like",    "i don't want",
            "i dont like",      "i dont want",
            "forget that",      "forget i",
            "remove that",      "delete that",
            "i stopped",        "i quit"
        ];

        foreach (var phrase in revocationPhrases)
        {
            if (lower.Contains(phrase, StringComparison.Ordinal) ||
                normalized.Contains(phrase, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static bool LooksLikeWebSearchRequest(string lower)
        => GetWebLookupHeuristicEvidence(lower).ShouldLookup;

    public static bool LooksLikeSelfContainedKnowledgeOrReasoningPrompt(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        if (TryGetExplicitToolInvocationIntent(lower) is not null ||
            LooksLikeScreenRequest(lower) ||
            LooksLikeFileRequest(lower) ||
            LooksLikeSystemCommand(lower) ||
            LooksLikeBrowseRequest(lower) ||
            LooksLikeMemoryWriteRequest(lower) ||
            LooksLikeExplicitNewsLookup(lower) ||
            LooksLikeDeepDiveLookup(lower) ||
            LooksLikeLocalBusinessDiscovery(lower) ||
            LooksLikeFactLookup(lower) ||
            LooksLikeWebSearchRequest(lower))
        {
            return false;
        }

        if (LooksLikeLogicPuzzlePrompt(lower))
            return true;

        var startsWithKnowledgeCue =
            lower.StartsWith("explain ", StringComparison.Ordinal) ||
            lower.StartsWith("describe ", StringComparison.Ordinal) ||
            lower.StartsWith("teach me ", StringComparison.Ordinal) ||
            lower.StartsWith("tell me about ", StringComparison.Ordinal) ||
            lower.StartsWith("what is ", StringComparison.Ordinal) ||
            lower.StartsWith("what's ", StringComparison.Ordinal) ||
            lower.StartsWith("whats ", StringComparison.Ordinal) ||
            lower.StartsWith("how does ", StringComparison.Ordinal) ||
            lower.StartsWith("how do ", StringComparison.Ordinal) ||
            lower.StartsWith("why does ", StringComparison.Ordinal) ||
            lower.StartsWith("why is ", StringComparison.Ordinal);

        if (!startsWithKnowledgeCue)
            return false;

        var hasCurrentEventsCue =
            lower.Contains("today", StringComparison.Ordinal) ||
            lower.Contains("latest", StringComparison.Ordinal) ||
            lower.Contains("recent", StringComparison.Ordinal) ||
            lower.Contains("right now", StringComparison.Ordinal) ||
            lower.Contains("currently", StringComparison.Ordinal) ||
            lower.Contains("this week", StringComparison.Ordinal) ||
            lower.Contains("this month", StringComparison.Ordinal);

        if (hasCurrentEventsCue)
            return false;

        var hasLocalBusinessCue =
            lower.Contains(" hours", StringComparison.Ordinal) ||
            lower.Contains(" open", StringComparison.Ordinal) ||
            lower.Contains(" close", StringComparison.Ordinal) ||
            lower.Contains("near me", StringComparison.Ordinal) ||
            lower.Contains("nearby", StringComparison.Ordinal);

        return !hasLocalBusinessCue;
    }

    public static WebLookupHeuristicEvidence GetWebLookupHeuristicEvidence(string lower)
    {
        if (LooksLikeConversationalCheckIn(lower))
            return new WebLookupHeuristicEvidence(0.0, "conversational_check_in", false, 0.0);

        if (LooksLikeVoiceMicCheck(lower))
            return new WebLookupHeuristicEvidence(0.0, "voice_mic_check", false, 0.0);

        if (LooksLikeLogicPuzzlePrompt(lower))
            return new WebLookupHeuristicEvidence(0.0, "logic_puzzle", false, 0.0);

        if (LooksLikePreferenceOrOpinionPrompt(lower))
            return new WebLookupHeuristicEvidence(0.0, "opinion_or_preference", false, 0.0);

        if (LooksLikeSeasonEpisodePlotLookup(lower))
            return new WebLookupHeuristicEvidence(3.0, "season_episode_lookup", true, 0.96);

        if (LooksLikeReleasedProductExistenceLookup(lower))
            return new WebLookupHeuristicEvidence(3.0, "released_product_existence", true, 0.95);

        if (lower.Contains("web_search", StringComparison.Ordinal) ||
            lower.Contains("web search", StringComparison.Ordinal))
        {
            return new WebLookupHeuristicEvidence(3.0, "explicit_web_search", true, 0.96);
        }

        var score = 0.0;
        var reasonCode = "none";
        var strongest = 0.0;
        void AddSignal(double weight, string reason)
        {
            score += weight;
            if (weight > strongest)
            {
                strongest = weight;
                reasonCode = reason;
            }
        }

        ReadOnlySpan<string> phrases =
        [
            "search for",   "search up",    "look up",     "look into",
            "google ",      "find me ",     "find out ",
            "news on ",     "news about ",  "news for ",
            "price of ",    "price for ",
            "updates on ",  "update on ",   "updates about ",
            "what's the price", "whats the price",
            "how much is",  "how much does",
            "search the web",  "search online",    "look it up",
            "look this up",    "find information",  "find info on",
            "search about",

            // Recommendation / product signals -- these imply the user
            // needs current real-world data, not a canned opinion.
            "recommend",    "suggestion for",  "suggestions for",
            "best ",        "top rated",        "top-rated",
            "on amazon",    "on ebay",          "on etsy",
            "on walmart",

            // Comparison signals -- comparing real-world entities
            // typically needs current info (movies, products, etc.)
            "compared to",    "how does it compare",
            "word for word",  "similar to the original",
            "like the original",

            // Conversational search phrasing. Keep this strict to avoid
            // routing casual conversation into web lookup.
            "tell me if",     "tell me whether"
        ];

        foreach (var phrase in phrases)
        {
            if (lower.Contains(phrase, StringComparison.Ordinal))
            {
                AddSignal(2.0, "explicit_search_phrase");
                break;
            }
        }

        // Temporal freshness + update/info keywords together strongly
        // signal a need for live web data regardless of domain topic.
        if (HasTemporalFreshnessWithUpdateCue(lower))
            AddSignal(2.0, "freshness_update_combo");

        if (LooksLikeIdentityLookup(lower))
            AddSignal(1.2, "identity_lookup");

        var hasTopic =
            lower.Contains("news", StringComparison.Ordinal) ||
            lower.Contains("headline", StringComparison.Ordinal) ||
            lower.Contains("price", StringComparison.Ordinal) ||
            lower.Contains("stock", StringComparison.Ordinal) ||
            lower.Contains("market", StringComparison.Ordinal) ||
            lower.Contains("dow jones", StringComparison.Ordinal) ||
            lower.Contains("dow", StringComparison.Ordinal) ||
            lower.Contains("nasdaq", StringComparison.Ordinal) ||
            lower.Contains("s&p", StringComparison.Ordinal) ||
            lower.Contains("s and p", StringComparison.Ordinal) ||
            lower.Contains("sp500", StringComparison.Ordinal) ||
            lower.Contains("weather", StringComparison.Ordinal) ||
            lower.Contains("forecast", StringComparison.Ordinal) ||
            lower.Contains("score", StringComparison.Ordinal) ||
            lower.Contains("crypto", StringComparison.Ordinal) ||
            lower.Contains("bitcoin", StringComparison.Ordinal) ||
            lower.Contains("dogecoin", StringComparison.Ordinal) ||
            lower.Contains("ethereum", StringComparison.Ordinal) ||
            lower.Contains("solana", StringComparison.Ordinal) ||
            lower.Contains("forex", StringComparison.Ordinal) ||
            lower.Contains(".com", StringComparison.Ordinal) ||
            lower.Contains("movie", StringComparison.Ordinal) ||
            lower.Contains("film", StringComparison.Ordinal) ||
            lower.Contains("live action", StringComparison.Ordinal) ||
            lower.Contains("review", StringComparison.Ordinal) ||
            lower.Contains("rating", StringComparison.Ordinal) ||
            lower.Contains("supplement", StringComparison.Ordinal) ||
            lower.Contains("product", StringComparison.Ordinal);

        if (hasTopic)
            AddSignal(1.0, "domain_topic");

        var hasMarketTopic =
            lower.Contains("stock", StringComparison.Ordinal) ||
            lower.Contains("share price", StringComparison.Ordinal) ||
            lower.Contains("market", StringComparison.Ordinal) ||
            lower.Contains("nasdaq", StringComparison.Ordinal) ||
            lower.Contains("dow", StringComparison.Ordinal) ||
            lower.Contains("s&p", StringComparison.Ordinal) ||
            lower.Contains("crypto", StringComparison.Ordinal) ||
            lower.Contains("bitcoin", StringComparison.Ordinal) ||
            lower.Contains("ethereum", StringComparison.Ordinal);

        var hasQuoteCue =
            lower.Contains("price", StringComparison.Ordinal) ||
            lower.Contains("quote", StringComparison.Ordinal) ||
            lower.Contains("trading at", StringComparison.Ordinal) ||
            lower.Contains("worth", StringComparison.Ordinal);

        var hasFreshnessCue =
            lower.Contains("today", StringComparison.Ordinal) ||
            lower.Contains("right now", StringComparison.Ordinal) ||
            lower.Contains("currently", StringComparison.Ordinal) ||
            lower.Contains("latest", StringComparison.Ordinal) ||
            lower.Contains("live", StringComparison.Ordinal) ||
            lower.Contains("current", StringComparison.Ordinal);

        if ((hasMarketTopic || hasQuoteCue) && hasFreshnessCue)
            AddSignal(1.2, "market_fresh_quote");

        if (lower.Contains('?', StringComparison.Ordinal))
            AddSignal(0.8, "question_mark");

        if (lower.Contains("can you", StringComparison.Ordinal) ||
            lower.Contains("could you", StringComparison.Ordinal) ||
            lower.Contains("would you", StringComparison.Ordinal) ||
            lower.Contains("will you", StringComparison.Ordinal) ||
            lower.Contains("please", StringComparison.Ordinal))
        {
            AddSignal(0.4, "request_language");
        }

        if (lower.Contains("pull", StringComparison.Ordinal) ||
            lower.Contains("look", StringComparison.Ordinal) ||
            lower.Contains("check", StringComparison.Ordinal) ||
            lower.Contains("find", StringComparison.Ordinal) ||
            lower.Contains("show", StringComparison.Ordinal) ||
            lower.Contains("get", StringComparison.Ordinal) ||
            lower.Contains("bring", StringComparison.Ordinal) ||
            lower.Contains("grab", StringComparison.Ordinal) ||
            lower.Contains("fetch", StringComparison.Ordinal) ||
            lower.Contains("tell", StringComparison.Ordinal) ||
            lower.Contains("give", StringComparison.Ordinal) ||
            lower.Contains("update", StringComparison.Ordinal))
        {
            AddSignal(0.8, "retrieval_verb");
        }

        if (lower.Contains("what", StringComparison.Ordinal) ||
            lower.Contains("how", StringComparison.Ordinal) ||
            lower.Contains("where", StringComparison.Ordinal) ||
            lower.Contains("when", StringComparison.Ordinal) ||
            lower.Contains("who", StringComparison.Ordinal) ||
            lower.Contains("why", StringComparison.Ordinal))
        {
            AddSignal(0.4, "question_word");
        }

        if (lower.Contains("today", StringComparison.Ordinal) ||
            lower.Contains("tonight", StringComparison.Ordinal) ||
            lower.Contains("yesterday", StringComparison.Ordinal) ||
            lower.Contains("last week", StringComparison.Ordinal) ||
            lower.Contains("this week", StringComparison.Ordinal) ||
            lower.Contains("past week", StringComparison.Ordinal) ||
            lower.Contains("last month", StringComparison.Ordinal) ||
            lower.Contains("this month", StringComparison.Ordinal) ||
            lower.Contains("right now", StringComparison.Ordinal) ||
            lower.Contains("currently", StringComparison.Ordinal) ||
            lower.Contains("latest", StringComparison.Ordinal) ||
            lower.Contains("recent", StringComparison.Ordinal) ||
            lower.Contains("lately", StringComparison.Ordinal))
        {
            AddSignal(0.8, "freshness_term");
        }

        // Require enough evidence so conversational prompts don't escalate.
        var shouldLookup = score >= 2.0;
        var confidence = shouldLookup
            ? Math.Clamp(0.55 + (score * 0.08), 0.55, 0.96)
            : Math.Clamp(score * 0.2, 0.0, 0.5);

        return new WebLookupHeuristicEvidence(
            Score: score,
            ReasonCode: reasonCode,
            ShouldLookup: shouldLookup,
            Confidence: confidence);
    }

    public static bool LooksLikeExplicitNewsLookup(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        ReadOnlySpan<string> newsSignals =
        [
            "news",
            "headline",
            "headlines",
            "article",
            "articles",
            "coverage",
            "top stories",
            "top story",
            "news feed",
            "read more"
        ];

        ReadOnlySpan<string> listSignals =
        [
            "show me",
            "list",
            "pull up",
            "give me",
            "get me",
            "find me",
            "bring me",
            "send me"
        ];

        ReadOnlySpan<string> timeSignals =
        [
            "today",
            "tonight",
            "latest",
            "recent",
            "this week",
            "last week",
            "this month",
            "past week"
        ];

        var hasNewsSignal = ContainsAny(lower, newsSignals);
        if (!hasNewsSignal)
            return false;

        if (ContainsAny(lower, listSignals) || ContainsAny(lower, timeSignals))
            return true;

        return lower.Contains("news on ", StringComparison.Ordinal) ||
               lower.Contains("news about ", StringComparison.Ordinal) ||
               lower.Contains("news for ", StringComparison.Ordinal) ||
             lower.Contains("local news", StringComparison.Ordinal) ||
               lower.Contains("headlines on ", StringComparison.Ordinal) ||
               lower.Contains("headlines about ", StringComparison.Ordinal) ||
               lower.Contains("latest news", StringComparison.Ordinal) ||
               lower.Contains("recent news", StringComparison.Ordinal);
    }

    public static bool LooksLikeDeepDiveLookup(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        ReadOnlySpan<string> explicitSignals =
        [
            "deep dive",
            "hours + reviews",
            "hours and reviews",
            "tell me when it opens",
            "tell me when it closes",
            "what time does",
            "what time do they open",
            "what time do they close",
            "opening hours",
            "closing time",
            "store hours",
            "business hours",
            "hours of operation",
            "create a briefing",
            "give me a briefing",
            "brief me on",
            "briefing on",
            "briefing for"
        ];

        if (ContainsAny(lower, explicitSignals))
            return true;

        ReadOnlySpan<string> followUpDeepDiveSignals =
        [
            "pull me up more info on",
            "pull me up more info about",
            "bring me up more info on",
            "bring me up more info about",
            "tell me more about",
            "more info on",
            "more info about"
        ];

        if (ContainsAny(lower, followUpDeepDiveSignals))
        {
            ReadOnlySpan<string> businessTerms =
            [
                "restaurant", "restaurants", "cafe", "coffee shop", "diner",
                "florist", "florists", "bakery", "bakeries",
                "bar", "pub", "store", "shop",
                "grocery", "groceries", "supermarket", "pharmacy", "pharmacies",
                "bank", "banks", "credit union",
                "park", "parks", "playground",
                "hotel", "motel",
                "gas station", "car wash", "laundromat", "salon", "barber",
                "gym", "dentist", "clinic", "doctor", "urgent care"
            ];

            if (ContainsAny(lower, businessTerms) && !LooksLikeLocalBusinessDiscovery(lower))
                return true;
        }

        // Natural phrasing often includes "tell me when <place> is open/closed".
        var hasTellMeWhen = lower.Contains("tell me when", StringComparison.Ordinal);
        var hasWhatTime = lower.Contains("what time", StringComparison.Ordinal);
        var hasOpenCloseLanguage =
            lower.Contains(" open", StringComparison.Ordinal) ||
            lower.Contains(" opens", StringComparison.Ordinal) ||
            lower.Contains(" opening", StringComparison.Ordinal) ||
            lower.Contains(" close", StringComparison.Ordinal) ||
            lower.Contains(" closes", StringComparison.Ordinal) ||
            lower.Contains(" closing", StringComparison.Ordinal);
        if ((hasTellMeWhen || hasWhatTime) && hasOpenCloseLanguage)
            return true;

        // ── "Is X open" patterns ─────────────────────────────────────
        if (LooksLikeIsOpenQuery(lower))
            return true;

        // ── "When does X open/close" patterns ────────────────────────
        if (LooksLikeWhenOpenQuery(lower))
            return true;

        // NOTE: LooksLikeLocalBusinessDiscovery is intentionally NOT called
        // here. Discovery queries ("find bakeries nearby") should route to
        // WebFactFind so users see multiple options. Only specific-place
        // queries (hours, briefings, "is X open") belong in DeepDive.

        var hasHoursVerb =
            lower.Contains("open", StringComparison.Ordinal) ||
            lower.Contains("close", StringComparison.Ordinal) ||
            lower.Contains("hours", StringComparison.Ordinal);

        var hasReviewVerb =
            lower.Contains("reviews", StringComparison.Ordinal) ||
            lower.Contains("rating", StringComparison.Ordinal) ||
            lower.Contains("what to expect", StringComparison.Ordinal);

        return hasHoursVerb && hasReviewVerb;
    }

    /// <summary>
    /// Detects "is X open" / "are they open" / "is it open" phrasing.
    /// </summary>
    internal static bool LooksLikeIsOpenQuery(string lower)
    {
        ReadOnlySpan<string> isOpenPatterns =
        [
            "is it open",
            "are they open",
            "are you open",
            "is it closed",
            "are they closed"
        ];

        foreach (var pattern in isOpenPatterns)
        {
            if (lower.Contains(pattern, StringComparison.Ordinal))
                return true;
        }

        if (lower.StartsWith("is ", StringComparison.Ordinal) &&
            (lower.Contains(" open", StringComparison.Ordinal) ||
             lower.Contains(" closed", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Detects "when/what time does X open/close" phrasing.
    /// </summary>
    private static bool LooksLikeWhenOpenQuery(string lower)
    {
        var hasWhenLikeSignal =
            lower.Contains("when does", StringComparison.Ordinal) ||
            lower.Contains("when do they", StringComparison.Ordinal) ||
            lower.Contains("when is", StringComparison.Ordinal) ||
            lower.Contains("what time", StringComparison.Ordinal);

        if (!hasWhenLikeSignal) return false;

        return lower.Contains(" open", StringComparison.Ordinal) ||
               lower.Contains(" close", StringComparison.Ordinal);
    }

    /// <summary>
    /// Detects local business discovery requests that ask for currently
    /// open places or business options in a location.
    /// Example: "find me open restaurants in Rexburg right now".
    /// </summary>
    public static bool LooksLikeLocalBusinessDiscovery(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        if (lower.Contains("knowledge_store", StringComparison.Ordinal) ||
            lower.Contains("knowledge store", StringComparison.Ordinal) ||
            lower.Contains("journal_log_entry", StringComparison.Ordinal) ||
            lower.Contains("read_file", StringComparison.Ordinal) ||
            lower.Contains("file_list", StringComparison.Ordinal) ||
            lower.Contains("file_read", StringComparison.Ordinal) ||
            lower.Contains("tool call", StringComparison.Ordinal) ||
            lower.Contains("call ", StringComparison.Ordinal) && lower.Contains("tool", StringComparison.Ordinal))
        {
            return false;
        }

        // Guard against non-place "open" topics.
        if (lower.Contains("open source", StringComparison.Ordinal))
            return false;

        ReadOnlySpan<string> businessTerms =
        [
            "restaurant", "restaurants", "cafe", "coffee shop", "diner",
            "deli", "delis", "delicatessen", "delicatessens",
            "florist", "florists", "bakery", "bakeries",
            "bar", "pub", "store", "shop",
            "grocery", "groceries", "supermarket", "pharmacy", "pharmacies",
            "bank", "banks", "credit union",
            "park", "parks", "playground",
            "hotel", "motel",
            "gas station", "car wash", "laundromat", "salon", "barber",
            "gym", "dentist", "clinic", "doctor", "urgent care"
        ];

        if (!ContainsAny(lower, businessTerms))
            return false;

        var hasActionCue =
            lower.Contains("find", StringComparison.Ordinal) ||
            lower.Contains("show", StringComparison.Ordinal) ||
            lower.Contains("recommend", StringComparison.Ordinal) ||
            lower.Contains("best ", StringComparison.Ordinal) ||
            lower.Contains("good ", StringComparison.Ordinal) ||
            lower.Contains("open", StringComparison.Ordinal) ||
            lower.Contains("hours", StringComparison.Ordinal) ||
            lower.Contains("close", StringComparison.Ordinal) ||
            lower.Contains("tell", StringComparison.Ordinal) ||
            lower.Contains("where", StringComparison.Ordinal) ||
            lower.Contains("bring up", StringComparison.Ordinal) ||
            lower.Contains("look up", StringComparison.Ordinal) ||
            lower.Contains("search", StringComparison.Ordinal) ||
            lower.Contains("any ", StringComparison.Ordinal) ||
            lower.Contains("some ", StringComparison.Ordinal);

        // Hard proximity cues are unambiguous enough that business term +
        // proximity alone is sufficient — no action cue needed.
        // "florists nearby" is always a local business query.
        var hasHardProximityCue =
            lower.Contains("near me", StringComparison.Ordinal) ||
            lower.Contains("nearby", StringComparison.Ordinal) ||
            lower.Contains("around me", StringComparison.Ordinal) ||
            lower.Contains("around here", StringComparison.Ordinal) ||
            lower.Contains("close by", StringComparison.Ordinal) ||
            lower.Contains("in my area", StringComparison.Ordinal) ||
            lower.Contains("local", StringComparison.Ordinal);

        if (hasHardProximityCue)
            return true;

        if (!hasActionCue)
            return false;

        var hasLocalCue =
            hasHardProximityCue ||
            lower.Contains(" in ", StringComparison.Ordinal) ||
            lower.Contains("right now", StringComparison.Ordinal) ||
            lower.Contains("today", StringComparison.Ordinal) ||
            lower.Contains("tonight", StringComparison.Ordinal);

        return hasLocalCue;
    }

    /// <summary>
    /// Returns true when the message contains a local business type term
    /// combined with a proximity cue (near me, nearby, etc.). Used by the
    /// search pipeline to inject location context or return early guidance
    /// when no location hint is available.
    /// </summary>
    public static bool HasLocalBusinessProximitySignals(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        if (LooksLikeLocalBusinessDiscovery(lower))
            return true;

        ReadOnlySpan<string> businessTerms =
        [
            "restaurant", "restaurants", "cafe", "coffee shop", "diner",
            "deli", "delis", "delicatessen", "delicatessens",
            "florist", "florists", "bakery", "bakeries",
            "bar", "pub", "store", "shop",
            "grocery", "groceries", "supermarket", "pharmacy", "pharmacies",
            "bank", "banks", "credit union",
            "park", "parks", "playground",
            "hotel", "motel",
            "gas station", "car wash", "laundromat", "salon", "barber",
            "gym", "dentist", "clinic", "doctor", "urgent care",
            // Popular chains / brand names that imply local business lookup.
            "starbucks", "mcdonald", "mcdonalds", "mcdonald's",
            "walmart", "target", "costco", "trader joe",
            "walgreens", "cvs", "rite aid",
            "home depot", "lowe's", "lowes",
            "taco bell", "burger king", "wendy's", "wendys",
            "subway", "chick-fil-a", "chipotle", "domino's", "dominos",
            "dunkin", "panda express", "pizza hut", "papa john",
            "whole foods", "kroger", "safeway", "albertsons",
            "best buy", "gamestop", "petco", "petsmart",
            "ikea", "nordstrom", "marshalls", "tj maxx",
            "aldi", "sprouts", "fred meyer", "winco"
        ];

        if (!ContainsAny(lower, businessTerms))
            return false;

        return lower.Contains("near me", StringComparison.Ordinal) ||
               lower.Contains("nearby", StringComparison.Ordinal) ||
               lower.Contains("around me", StringComparison.Ordinal) ||
               lower.Contains("close by", StringComparison.Ordinal) ||
               lower.Contains("in my area", StringComparison.Ordinal) ||
               lower.Contains("around here", StringComparison.Ordinal) ||
               lower.Contains("closest", StringComparison.Ordinal) ||
               lower.Contains("nearest", StringComparison.Ordinal) ||
               lower.Contains("local", StringComparison.Ordinal);
    }

    public static bool LooksLikeProductRecommendationLookup(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        // Keep local-business recommendations ("best cafe near me") on the
        // local-business path rather than shopping/product retrieval.
        if (LooksLikeLocalBusinessDiscovery(lower))
            return false;

        if (LooksLikeExplicitNewsLookup(lower) || LooksLikeDeepDiveLookup(lower))
            return false;

        var hasRetailerAnchor =
            lower.Contains("amazon", StringComparison.Ordinal) ||
            lower.Contains("walmart", StringComparison.Ordinal) ||
            lower.Contains("ebay", StringComparison.Ordinal) ||
            lower.Contains("etsy", StringComparison.Ordinal);

        var hasRecommendationCue =
            lower.Contains("recommend", StringComparison.Ordinal) ||
            lower.Contains("recommendation", StringComparison.Ordinal) ||
            lower.Contains("best ", StringComparison.Ordinal) ||
            lower.Contains("top ", StringComparison.Ordinal) ||
            lower.Contains("good ", StringComparison.Ordinal) ||
            lower.Contains("which should i buy", StringComparison.Ordinal) ||
            lower.Contains("what should i buy", StringComparison.Ordinal) ||
            lower.Contains("worth buying", StringComparison.Ordinal);

        var hasComparisonCue =
            lower.Contains("compare", StringComparison.Ordinal) ||
            lower.Contains("versus", StringComparison.Ordinal) ||
            lower.Contains(" vs ", StringComparison.Ordinal) ||
            lower.Contains("review", StringComparison.Ordinal) ||
            lower.Contains("reviews", StringComparison.Ordinal);

        var hasProductObject =
            lower.Contains("product", StringComparison.Ordinal) ||
            lower.Contains("brand", StringComparison.Ordinal) ||
            lower.Contains("brands", StringComparison.Ordinal) ||
            lower.Contains("supplement", StringComparison.Ordinal) ||
            lower.Contains("vitamin", StringComparison.Ordinal) ||
            lower.Contains("capsule", StringComparison.Ordinal) ||
            lower.Contains("tablet", StringComparison.Ordinal) ||
            lower.Contains("powder", StringComparison.Ordinal) ||
            lower.Contains("gummy", StringComparison.Ordinal) ||
            lower.Contains("rating", StringComparison.Ordinal) ||
            lower.Contains("ratings", StringComparison.Ordinal) ||
            lower.Contains("price", StringComparison.Ordinal);

        if (hasRetailerAnchor && (hasRecommendationCue || hasComparisonCue || hasProductObject))
            return true;

        return hasRecommendationCue && hasProductObject;
    }

    public static bool LooksLikeFactLookup(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        if (LooksLikePreferenceOrOpinionPrompt(lower))
            return false;

        if (LooksLikeExplicitNewsLookup(lower))
            return false;

        if (LooksLikeReleasedProductExistenceLookup(lower))
            return true;

        if (LooksLikeIdentityLookup(lower))
            return true;

        if (LooksLikeWebSearchRequest(lower))
            return true;

        ReadOnlySpan<string> factPrefixes =
        [
            "what is ",
            "what's ",
            "whats ",
            "what are ",
            "who is ",
            "who's ",
            "whos ",
            "who was ",
            "when is ",
            "when was ",
            "when did ",
            "where is ",
            "where are ",
            "how many ",
            "how much ",
            "in what year ",
            "what year ",
            "define ",
            "meaning of "
        ];

        foreach (var prefix in factPrefixes)
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        if (lower.Contains("airspeed velocity of an unladen swallow", StringComparison.Ordinal))
            return true;

        return false;
    }

    public static bool LooksLikeReleasedProductExistenceLookup(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var hasExistenceCue =
            lower.StartsWith("does ", StringComparison.Ordinal) ||
            lower.StartsWith("did ", StringComparison.Ordinal) ||
            lower.Contains(" exist ", StringComparison.Ordinal) ||
            lower.EndsWith(" exist?", StringComparison.Ordinal) ||
            lower.Contains(" real ", StringComparison.Ordinal);

        if (!hasExistenceCue)
            return false;

        ReadOnlySpan<string> releaseSignals =
        [
            "released product",
            "released device",
            "released model",
            "officially released",
            "ever released",
            "shipping product",
            "real product",
            "real device"
        ];

        if (ContainsAny(lower, releaseSignals))
            return true;

        var hasReleaseVerb =
            lower.Contains("released", StringComparison.Ordinal) ||
            lower.Contains("launch", StringComparison.Ordinal) ||
            lower.Contains("shipped", StringComparison.Ordinal) ||
            lower.Contains("available", StringComparison.Ordinal);

        var hasArtifactNoun =
            lower.Contains("product", StringComparison.Ordinal) ||
            lower.Contains("device", StringComparison.Ordinal) ||
            lower.Contains("model", StringComparison.Ordinal) ||
            lower.Contains("phone", StringComparison.Ordinal);

        return hasReleaseVerb && hasArtifactNoun;
    }

    public static bool LooksLikeLogicPuzzlePrompt(string lower)
        => LogicPuzzleDetector.IsLogicPuzzle(lower);

    public static bool LooksLikeIdentityLookup(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        if (LooksLikeLogicPuzzlePrompt(lower))
            return false;

        if (lower.Contains("what's up", StringComparison.Ordinal) ||
            lower.Contains("whats up", StringComparison.Ordinal))
        {
            return false;
        }

        if (lower.Contains("who are you", StringComparison.Ordinal) ||
            lower.Contains("what are you", StringComparison.Ordinal) ||
            lower.Contains("who am i", StringComparison.Ordinal) ||
            lower.Contains("what is my name", StringComparison.Ordinal) ||
            lower.Contains("what's my name", StringComparison.Ordinal) ||
            lower.Contains("whats my name", StringComparison.Ordinal) ||
            lower.Contains("what is your name", StringComparison.Ordinal) ||
            lower.Contains("what's your name", StringComparison.Ordinal) ||
            lower.Contains("whats your name", StringComparison.Ordinal))
        {
            return false;
        }

        return lower.Contains("who is ", StringComparison.Ordinal) ||
               lower.Contains("who's ", StringComparison.Ordinal) ||
               lower.Contains("whos ", StringComparison.Ordinal) ||
               lower.Contains("who was ", StringComparison.Ordinal) ||
               lower.Contains("who the heck is", StringComparison.Ordinal) ||
               lower.Contains("who the hell is", StringComparison.Ordinal) ||
               lower.Contains("define ", StringComparison.Ordinal) ||
               lower.Contains("meaning of ", StringComparison.Ordinal) ||
               lower.Contains("what does ", StringComparison.Ordinal);
    }

    public static bool LooksLikePreferenceOrOpinionPrompt(string lower)
    {
        return lower.Contains("what is your favorite", StringComparison.Ordinal) ||
               lower.Contains("what's your favorite", StringComparison.Ordinal) ||
               lower.Contains("whats your favorite", StringComparison.Ordinal) ||
               lower.Contains("tell me about your favorite", StringComparison.Ordinal) ||
               lower.Contains("about your favorite", StringComparison.Ordinal) ||
               lower.Contains("favorite thing", StringComparison.Ordinal) ||
               lower.Contains("what do you think", StringComparison.Ordinal) ||
               lower.Contains("what's your opinion", StringComparison.Ordinal) ||
               lower.Contains("whats your opinion", StringComparison.Ordinal) ||
               lower.Contains("what's your take", StringComparison.Ordinal) ||
               lower.Contains("whats your take", StringComparison.Ordinal) ||
               lower.Contains("tell me about yourself", StringComparison.Ordinal) ||
               lower.Contains("what makes you good at", StringComparison.Ordinal) ||
               lower.Contains("should i ", StringComparison.Ordinal);
    }

    public static bool LooksLikeSelfContainedReasoningPrompt(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        if (LooksLikePreferenceOrOpinionPrompt(lower) ||
            LooksLikeMemoryWriteRequest(lower) ||
            LooksLikeScreenRequest(lower) ||
            LooksLikeFileRequest(lower) ||
            LooksLikeSystemCommand(lower) ||
            LooksLikeBrowseRequest(lower) ||
            LooksLikeDeepDiveLookup(lower) ||
            LooksLikeExplicitNewsLookup(lower) ||
            LooksLikeLocalBusinessDiscovery(lower) ||
            LooksLikeIdentityLookup(lower) ||
            lower.Contains("search ", StringComparison.Ordinal) ||
            lower.Contains("look up", StringComparison.Ordinal) ||
            lower.Contains("latest", StringComparison.Ordinal) ||
            lower.Contains("recent", StringComparison.Ordinal) ||
            lower.Contains("news", StringComparison.Ordinal) ||
            lower.Contains("weather", StringComparison.Ordinal) ||
            lower.Contains(".com", StringComparison.Ordinal))
        {
            return false;
        }

        var hasNameDeclaration =
            lower.Contains("my name is ", StringComparison.Ordinal) ||
            lower.Contains("i am ", StringComparison.Ordinal);

        if (!hasNameDeclaration)
            return false;

        var asksToRecallName =
            lower.Contains("what my name is", StringComparison.Ordinal) ||
            lower.Contains("what's my name", StringComparison.Ordinal) ||
            lower.Contains("whats my name", StringComparison.Ordinal) ||
            lower.Contains("tell me what my name is", StringComparison.Ordinal) ||
            lower.Contains("tell me my name", StringComparison.Ordinal);

        if (!asksToRecallName)
            return false;

        var hasSimpleArithmetic =
            System.Text.RegularExpressions.Regex.IsMatch(
                lower,
                @"\bwhat is\s+\d+\s*[\+\-\*/x]\s*\d+\b",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            lower.Contains(" plus ", StringComparison.Ordinal) ||
            lower.Contains(" minus ", StringComparison.Ordinal) ||
            lower.Contains(" times ", StringComparison.Ordinal) ||
            lower.Contains(" divided by ", StringComparison.Ordinal);

        return hasSimpleArithmetic;
    }

    /// <summary>
    /// Detects queries that combine temporal freshness signals (e.g. "latest",
    /// "recent") with update/information keywords (e.g. "updates", "changes",
    /// "developments"). This compound pattern strongly indicates the user
    /// wants current data from the web, even when no domain topic keyword
    /// (news, weather, stock) is present.
    /// </summary>
    private static bool HasTemporalFreshnessWithUpdateCue(string lower)
    {
        var hasFreshness =
            lower.Contains("latest", StringComparison.Ordinal) ||
            lower.Contains("recent", StringComparison.Ordinal) ||
            lower.Contains("current", StringComparison.Ordinal) ||
            lower.Contains("this year", StringComparison.Ordinal) ||
            lower.Contains("past year", StringComparison.Ordinal) ||
            lower.Contains("last year", StringComparison.Ordinal) ||
            lower.Contains("this month", StringComparison.Ordinal) ||
            lower.Contains("right now", StringComparison.Ordinal);

        if (!hasFreshness)
            return false;

        return lower.Contains("updates", StringComparison.Ordinal) ||
               lower.Contains("update", StringComparison.Ordinal) ||
               lower.Contains("developments", StringComparison.Ordinal) ||
               lower.Contains("changes", StringComparison.Ordinal) ||
               lower.Contains("releases", StringComparison.Ordinal) ||
               lower.Contains("announcements", StringComparison.Ordinal) ||
               lower.Contains("version", StringComparison.Ordinal) ||
               lower.Contains("release", StringComparison.Ordinal);
    }

    /// <summary>
    /// Detects episodic TV lookup prompts that should be grounded with web
    /// evidence rather than answered from model priors (e.g. canceled season
    /// questions asking for a plot/synopsis).
    /// </summary>
    private static bool LooksLikeSeasonEpisodePlotLookup(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var hasSeasonToken = lower.Contains("season ", StringComparison.Ordinal) ||
                             lower.Contains("s1", StringComparison.Ordinal) ||
                             lower.Contains("s2", StringComparison.Ordinal) ||
                             lower.Contains("s3", StringComparison.Ordinal) ||
                             lower.Contains("s4", StringComparison.Ordinal) ||
                             lower.Contains("s5", StringComparison.Ordinal);

        var hasEpisodeToken = lower.Contains("episode ", StringComparison.Ordinal) ||
                              lower.Contains("ep ", StringComparison.Ordinal) ||
                              lower.Contains("e1", StringComparison.Ordinal) ||
                              lower.Contains("e2", StringComparison.Ordinal) ||
                              lower.Contains("e3", StringComparison.Ordinal) ||
                              lower.Contains("e4", StringComparison.Ordinal) ||
                              lower.Contains("e5", StringComparison.Ordinal);

        if (!hasSeasonToken || !hasEpisodeToken)
            return false;

        var asksForEpisodeContent =
            lower.Contains("plot", StringComparison.Ordinal) ||
            lower.Contains("synopsis", StringComparison.Ordinal) ||
            lower.Contains("what happens", StringComparison.Ordinal) ||
            lower.StartsWith("what would be", StringComparison.Ordinal) ||
            lower.StartsWith("what is", StringComparison.Ordinal) ||
            lower.StartsWith("what's", StringComparison.Ordinal) ||
            lower.StartsWith("whats", StringComparison.Ordinal);

        if (!asksForEpisodeContent)
            return false;

        // Creative writing prompts should remain chat-only.
        var asksForCreativeWriting =
            lower.Contains("write", StringComparison.Ordinal) ||
            lower.Contains("fanfic", StringComparison.Ordinal) ||
            lower.Contains("fan fiction", StringComparison.Ordinal) ||
            lower.Contains("invent", StringComparison.Ordinal) ||
            lower.Contains("make up", StringComparison.Ordinal);

        return !asksForCreativeWriting;
    }

    private static bool ContainsAny(string lower, ReadOnlySpan<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool ContainsLoosePhrase(string lower, ReadOnlySpan<string> phrases)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var normalized = NormalizeLoosePhraseInput(lower);
        var compact = NormalizeCompactPhraseInput(lower);

        foreach (var phrase in phrases)
        {
            if (lower.Contains(phrase, StringComparison.Ordinal) ||
                normalized.Contains(phrase, StringComparison.Ordinal))
            {
                return true;
            }

            var compactPhrase = NormalizeCompactPhraseInput(phrase);
            if (compactPhrase.Length > 0 && compact.Contains(compactPhrase, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string NormalizeLoosePhraseInput(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var buffer = new System.Text.StringBuilder(value.Length);
        var lastWasSpace = true;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c) || c is '\'' or '-')
            {
                buffer.Append(c);
                lastWasSpace = false;
                continue;
            }

            if (lastWasSpace)
                continue;

            buffer.Append(' ');
            lastWasSpace = true;
        }

        return buffer.ToString().Trim();
    }

    private static string NormalizeCompactPhraseInput(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var buffer = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                buffer.Append(c);
        }

        return buffer.ToString();
    }
}
