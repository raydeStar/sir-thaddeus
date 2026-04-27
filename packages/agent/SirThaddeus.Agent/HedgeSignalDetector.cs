using System.Text.RegularExpressions;

namespace SirThaddeus.Agent;

/// <summary>
/// Detects when an assistant draft signals its own lack of confidence on
/// a factual question — the classic "I believe ... as of my knowledge
/// cutoff ... if I recall correctly" pattern. These are the tells a model
/// emits when it's guessing from stale training data but still produces
/// a coherent-looking answer.
///
/// <para>Paired with <see cref="RefusalDetector"/>, this catches the
/// confidence gap that refusal markers miss: refusal = "I can't answer",
/// hedge = "I'll answer but I'm guessing". The user wants the latter
/// verified, not the former ignored.</para>
///
/// <para>Firing requires BOTH a hedge marker in the draft AND the user's
/// original question to look factual. A hedge in a creative / opinion
/// response ("I think this poem captures the feeling") is fine and must
/// not be rewritten. A hedge in an existence / recency / pricing answer
/// is a cue to verify via <c>web_search</c>.</para>
/// </summary>
public static class HedgeSignalDetector
{
    // Lowercase substring markers. Each one represents a real observed
    // hedge shape — words or phrases a model outputs when it's answering
    // from memory instead of grounded data. "I think" on its own is too
    // broad (it appears in legit opinion), so we require BOTH the hedge
    // AND a factual-question user prompt to fire the verifier.
    private static readonly string[] HedgeMarkers =
    [
        "as of my knowledge cutoff",
        "as of my last update",
        "as of my training",
        "my training data",
        "my training cutoff",
        "i don't have access to real-time",
        "i do not have access to real-time",
        "don't have real-time",
        "do not have real-time",
        "i don't have live",
        "i cannot verify",
        "i can't verify",
        "as far as i know",
        "as far as i am aware",
        "as far as i'm aware",
        "to my knowledge",
        "to the best of my knowledge",
        "if i recall correctly",
        "if i remember correctly",
        "if memory serves",
        "i'm not entirely certain",
        "im not entirely certain",
        "i'm not entirely sure",
        "im not entirely sure",
        "i'm not 100%",
        "im not 100%",
        "based on my information",
        "was allegedly",
        "was reportedly",
        "is allegedly",
        "is reportedly",
        "may have been released",
        "might have been released",
        "i believe",
        "i think",
        // "My internal archives/records/knowledge" — Thaddeus voice for
        // "my training data". Local models re-phrase "knowledge cutoff"
        // in dozens of ways; keep adding observed variants.
        "my internal archives",
        "my internal records",
        "my internal knowledge",
        "my knowledge base",
        "internal archives contain",
        "internal records contain",
        "internal knowledge contain",
        // Deferral shapes: the model noticed it should search but asked
        // the user for permission instead of acting. From the user's
        // perspective this is the same as an unverified answer — force
        // the search so the next draft is grounded.
        "shall i perform",
        "shall i look",
        "shall i search",
        "shall i check",
        "shall i proceed",
        "should i look up",
        "should i search",
        "should i proceed",
        "would you like me to search",
        "would you like me to look",
        "would you like me to check",
        "would you like me to verify",
        "would you like me to perform",
        "do you want me to search",
        "do you want me to check",
        "do you want me to look",
        "do you want me to proceed",
        "awaiting your confirmation",
        "let me know if you'd like",
        "let me know if you want",
        "i can perform a web search",
        "i could perform a web search",
        "i'll perform a search",
        "i will perform a search",
        "i will need to perform a search",
        "i need to perform a search",
        "i shall use the web_search",
        "i shall use the `web_search",
        "let me search",
        "i'll search",
        "i will search",
    ];

    // User prompts that plausibly invite a factual answer. Only when the
    // user's question has this shape should a hedge in the draft trigger
    // verification — otherwise we'd re-search on every "I think that
    // sounds fun" response.
    //
    // Pattern mirrors FreshnessRouterStep's question shapes but wider
    // (covers "what is", "when did", "how old", etc.) because hedge
    // detection is post-draft: we've already produced an answer and now
    // decide whether to trust it.
    private static readonly Regex FactualQuestionShape = new(
        @"^\s*(?:hey[,!\s]+|hi[,!\s]+|so[,!\s]+|please[,!\s]+|could\s+you[,!\s]+|just\s+wondering[,\s]+)*" +
        @"(?:what(?:'s|\s+is|\s+are|\s+was|\s+were)|" +
        @"who(?:'s|\s+is|\s+are|\s+was|\s+were)|" +
        @"when\s+(?:did|does|was|will|is)|" +
        @"where\s+(?:is|was|are)|" +
        @"how\s+(?:much|many|old|long|tall|far|fast|heavy|often|recent)|" +
        @"which\s+(?:is|was|are|were)|" +
        @"does\s+\S+(?:\s+\S+){0,4}\s+exist|" +
        @"is\s+there\s+an?\s+\S+|" +
        @"has\s+\S+(?:\s+\S+){0,6}\s+(?:been|come\s+out|shipped)|" +
        @"did\s+\S+(?:\s+\S+){0,6}\s+(?:release|ship|launch|win|happen)|" +
        // Broadened: "tell me if", "tell me whether", plain "tell me"
        // without a restrictive follower. "Tell me a joke" won't hedge
        // in practice (no hedge markers in a joke answer), so this is
        // safe to widen.
        @"(?:can\s+you\s+)?tell\s+me\b|" +
        // Recommendation / discovery shapes: "recommend a good X",
        // "find me a Y", "suggest a Z". These invite factual, current
        // answers ("which X exists?", "which Y is good?") so a hedged
        // or deferred draft deserves the same search-grounded retry
        // as an explicit "what is" question.
        @"(?:can\s+you\s+|could\s+you\s+)?(?:recommend|suggest|find\s+me|show\s+me|help\s+me\s+find|point\s+me\s+to)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns true when a verification pass is warranted: the draft hedges
    /// its own confidence AND the original prompt looks factual. Designed
    /// to <i>compose</i> with <see cref="RefusalDetector"/> — callers
    /// typically OR the two signals.
    /// </summary>
    /// <param name="draft">The assistant draft (post-sanitize).</param>
    /// <param name="userPrompt">The user's original message this turn.</param>
    public static bool ShouldVerify(string? draft, string? userPrompt)
    {
        if (string.IsNullOrWhiteSpace(draft) || string.IsNullOrWhiteSpace(userPrompt))
            return false;

        if (!FactualQuestionShape.IsMatch(userPrompt))
            return false;

        var lower = draft.ToLowerInvariant();
        foreach (var marker in HedgeMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
