using System.Text.RegularExpressions;
using SirThaddeus.Agent.ConversationSegmentation;

namespace SirThaddeus.Agent.Pipeline;

public sealed class RequestPreprocessor : IRequestPreprocessor
{
    private static readonly Regex MultiWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex LeadingActionPrefixRegex = new(
        @"^\s*(?:can\s+you|could\s+you|would\s+you|please|help\s+me|get\s+me|find\s+me|show\s+me|look\s+up|search\s+for)\b[\s,:-]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SequencerRegex = new(
        @"\s*(?:,\s*)?(?:then|and\s+then|and)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ChitchatRegex = new(
        @"^\s*(?:hey|hi|hello|yo|good\s+(?:morning|afternoon|evening)|how\s+are\s+you|how'?s\s+it\s+going|what'?s\s+up)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IConversationSegmenter _segmenter;

    public RequestPreprocessor(IConversationSegmenter? segmenter = null)
    {
        _segmenter = segmenter ?? new ConversationSegmenter();
    }

    public PreprocessorResult Decompose(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return new PreprocessorResult
            {
                Intents = [],
                IsMultiIntent = false
            };
        }

        var segmentation = _segmenter.Segment(userMessage);
        var rawParts = SplitSegments(segmentation.Segments);

        var intents = new List<PipelineIntent>(rawParts.Count);
        for (var index = 0; index < rawParts.Count; index++)
        {
            var part = rawParts[index];
            var normalized = Normalize(part.Text, part.IsActionable);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            intents.Add(new PipelineIntent
            {
                OriginalFragment = part.Text,
                NormalizedRequest = normalized,
                Type = PipelineIntentType.Unknown,
                Order = intents.Count,
                Confidence = part.Confidence
            });
        }

        if (intents.Count == 0)
        {
            intents.Add(new PipelineIntent
            {
                OriginalFragment = userMessage.Trim(),
                NormalizedRequest = Normalize(userMessage, isActionable: true),
                Type = PipelineIntentType.Unknown,
                Order = 0,
                Confidence = 0.5
            });
        }

        return new PreprocessorResult
        {
            Intents = intents,
            IsMultiIntent = intents.Count > 1
        };
    }

    private static List<(string Text, bool IsActionable, double Confidence)> SplitSegments(
        IReadOnlyList<ConversationSegment> segments)
    {
        var output = new List<(string Text, bool IsActionable, double Confidence)>();

        foreach (var segment in segments.OrderBy(s => s.Order))
        {
            var candidateParts = SplitBySequencers(segment.Text);
            foreach (var candidate in candidateParts)
            {
                var trimmed = candidate.Trim();
                if (trimmed.Length < 2)
                    continue;

                var isChitchat = LooksLikeChitchat(trimmed);
                var isActionable = !isChitchat && (segment.IsActionable || LooksActionable(trimmed));

                if (output.Count > 0 && !isActionable && !output[^1].IsActionable)
                {
                    var merged = $"{output[^1].Text} {trimmed}".Trim();
                    output[^1] = (merged, false, Math.Min(output[^1].Confidence, segment.Confidence));
                }
                else
                {
                    output.Add((trimmed, isActionable, segment.Confidence));
                }
            }
        }

        return output;
    }

    private static IReadOnlyList<string> SplitBySequencers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var parts = SequencerRegex
            .Split(text)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        return parts.Count > 0 ? parts : [text.Trim()];
    }

    private static bool LooksLikeChitchat(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return ChitchatRegex.IsMatch(text);
    }

    private static bool LooksActionable(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        return lower.Contains("weather", StringComparison.Ordinal)
            || lower.Contains("news", StringComparison.Ordinal)
            || lower.Contains("search", StringComparison.Ordinal)
            || lower.Contains("summarize", StringComparison.Ordinal)
            || lower.Contains("draft", StringComparison.Ordinal)
            || lower.Contains("remind", StringComparison.Ordinal)
            || lower.Contains("read file", StringComparison.Ordinal)
            || lower.Contains("open file", StringComparison.Ordinal)
            || lower.Contains("show me", StringComparison.Ordinal)
            || lower.Contains("write", StringComparison.Ordinal)
            || lower.Contains("create", StringComparison.Ordinal)
            || lower.Contains("run", StringComparison.Ordinal)
            || text.Contains('?', StringComparison.Ordinal);
    }

    private static string Normalize(string text, bool isActionable)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = MultiWhitespaceRegex.Replace(text.Trim(), " ");

        if (isActionable)
        {
            normalized = LeadingActionPrefixRegex.Replace(normalized, "");
            normalized = MultiWhitespaceRegex.Replace(normalized.Trim(), " ");
        }

        return normalized.Trim().TrimEnd('.', '!', '?');
    }
}
