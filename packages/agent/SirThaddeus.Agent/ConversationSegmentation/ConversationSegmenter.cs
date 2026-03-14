using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.ConversationSegmentation;

/// <summary>
/// Deterministic first-pass conversational segmenter.
/// Keeps the taxonomy intentionally small:
/// actionable vs non-actionable.
/// </summary>
public sealed class ConversationSegmenter : IConversationSegmenter
{
    // Sentence-ish chunks with punctuation/newline boundaries preserved.
    private static readonly Regex ChunkRegex = new(
        @"[^.!?\n]+(?:[.!?]+|$)",
        RegexOptions.Compiled);

    private static readonly Regex ClauseConnectorRegex = new(
        @"\b(?:and|also|then)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ActionableTriggerRegex = new(
        @"\b(?:weather|forecast|news|headline|time|distance|how\s+far|search|look\s+up|find|show|open|summarize|calculate|convert|who\s+is|what\s+is|what's|how\s+many|where\s+is|when\s+is|can\s+you|could\s+you|please|check\s+(?:if|on|for))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GreetingOnlyRegex = new(
        @"^\s*(?:hey|hi|hello|yo|good\s+(?:morning|afternoon|evening)|what'?s\s+up|how\s+are\s+you)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ContradictionRegex = new(
        @"\bdon'?t\b.{0,60}\bactually\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex BoundaryMarkerRegex = new(
        @"[.!?\n]|(?:\b(?:anyway|btw|by the way|also|then)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ConversationSegmentationResult Segment(string userMessage)
    {
        var text = userMessage ?? "";
        var spans = SplitIntoSpans(text);
        var segments = BuildSegments(text, spans);
        var actionableCount = segments.Count(s => s.IsActionable);

        var (highConfidence, reason) = EvaluateConfidence(text, segments, actionableCount);

        return new ConversationSegmentationResult
        {
            OriginalMessage = text,
            Segments = segments,
            HasActionable = actionableCount > 0,
            HighConfidence = highConfidence,
            ConfidenceReason = reason
        };
    }

    private static IReadOnlyList<(int Start, int End)> SplitIntoSpans(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var spans = new List<(int Start, int End)>();
        var matches = ChunkRegex.Matches(text);
        foreach (Match match in matches)
        {
            if (!match.Success || match.Length == 0)
                continue;

            var start = match.Index;
            var end = match.Index + match.Length;

            // Trim leading/trailing whitespace without changing offsets.
            while (start < end && char.IsWhiteSpace(text[start]))
                start++;
            while (end > start && char.IsWhiteSpace(text[end - 1]))
                end--;

            if (end - start < 2)
                continue;

            spans.AddRange(SplitCompoundSpanIfNeeded(text, start, end));
        }

        if (spans.Count > 0)
            return MergeSpansSplitOnDottedTokens(text, spans);

        // Fallback: whole message as one span.
        var fallbackEnd = text.Length;
        var fallbackStart = 0;
        while (fallbackStart < fallbackEnd && char.IsWhiteSpace(text[fallbackStart]))
            fallbackStart++;
        while (fallbackEnd > fallbackStart && char.IsWhiteSpace(text[fallbackEnd - 1]))
            fallbackEnd--;

        return fallbackEnd > fallbackStart
            ? [(fallbackStart, fallbackEnd)]
            : [];
    }

    /// <summary>
    /// Recombines spans that were split on a period that is part of a dotted
    /// token (e.g. ".NET", "U.S.", "Dr.") rather than a real sentence boundary.
    /// A split point is considered a dotted-token artifact when the text
    /// immediately after the period starts with a letter (no whitespace gap).
    /// </summary>
    private static IReadOnlyList<(int Start, int End)> MergeSpansSplitOnDottedTokens(
        string text,
        List<(int Start, int End)> spans)
    {
        if (spans.Count < 2)
            return spans;

        var merged = new List<(int Start, int End)> { spans[0] };
        for (var i = 1; i < spans.Count; i++)
        {
            var prev = merged[^1];

            // Look at the gap between the previous span's raw end (before
            // whitespace trim) and the current span's raw start.  If the
            // gap is exactly "." optionally followed by nothing before the
            // next letter, the two chunks were part of one dotted token.
            var gapStart = prev.End;
            var gapEnd   = spans[i].Start;

            // Walk backwards from the current span start to find the dot.
            // The regex consumes the "." as part of group [.!?]+, so the
            // gap in the original text should contain the separator.
            var betweenStart = gapStart;
            while (betweenStart < gapEnd && char.IsWhiteSpace(text[betweenStart]))
                betweenStart++;

            var isDottedToken = false;
            if (betweenStart < text.Length && text[betweenStart] == '.')
            {
                // Check if the character directly after the dot is a letter
                // (no whitespace gap → ".NET", not ". Next sentence").
                var afterDot = betweenStart + 1;
                if (afterDot < text.Length && char.IsLetter(text[afterDot]))
                    isDottedToken = true;
            }
            // Also check the end of the raw regex match: the dot may have
            // been consumed inside the preceding match. Look at text just
            // before the current span.
            if (!isDottedToken && gapEnd > 0)
            {
                var beforeCurrent = gapEnd - 1;
                while (beforeCurrent > gapStart && char.IsWhiteSpace(text[beforeCurrent]))
                    beforeCurrent--;
                if (beforeCurrent >= 0 && text[beforeCurrent] == '.' &&
                    gapEnd < text.Length && char.IsLetter(text[gapEnd]))
                    isDottedToken = true;
            }

            if (isDottedToken)
            {
                // Merge: extend the previous span to cover the current one.
                merged[^1] = (prev.Start, spans[i].End);
            }
            else
            {
                merged.Add(spans[i]);
            }
        }

        return merged;
    }

    private static IReadOnlyList<ConversationSegment> BuildSegments(
        string originalMessage,
        IReadOnlyList<(int Start, int End)> spans)
    {
        var output = new List<ConversationSegment>(spans.Count);
        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            var text = originalMessage[span.Start..span.End].Trim();
            if (text.Length < 2)
                continue;

            var isActionable = LooksActionable(text);
            var confidence = isActionable ? 0.92 : 0.86;

            output.Add(new ConversationSegment
            {
                SegmentId = $"seg-{i + 1:0000}",
                Text = text,
                Order = i,
                StartIndex = span.Start,
                EndIndex = span.End,
                IsActionable = isActionable,
                Confidence = confidence
            });
        }

        return output;
    }

