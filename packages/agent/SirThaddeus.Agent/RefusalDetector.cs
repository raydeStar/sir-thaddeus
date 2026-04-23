namespace SirThaddeus.Agent;

/// <summary>
/// Detects refusal / uncertainty shapes in an assistant draft. Used by
/// the pipeline's <c>SearchFallbackStep</c> (and by the UI runtime's
/// equivalent) so both sides make the same "should we retry via search?"
/// decision.
///
/// <para>The marker list is empirical — each phrase represents a real
/// draft the model produced that looked coherent but failed to deliver
/// the datum the user asked for. Extend deliberately; each new marker
/// loosens the net so be sure it's a genuine refusal shape and not a
/// natural phrase that appears in useful answers.</para>
/// </summary>
public static class RefusalDetector
{
    private static readonly string[] RefusalMarkers =
    [
        "i don't know",
        "i dont know",
        "i'm not sure",
        "im not sure",
        "not sure",
        "i can't",
        "i cant",
        "i cannot",
        "unable to",
        "can't answer",
        "cannot answer",
        "don't have enough information",
        "do not have enough information",
        "not enough information",
        "i couldn't find",
        "i could not find",
        "i wasn't able to",
        "i was not able to",
        // Observed in real refusal drafts after web_search returned
        // results but the model couldn't pull the specific datum the
        // user asked for (e.g. weather returned timestamps instead of
        // temperature). Kept in sync with the markers above.
        "i couldn't retrieve",
        "i could not retrieve",
        "couldn't retrieve",
        "could not retrieve",
        "failed to retrieve",
        "unable to retrieve",
        "only provided",
        "didn't return",
        "did not return",
        "no direct answer",
        "no clear answer",
        "try searching",
        "i'll try to find",
        "ill try to find",
        "check back in a moment",
    ];

    /// <summary>
    /// Returns true when <paramref name="processedDraft"/> looks like a
    /// refusal or uncertainty response (or is blank). Also returns true
    /// when the raw draft is blank — a silent model is treated the same
    /// as an explicit "I don't know".
    /// </summary>
    public static bool HasRefusalOrUncertaintySignals(string rawDraft, string processedDraft)
    {
        if (string.IsNullOrWhiteSpace(processedDraft))
            return true;

        var lower = processedDraft.Trim().ToLowerInvariant();
        foreach (var marker in RefusalMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        if (string.IsNullOrWhiteSpace(rawDraft))
            return true;

        return false;
    }
}
