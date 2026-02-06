namespace SirThaddeus.Memory;

/// <summary>
/// Cheap, rules-based intent classifier for driving the anti-creepiness
/// gate. No LLM call — just keyword matching.  Can be swapped for a
/// model-based classifier later without changing the retrieval pipeline.
/// </summary>
public static class IntentClassifier
{
    // ── Keyword banks ────────────────────────────────────────────────

    private static readonly string[] PersonalKeywords =
    [
        "feel", "feeling", "feelings", "emotion", "emotions", "sad",
        "happy", "angry", "anxious", "depressed", "stressed",
        "relationship", "relationships", "family", "friend", "friends",
        "love", "hate", "health", "doctor", "therapy", "therapist",
        "journal", "diary", "dream", "dreams", "birthday",
        "anniversary", "personal", "private", "dating", "marriage",
        "divorce", "grief", "mourning", "lonely", "loneliness"
    ];

    private static readonly string[] TechnicalKeywords =
    [
        "code", "coding", "program", "programming", "debug", "debugging",
        "error", "exception", "bug", "fix", "build", "compile", "deploy",
        "api", "endpoint", "database", "query", "sql", "function",
        "class", "method", "variable", "type", "interface",
        "architecture", "design", "pattern", "refactor", "test",
        "testing", "git", "commit", "branch", "merge", "pull request",
        "ci", "cd", "pipeline", "docker", "container", "server",
        "client", "frontend", "backend", "framework", "library",
        "package", "dependency", "config", "configuration", "algorithm",
        "data structure", "performance", "optimize", "csharp", "dotnet",
        "python", "javascript", "typescript"
    ];

    private static readonly string[] PlanningKeywords =
    [
        "schedule", "calendar", "meeting", "appointment", "deadline",
        "task", "todo", "plan", "planning", "trip", "travel", "flight",
        "hotel", "booking", "reservation", "buy", "purchase", "price",
        "cost", "budget", "shopping", "list", "remind", "reminder",
        "tomorrow", "next week", "next month", "event", "agenda",
        "organize", "prepare", "goal", "goals", "project", "milestone"
    ];

    // ── Public API ───────────────────────────────────────────────────

    /// <summary>
    /// Classifies the user's query intent using keyword rules.
    /// Falls back to GENERAL if no strong signal is detected.
    /// </summary>
    public static MemoryIntent Classify(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return MemoryIntent.General;

        var lower = query.ToLowerInvariant();
        var words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var personalScore  = CountMatches(words, lower, PersonalKeywords);
        var technicalScore = CountMatches(words, lower, TechnicalKeywords);
        var planningScore  = CountMatches(words, lower, PlanningKeywords);

        // Require at least one match to classify
        if (personalScore == 0 && technicalScore == 0 && planningScore == 0)
            return MemoryIntent.General;

        // Highest score wins; ties go to the safer (less sensitive) category
        if (technicalScore >= personalScore && technicalScore >= planningScore)
            return MemoryIntent.Technical;

        if (planningScore >= personalScore)
            return MemoryIntent.Planning;

        return MemoryIntent.Personal;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static int CountMatches(string[] words, string fullText, string[] keywords)
    {
        var count = 0;
        foreach (var kw in keywords)
        {
            if (kw.Contains(' '))
            {
                // Multi-word keywords: check full text
                if (fullText.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            else
            {
                // Single-word keywords: match against individual tokens
                foreach (var w in words)
                {
                    if (w == kw || w.StartsWith(kw, StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                        break;
                    }
                }
            }
        }

        return count;
    }
}
