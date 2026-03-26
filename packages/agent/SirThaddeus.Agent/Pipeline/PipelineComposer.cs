namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Composes execution results into a final response. Merges multi-segment
/// outputs, handles partial failures gracefully, and applies sanitization.
/// </summary>
public sealed class PipelineComposer : IResponseComposer
{
    private readonly Func<string, string>? _sanitize;

    /// <summary>
    /// Creates a composer with an optional sanitization function.
    /// In production, this wraps DeterministicChatPostProcessor.SanitizeFinalResponse.
    /// </summary>
    public PipelineComposer(Func<string, string>? sanitize = null)
    {
        _sanitize = sanitize;
    }

    public ComposerResult Compose(
        string originalMessage,
        PreprocessorResult preprocessed,
        ExecutorResult executed)
    {
        var warnings = new List<string>();
        var parts = new List<string>();

        // Separate greeting/chat context from actionable results
        var chatSegments = new List<ExecutionSegmentResult>();
        var actionSegments = new List<ExecutionSegmentResult>();

        foreach (var segment in executed.Segments)
        {
            if (!segment.Source.RequiresExecution)
                chatSegments.Add(segment);
            else
                actionSegments.Add(segment);
        }

        // Build social lead from chat segments
        var socialLead = BuildSocialLead(chatSegments);
        if (!string.IsNullOrWhiteSpace(socialLead))
            parts.Add(socialLead);

        // Append actionable results
        var succeeded = actionSegments.Where(s => s.Success).ToList();
        var failed = actionSegments.Where(s => !s.Success).ToList();

        foreach (var result in succeeded)
        {
            if (!string.IsNullOrWhiteSpace(result.ResponseText))
                parts.Add(result.ResponseText);
        }

        // Handle failures
        if (failed.Count > 0)
        {
            if (succeeded.Count == 0)
            {
                // All failed — compose a graceful failure message
                parts.Add(BuildFailureSummary(failed));
                warnings.Add($"All {failed.Count} action segment(s) failed.");
            }
            else
            {
                // Partial failure — note what didn't work
                warnings.Add($"{failed.Count} of {actionSegments.Count} action segment(s) failed.");
            }
        }

        // Fall back to a minimal response if nothing was produced
        if (parts.Count == 0)
        {
            parts.Add("I wasn't able to complete that request.");
            warnings.Add("No content produced by any pipeline segment.");
        }

        var raw = string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        var final = _sanitize is not null ? _sanitize(raw) : raw;

        return new ComposerResult
        {
            FinalResponse = final,
            WasSanitized = _sanitize is not null && !string.Equals(raw, final, StringComparison.Ordinal),
            Warnings = warnings
        };
    }

    private static string BuildSocialLead(IReadOnlyList<ExecutionSegmentResult> chatSegments)
    {
        if (chatSegments.Count == 0)
            return "";

        // Use the original fragment for greeting context
        var greetings = chatSegments
            .Select(s => s.Source.Source.Source.OriginalFragment)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (greetings.Count == 0)
            return "";

        var greeting = greetings[0].Trim();
        return LooksLikeGreeting(greeting) ? MapGreetingResponse(greeting) : "";
    }

    private static bool LooksLikeGreeting(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.StartsWith("hey", StringComparison.Ordinal)
            || lower.StartsWith("hi", StringComparison.Ordinal)
            || lower.StartsWith("hello", StringComparison.Ordinal)
            || lower.StartsWith("yo", StringComparison.Ordinal)
            || lower.StartsWith("good morning", StringComparison.Ordinal)
            || lower.StartsWith("good afternoon", StringComparison.Ordinal)
            || lower.StartsWith("good evening", StringComparison.Ordinal);
    }

    private static string MapGreetingResponse(string greeting)
    {
        var lower = greeting.ToLowerInvariant();
        if (lower.StartsWith("good morning", StringComparison.Ordinal))
            return "Good morning!";
        if (lower.StartsWith("good afternoon", StringComparison.Ordinal))
            return "Good afternoon!";
        if (lower.StartsWith("good evening", StringComparison.Ordinal))
            return "Good evening!";
        return "Hey!";
    }

    private static string BuildFailureSummary(IReadOnlyList<ExecutionSegmentResult> failed)
    {
        if (failed.Count == 1)
        {
            var err = failed[0].Error;
            return string.IsNullOrWhiteSpace(err)
                ? "I ran into an issue processing that request."
                : $"I ran into an issue: {err}";
        }

        return $"I ran into issues with {failed.Count} parts of that request.";
    }
}
