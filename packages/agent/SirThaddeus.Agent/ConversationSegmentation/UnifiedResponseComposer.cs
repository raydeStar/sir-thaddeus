using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.ConversationSegmentation;

public sealed record UnifiedResponseComposeRequest
{
    public required string OriginalMessage { get; init; }
    public IReadOnlyList<string> NonActionableContext { get; init; } = [];
    public IReadOnlyList<SegmentExecutionResult> Executed { get; init; } = [];
    public IReadOnlyList<ConversationSegment> Deferred { get; init; } = [];
}

/// <summary>
/// Builds one cohesive assistant response from segment execution output.
/// Separates successful results from failed ones so the user sees clean
/// data first, with a gentle note about anything that didn't resolve.
/// </summary>
public sealed class UnifiedResponseComposer
{
    private static readonly Regex GreetingRegex = new(
        @"\b(?:hey|hi|hello|yo|good\s+(?:morning|afternoon|evening)|how are you|what'?s up)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DistressRegex = new(
        @"\b(?:trouble|rough|stressed|upset|bad day|in trouble)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Compose(UnifiedResponseComposeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parts = new List<string>();

        var socialLead = BuildSocialLead(request.OriginalMessage, request.NonActionableContext);
        if (!string.IsNullOrWhiteSpace(socialLead))
            parts.Add(socialLead);

        var succeeded = request.Executed.Where(e => e.Success).ToList();
        var failed    = request.Executed.Where(e => !e.Success).ToList();

        // Successful results first — this is the primary content.
        AppendResults(parts, succeeded);

        // Failed segments get a brief, clear note — not a raw error dump.
        if (failed.Count > 0)
        {
            var failureSummary = BuildFailureSummary(succeeded, failed);
            if (!string.IsNullOrWhiteSpace(failureSummary))
                parts.Add(failureSummary);
        }

        if (request.Deferred.Count > 0)
            parts.Add(BuildDeferredLine(request.Executed, request.Deferred));

        var contextualClose = BuildContextClose(request.NonActionableContext);
        if (!string.IsNullOrWhiteSpace(contextualClose))
            parts.Add(contextualClose);

        var composed = string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return string.IsNullOrWhiteSpace(composed)
            ? "I took a look and I'm ready for the next step."
            : composed.Trim();
    }

    private static void AppendResults(List<string> parts, IReadOnlyList<SegmentExecutionResult> results)
    {
        if (results.Count == 0)
            return;

        if (results.Count == 1)
        {
            parts.Add(results[0].ResponseText.Trim());
            return;
        }

        var joined = string.Join(
            "\n\n",
            results
                .Where(e => !string.IsNullOrWhiteSpace(e.ResponseText))
                .Select(e => e.ResponseText.Trim()));

        if (!string.IsNullOrWhiteSpace(joined))
            parts.Add(joined);
    }

    private static string BuildFailureSummary(
        IReadOnlyList<SegmentExecutionResult> succeeded,
        IReadOnlyList<SegmentExecutionResult> failed)
    {
        if (succeeded.Count > 0 && failed.All(f => IsPresentationOnlyFragment(f.SegmentText)))
            return "";

        if (failed.Count == 1)
        {
            var desc = QuoteShort(failed[0].SegmentText);
            return $"I wasn't able to resolve {desc} — try being more specific or ask again.";
        }

        var items = string.Join(", ", failed.Select(f => QuoteShort(f.SegmentText)));
        return $"I wasn't able to resolve a couple of things ({items}) — try being more specific or ask again.";
    }

    private static bool IsPresentationOnlyFragment(string text)
    {
        var lower = (text ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        return lower.Contains("in one sentence", StringComparison.Ordinal) ||
               lower.Contains("in two sentences", StringComparison.Ordinal) ||
               lower.Contains("in 2 sentences", StringComparison.Ordinal) ||
               lower.Contains("summarize it", StringComparison.Ordinal) ||
               lower.Contains("summarise it", StringComparison.Ordinal) ||
               lower.Contains("short version", StringComparison.Ordinal) ||
               lower.Contains("briefly", StringComparison.Ordinal) ||
               lower.Contains("bullet points", StringComparison.Ordinal);
    }

    private static string BuildSocialLead(string originalMessage, IReadOnlyList<string> nonActionable)
    {
        if (GreetingRegex.IsMatch(originalMessage))
            return "Hey - thanks for the message.";

        var combined = string.Join(" ", nonActionable);
        return GreetingRegex.IsMatch(combined)
            ? "Thanks for the update."
            : "";
    }

    private static string BuildContextClose(IReadOnlyList<string> nonActionable)
    {
        if (nonActionable.Count == 0)
            return "";

        var combined = string.Join(" ", nonActionable);
        if (DistressRegex.IsMatch(combined))
            return "Sorry the day has been rough - if you want, I can help with what to do next.";

        return "";
    }

    // Deterministic template for stable tests and predictable UX.
    private static string BuildDeferredLine(
        IReadOnlyList<SegmentExecutionResult> executed,
        IReadOnlyList<ConversationSegment> deferred)
    {
        var executedItems = executed.Count == 0
            ? "the first request"
            : string.Join(", ", executed.Select(e => QuoteShort(e.SegmentText)));
        var deferredItems = string.Join(", ", deferred.Select(s => QuoteShort(s.Text)));

        return $"I handled {executedItems}. I also noticed: {deferredItems} - tell me which one to do next.";
    }

    private static string QuoteShort(string text)
    {
        var value = (text ?? "").Trim();
        if (value.Length > 40)
            value = value[..40].TrimEnd() + "...";
        return $"\"{value}\"";
    }
}
