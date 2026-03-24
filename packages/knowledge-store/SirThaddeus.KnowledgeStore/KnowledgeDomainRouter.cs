namespace SirThaddeus.KnowledgeStore;

/// <summary>
/// Routes user messages to the correct knowledge domain
/// without the user explicitly saying "write to the journal folder."
/// </summary>
public sealed class KnowledgeDomainRouter
{
    private readonly string _rootPath;
    private string? _activeSessionDomain;

    private static readonly Dictionary<string, string[]> DomainKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["journal"] =
        [
            "ate", "eaten", "calories", "mood", "today",
            "this morning", "tonight", "journal", "log",
            "daily", "diary", "slept", "woke up",
            "worked on", "went to", "met with"
        ],
        ["health"] =
        [
            "bloodwork", "blood work", "labs", "sleep",
            "vitamin", "supplement", "weight", "blood pressure",
            "cholesterol", "doctor", "appointment", "medication"
        ],
        ["routines"] =
        [
            "schedule", "routine", "meal plan", "exercise",
            "workout", "period", "cycle", "habit"
        ],
        ["games"] =
        [
            "adventure", "dungeon", "character", "inventory",
            "attack", "explore", "room", "quest", "roll",
            "hp", "hit points", "gold", "loot"
        ]
    };

    public KnowledgeDomainRouter(string rootPath)
    {
        _rootPath = rootPath;
    }

    /// <summary>
    /// Route a user message to the most likely knowledge domain.
    /// </summary>
    public DomainMatch Route(string message)
    {
        var lower = message.ToLowerInvariant();

        // Priority 1: explicit domain reference ("my journal", "novel notes")
        var explicitMatch = FindExplicitDomainReference(lower);
        if (explicitMatch is not null)
        {
            _activeSessionDomain = explicitMatch.Domain;
            return explicitMatch;
        }

        // Priority 2: active session continuity
        if (_activeSessionDomain is not null)
        {
            var otherDomain = FindKeywordMatch(lower);
            if (otherDomain is not null && otherDomain.Domain != _activeSessionDomain)
            {
                _activeSessionDomain = otherDomain.Domain;
                return otherDomain;
            }

            return new DomainMatch
            {
                Domain = _activeSessionDomain,
                Confidence = DomainConfidence.SessionContinuity
            };
        }

        // Priority 3: keyword matching
        var keywordMatch = FindKeywordMatch(lower);
        if (keywordMatch is not null)
        {
            _activeSessionDomain = keywordMatch.Domain;
            return keywordMatch;
        }

        // Priority 4: no match
        return new DomainMatch
        {
            Domain = null,
            Confidence = DomainConfidence.None
        };
    }

    /// <summary>
    /// Clear the active session domain (e.g., on conversation reset).
    /// </summary>
    public void ClearSession() => _activeSessionDomain = null;

    /// <summary>
    /// Get the currently active session domain (for testing/inspection).
    /// </summary>
    public string? ActiveSessionDomain => _activeSessionDomain;

    private DomainMatch? FindExplicitDomainReference(string message)
    {
        if (!Directory.Exists(_rootPath))
            return null;

        var folders = Directory.GetDirectories(_rootPath)
            .Select(Path.GetFileName)
            .Where(f => f is not null && !f.StartsWith('_'))
            .ToList();

        foreach (var folder in folders)
        {
            if (message.Contains(folder!, StringComparison.OrdinalIgnoreCase))
            {
                return new DomainMatch
                {
                    Domain = folder!,
                    Confidence = DomainConfidence.ExplicitReference
                };
            }
        }

        return null;
    }

    private static DomainMatch? FindKeywordMatch(string message)
    {
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (domain, keywords) in DomainKeywords)
        {
            var hits = keywords.Count(k =>
                message.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (hits > 0)
                scores[domain] = hits;
        }

        if (scores.Count == 0)
            return null;

        var best = scores.OrderByDescending(kv => kv.Value).First();
        return new DomainMatch
        {
            Domain = best.Key,
            Confidence = best.Value >= 2
                ? DomainConfidence.StrongKeywordMatch
                : DomainConfidence.WeakKeywordMatch
        };
    }
}
