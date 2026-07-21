using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Search;

/// <summary>
/// Recognizes only requests for the machine's current local date or time.
/// Questions about another place, an event, a conversion, or elapsed time are
/// deliberately left to the ordinary model and tool pipeline.
/// </summary>
internal static partial class CurrentDateTimeUtility
{
    [GeneratedRegex(
        @"^(?:(?:hey|hi|hello|yo)(?:\s+(?:thaddeus|assistant|there))?[,!]?\s+|" +
        @"please\s+|(?:can|could|would)\s+you\s+|" +
        @"(?:i\s+(?:need|want)\s+to\s+know|do\s+you\s+know)\s+)+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PolitePrefixPattern();

    [GeneratedRegex(
        @"\s*(?:[?.!,]\s*)?(?:please|briefly|(?:tell\s+me\s+)?in\s+one\s+sentence)?\s*[?.!]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BenignSuffixPattern();

    [GeneratedRegex(
        @"^(?:" +
            @"what(?:'s|\s+is)?\s+(?:the\s+)?(?:current\s+)?(?:date|day)(?:\s+today)?|" +
            @"what(?:'s|\s+is)?\s+today'?s\s+(?:date|day)|" +
            @"what\s+(?:date|day)\s+is\s+today|" +
            @"what\s+day\s+(?:of\s+the\s+week\s+)?is\s+it(?:\s+today)?|" +
            @"what\s+day\s+it\s+is(?:\s+today)?|" +
            @"what\s+(?:date|day)\s+today|" +
            @"which\s+date\s+is\s+today|" +
            @"today\s+is\s+what\s+day|" +
            @"(?:tell|give|show|display|check)\s+(?:me\s+)?(?:what\s+)?(?:the\s+)?(?:current\s+)?(?:date|day)(?:\s+(?:is\s+)?today)?|" +
            @"(?:tell|give|show|display|check)\s+(?:me\s+)?today'?s\s+(?:date|day)|" +
            @"(?:the\s+date|current\s+date)(?:\s+(?:of\s+)?today)?" +
        @")$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrentDatePattern();

    [GeneratedRegex(
        @"^(?:" +
            @"what(?:'s|\s+is)?\s+(?:the\s+)?(?:(?:current\s+)?(?:local\s+)?|correct\s+)time(?:\s+(?:right\s+now|now))?|" +
            @"what\s+time\s+is\s+it(?:\s+(?:right\s+now|now))?|" +
            @"what\s+time\s+it\s+is(?:\s+(?:right\s+now|now))?|" +
            @"(?:tell|give|show|display|check)\s+(?:me\s+)?(?:what\s+)?(?:the\s+)?(?:(?:current\s+)?(?:local\s+)?|correct\s+)time(?:\s+(?:is|it\s+is))?(?:\s+(?:right\s+now|now))?|" +
            @"can\s+i\s+(?:get|know)\s+(?:what(?:'s|\s+is)\s+)?(?:the\s+)?(?:current\s+)?time(?:\s+now)?|" +
            @"do\s+you\s+have\s+the\s+time|" +
            @"(?:(?:the|current|local)\s+)?time|clock\s+time" +
        @")$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrentTimePattern();

    [GeneratedRegex(
        @"\b(?:time\s+difference|convert|conversion|relative\s+to|time\s*zone|" +
        @"gmt|utc|[ecmp][sd]t|hours?\s+(?:ahead|behind)|" +
        @"alarm|appointment|calendar|event|flight|meeting|reminder|show|train|bus|match|" +
        @"elapsed|remaining|left|until|since|ago|tomorrow|yesterday)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonLocalTimeCuePattern();

    [GeneratedRegex(
        @"\b(?:time|clock)\b.*\b(?:in|at|for|on)\s+(?!one\s+sentence\b|brief\b|briefly\b)[a-z]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocationScopedTimePattern();

    [GeneratedRegex(
        @"\b(?:birthday|christmas|easter|halloween|holiday|valentine|anniversary|" +
        @"appointment|deadline|event|meeting|schedule|reminder|yesterday|tomorrow|" +
        @"next\s+|last\s+|ago|future|past|(?:19|20)\d{2})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonCurrentDateCuePattern();

    public static DeterministicUtilityResult? TryMatch(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var normalized = Regex.Replace(message.Trim(), @"\s+", " ");
        normalized = PolitePrefixPattern().Replace(normalized, "");
        normalized = BenignSuffixPattern().Replace(normalized, "").Trim();

        if (!NonCurrentDateCuePattern().IsMatch(normalized) &&
            CurrentDatePattern().IsMatch(normalized))
        {
            var now = DateTimeOffset.Now;
            return new DeterministicUtilityResult
            {
                Category = "date",
                Answer = $"Today is **{now:dddd, MMMM d, yyyy}** ({now:yyyy-MM-dd})."
            };
        }

        if (NonLocalTimeCuePattern().IsMatch(normalized) ||
            LocationScopedTimePattern().IsMatch(normalized) ||
            !CurrentTimePattern().IsMatch(normalized))
        {
            return null;
        }

        var localNow = DateTimeOffset.Now;
        return new DeterministicUtilityResult
        {
            Category = "time",
            Answer = $"The current local time is **{localNow:h:mm tt}** ({localNow:dddd, MMMM d, yyyy})."
        };
    }
}