    private static bool LooksActionable(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasTrigger = ActionableTriggerRegex.IsMatch(text);
        var hasQuestion = text.Contains('?', StringComparison.Ordinal);

        // Greeting-only suppression: bail only when nothing actionable is present.
        // "hey whats weather like" starts with a greeting but contains a real trigger.
        if (GreetingOnlyRegex.IsMatch(text) && text.Trim().Length < 50 && !hasTrigger)
            return false;

        return hasTrigger || hasQuestion;
    }

    private static IReadOnlyList<(int Start, int End)> SplitCompoundSpanIfNeeded(
        string originalText,
        int spanStart,
        int spanEnd)
    {
        var chunk = originalText[spanStart..spanEnd];
        if (CountActionableSignals(chunk) < 2)
            return [(spanStart, spanEnd)];

        var localSplits = new List<(int Index, int Length)>();
        foreach (Match connector in ClauseConnectorRegex.Matches(chunk))
        {
            if (!connector.Success)
                continue;

            var left = chunk[..connector.Index].Trim();
            var right = chunk[(connector.Index + connector.Length)..].Trim();
            if (left.Length < 2 || right.Length < 2)
                continue;

            if (LooksActionable(left) && CountActionableSignals(right) >= 1)
                localSplits.Add((connector.Index, connector.Length));
        }

        if (localSplits.Count == 0)
            return [(spanStart, spanEnd)];

        var spans = new List<(int Start, int End)>();
        var localStart = 0;
        foreach (var split in localSplits)
        {
            var candidateStart = spanStart + localStart;
            var candidateEnd = spanStart + split.Index;
            TrimCandidate(originalText, ref candidateStart, ref candidateEnd);
            if (candidateEnd - candidateStart >= 2)
                spans.Add((candidateStart, candidateEnd));

            localStart = split.Index + split.Length; // Keep text after connector.
        }

        var finalStart = spanStart + localStart;
        var finalEnd = spanEnd;
        TrimCandidate(originalText, ref finalStart, ref finalEnd);
        if (finalEnd - finalStart >= 2)
            spans.Add((finalStart, finalEnd));

        return spans.Count > 0 ? spans : [(spanStart, spanEnd)];
    }

    private static void TrimCandidate(string originalText, ref int start, ref int end)
    {
        while (start < end && char.IsWhiteSpace(originalText[start]))
            start++;
        while (end > start && char.IsWhiteSpace(originalText[end - 1]))
            end--;
    }

    private static (bool HighConfidence, string Reason) EvaluateConfidence(
        string text,
        IReadOnlyList<ConversationSegment> segments,
        int actionableCount)
    {
        if (actionableCount == 0)
            return (true, "no_actionable");

        var hasBoundaryHints = BoundaryMarkerRegex.IsMatch(text);
        var hasContradiction = ContradictionRegex.IsMatch(text);
        var longUnpunctuated =
            segments.Count == 1 &&
            text.Length > 120 &&
            !hasBoundaryHints;
        // Only flag as compound when multiple signals sit across a clause
        // connector ("and", "also", "then").  Overlapping triggers inside a
        // single query — "what's the weather" — are one intent, not two.
        var compoundActionableClause =
            segments.Count == 1 &&
            CountActionableSignals(segments[0].Text) >= 2 &&
            ClauseConnectorRegex.IsMatch(segments[0].Text);

        var actionableHaveTriggers = segments
            .Where(s => s.IsActionable)
            .All(s => ActionableTriggerRegex.IsMatch(s.Text) || s.Text.Contains('?', StringComparison.Ordinal));

        if (!actionableHaveTriggers)
            return (false, "missing_actionable_trigger");
        if (hasContradiction)
            return (false, "contradiction_detected");
        if (compoundActionableClause)
            return (false, "compound_actionable_clause");
        if (longUnpunctuated)
            return (false, "long_unpunctuated");
        if (!hasBoundaryHints && segments.Count > 1)
            return (false, "unclear_boundaries");

        return (true, "deterministic_clear");
    }

    private static int CountActionableSignals(string text)
        => ActionableTriggerRegex.Matches(text ?? "").Count;
}

