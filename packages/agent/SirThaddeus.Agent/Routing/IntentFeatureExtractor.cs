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
            "see what im working on"
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
            if (lower.Contains(phrase, StringComparison.Ordinal))
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
            if (lower.Contains(phrase, StringComparison.Ordinal))
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
            if (lower.Contains(phrase, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static bool LooksLikeWebSearchRequest(string lower)
    {
        if (LooksLikeLogicPuzzlePrompt(lower))
            return false;

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
            "how much is",  "how much does"
        ];

        foreach (var phrase in phrases)
        {
            if (lower.Contains(phrase, StringComparison.Ordinal))
                return true;
        }

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
            lower.Contains("forex", StringComparison.Ordinal);

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

    public static bool LooksLikeLogicPuzzlePrompt(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var normalized = lower.Replace('’', '\'');
        var hasPhotoCue =
            normalized.Contains("who is in the photograph", StringComparison.Ordinal) ||
            normalized.Contains("who is in the photo", StringComparison.Ordinal) ||
            normalized.Contains("who is in the picture", StringComparison.Ordinal) ||
            normalized.Contains("who's in the photograph", StringComparison.Ordinal) ||
            normalized.Contains("who's in the photo", StringComparison.Ordinal) ||
            normalized.Contains("who's in the picture", StringComparison.Ordinal) ||
            normalized.Contains("whos in the photograph", StringComparison.Ordinal) ||
            normalized.Contains("whos in the photo", StringComparison.Ordinal) ||
            normalized.Contains("whos in the picture", StringComparison.Ordinal) ||
            normalized.Contains("looking at a photograph", StringComparison.Ordinal) ||
            normalized.Contains("pointing to a photograph", StringComparison.Ordinal);
        var hasOnlyChildCue =
            normalized.Contains("brothers and sisters, i have none", StringComparison.Ordinal) ||
            normalized.Contains("brothers and sisters i have none", StringComparison.Ordinal) ||
            normalized.Contains("i have no siblings", StringComparison.Ordinal) ||
            normalized.Contains("i don't have siblings", StringComparison.Ordinal) ||
            normalized.Contains("i do not have siblings", StringComparison.Ordinal) ||
            normalized.Contains("i am an only child", StringComparison.Ordinal) ||
            normalized.Contains("i'm an only child", StringComparison.Ordinal);

        var hasFamilyEquation =
            (normalized.Contains("that man's", StringComparison.Ordinal) ||
             normalized.Contains("that woman's", StringComparison.Ordinal) ||
             normalized.Contains("that person's", StringComparison.Ordinal) ||
             normalized.Contains("that boy's", StringComparison.Ordinal) ||
             normalized.Contains("that girl's", StringComparison.Ordinal)) &&
            normalized.Contains(" is my ", StringComparison.Ordinal) &&
            (normalized.Contains(" father's ", StringComparison.Ordinal) ||
             normalized.Contains(" mother's ", StringComparison.Ordinal)) &&
            (normalized.Contains(" son", StringComparison.Ordinal) ||
             normalized.Contains(" daughter", StringComparison.Ordinal));

        return hasPhotoCue && hasOnlyChildCue && hasFamilyEquation;
    }

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
}

