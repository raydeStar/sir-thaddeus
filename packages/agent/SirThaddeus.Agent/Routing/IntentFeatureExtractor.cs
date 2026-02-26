namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Concentrates intent-oriented string heuristics outside the orchestrator.
/// </summary>
public static class IntentFeatureExtractor
{
    public static bool LooksLikeScreenRequest(string lower)
    {
        ReadOnlySpan<string> patterns =
        [
            "what's on my screen",   "whats on my screen",
            "what can you see",      "what do you see",
            "look at my screen",     "look at the screen",
            "take a screenshot",     "screenshot",
            "capture the screen",    "capture my screen",
            "screen capture",        "what's happening on screen",
            "show me my screen",     "read my screen",
            "what's on the screen",  "whats on the screen",
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

        foreach (var p in patterns)
        {
            if (lower.Contains(p, StringComparison.Ordinal))
                return true;
        }

        return false;
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
            "list directory",  "ls "
        ];

        foreach (var p in patterns)
        {
            if (lower.Contains(p, StringComparison.Ordinal))
                return true;
        }

        return false;
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
    {
        if (LooksLikeLogicPuzzlePrompt(lower))
            return false;

        if (lower.Contains("web_search", StringComparison.Ordinal) ||
            lower.Contains("web search", StringComparison.Ordinal))
        {
            return true;
        }

        if (LooksLikeIdentityLookup(lower))
            return true;

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

            // Conversational search phrasing the LLM classifier often
            // misclassifies as casual chat. Excludes "tell me about" and
            // bare "tell me how/what" which are too broad (catch opinions).
            "can you tell me", "tell me if",     "tell me whether"
        ];

        foreach (var phrase in phrases)
        {
            if (lower.Contains(phrase, StringComparison.Ordinal))
                return true;
        }

        // Temporal freshness + update/info keywords together strongly
        // signal a need for live web data regardless of domain topic.
        if (HasTemporalFreshnessWithUpdateCue(lower))
            return true;

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

        if (!hasTopic)
            return false;

        if (lower.Contains('?', StringComparison.Ordinal))
            return true;

        if (lower.Contains("can you", StringComparison.Ordinal) ||
            lower.Contains("could you", StringComparison.Ordinal) ||
            lower.Contains("would you", StringComparison.Ordinal) ||
            lower.Contains("will you", StringComparison.Ordinal) ||
            lower.Contains("please", StringComparison.Ordinal))
        {
            return true;
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
            return true;
        }

        if (lower.Contains("what", StringComparison.Ordinal) ||
            lower.Contains("how", StringComparison.Ordinal) ||
            lower.Contains("where", StringComparison.Ordinal) ||
            lower.Contains("when", StringComparison.Ordinal) ||
            lower.Contains("who", StringComparison.Ordinal) ||
            lower.Contains("why", StringComparison.Ordinal))
        {
            return true;
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
            return true;
        }

        return false;
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

        // Guard against non-place "open" topics.
        if (lower.Contains("open source", StringComparison.Ordinal))
            return false;

        ReadOnlySpan<string> businessTerms =
        [
            "restaurant", "restaurants", "cafe", "coffee shop", "diner",
            "florist", "florists", "bakery", "bakeries",
            "bar", "pub", "store", "shop",
            "grocery", "groceries", "supermarket", "pharmacy", "pharmacies",
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

        ReadOnlySpan<string> businessTerms =
        [
            "restaurant", "restaurants", "cafe", "coffee shop", "diner",
            "florist", "florists", "bakery", "bakeries",
            "bar", "pub", "store", "shop",
            "grocery", "groceries", "supermarket", "pharmacy", "pharmacies",
            "hotel", "motel",
            "gas station", "car wash", "laundromat", "salon", "barber",
            "gym", "dentist", "clinic", "doctor", "urgent care"
        ];

        if (!ContainsAny(lower, businessTerms))
            return false;

        return lower.Contains("near me", StringComparison.Ordinal) ||
               lower.Contains("nearby", StringComparison.Ordinal) ||
               lower.Contains("around me", StringComparison.Ordinal) ||
               lower.Contains("close by", StringComparison.Ordinal) ||
               lower.Contains("in my area", StringComparison.Ordinal) ||
               lower.Contains("around here", StringComparison.Ordinal) ||
               lower.Contains("local", StringComparison.Ordinal);
    }

    public static bool LooksLikeFactLookup(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        if (LooksLikeExplicitNewsLookup(lower))
            return false;

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
               lower.Contains("what is ", StringComparison.Ordinal) ||
               lower.Contains("what's ", StringComparison.Ordinal) ||
               lower.Contains("whats ", StringComparison.Ordinal) ||
               lower.Contains("define ", StringComparison.Ordinal) ||
               lower.Contains("meaning of ", StringComparison.Ordinal) ||
               lower.Contains("what does ", StringComparison.Ordinal);
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

    private static bool ContainsAny(string lower, ReadOnlySpan<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
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
}
