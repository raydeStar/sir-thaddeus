using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Search;

// ─────────────────────────────────────────────────────────────────────────
// Query Builder — Constrained Query Construction
//
// Replaces the old ExtractSearchViaToolCallAsync with a structured,
// validated approach. The LLM is given tight constraints and a schema;
// output is validated against the input tokens; rejected queries fall
// back to deterministic templates.
//
// Shared by NewsPipeline and FactFindPipeline with mode-specific
// constraints:
//   - News mode: recency window + "headlines" style
//   - FactFind mode: canonical entity + "overview/bio" style
//
// Key difference from the old approach: the LLM does NOT freestyle.
// It constructs from constrained inputs. Validation catches drift.
// Fallback templates guarantee a usable query.
// ─────────────────────────────────────────────────────────────────────────

public sealed partial class QueryBuilder
{
    private readonly ILlmClient   _llm;
    private readonly IAuditLogger _audit;

    /// <summary>Minimum acceptable query length.</summary>
    private const int MinQueryLength = 6;

    /// <summary>
    /// Glue words allowed in the query that don't need to come from
    /// the user message or entity. Keeps validation from rejecting
    /// reasonable queries like "latest Japan earthquake news."
    /// </summary>
    private static readonly HashSet<string> AllowedGlueWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "latest", "news", "today", "current", "recent",
        "who", "what", "when", "where", "how", "why",
        "is", "are", "was", "the", "a", "an", "of", "in", "on",
        "about", "for", "from", "to", "and", "or",
        "prime", "minister", "president", "ceo", "leader",
        "biography", "overview", "background", "history",
        "update", "updates", "report", "reports",
        "headlines", "breaking", "forecast"
    };

    private static readonly HashSet<string> ConversationalNoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "wassup", "sup", "diggy", "bro", "dude", "homie", "home", "yo", "hey",
        "pls", "please", "thanks", "thank", "you", "can", "could", "would", "will", "me"
    };

    public QueryBuilder(ILlmClient llm, IAuditLogger audit)
    {
        _llm   = llm   ?? throw new ArgumentNullException(nameof(llm));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>
    /// Result of query construction — validated query + recency.
    /// </summary>
    public sealed record SearchQuery
    {
        public required string Query   { get; init; }
        public required string Recency { get; init; }
        public bool UsedFallback       { get; init; }
    }

    /// <summary>
    /// Constructs a search query using the LLM with validation and
    /// fallback templates. The LLM is constrained by mode-specific
    /// instructions and the output is validated against input tokens.
    /// </summary>
    public async Task<SearchQuery> BuildAsync(
        SearchMode mode,
        string userMessage,
        EntityResolver.ResolvedEntity? entity,
        SearchSession session,
        IReadOnlyList<ChatMessage> recentHistory,
        CancellationToken ct)
    {
        // ── Try LLM construction first ───────────────────────────────
        var llmQuery = await TryLlmConstructionAsync(
            mode, userMessage, entity, session, recentHistory, ct);

        if (llmQuery is not null)
        {
            var normalizedQuery = SanitizeConstructedQuery(llmQuery.Query);
            normalizedQuery = NormalizeNewsQueryIfNeeded(mode, normalizedQuery, userMessage, entity);
            var normalizedRecency = ResolveRecency(mode, userMessage, llmQuery.Recency);

            // ── Validate the LLM output ──────────────────────────────
            var valid = ValidateQuery(normalizedQuery, userMessage, entity);
            if (valid)
            {
                if (!string.Equals(normalizedQuery, llmQuery.Query, StringComparison.Ordinal))
                {
                    _audit.Append(new AuditEvent
                    {
                        Actor  = "agent",
                        Action = "QUERY_SANITIZED",
                        Result = $"\"{llmQuery.Query}\" -> \"{normalizedQuery}\""
                    });
                }

                _audit.Append(new AuditEvent
                {
                    Actor  = "agent",
                    Action = "QUERY_BUILT",
                    Result = $"\"{normalizedQuery}\" (recency={normalizedRecency})"
                });
                return llmQuery with { Query = normalizedQuery, Recency = normalizedRecency };
            }

            _audit.Append(new AuditEvent
            {
                Actor  = "agent",
                Action = "QUERY_REJECTED",
                Result = $"LLM query \"{llmQuery.Query}\" failed validation — using fallback",
                Details = new Dictionary<string, object>
                {
                    ["rejected_query"] = llmQuery.Query,
                    ["reason"]         = "token_validation_failed"
                }
            });
        }

        // ── Fallback to deterministic templates ──────────────────────
        var fallback = BuildFallbackQuery(mode, userMessage, entity, session);

        _audit.Append(new AuditEvent
        {
            Actor  = "agent",
            Action = "QUERY_FALLBACK",
            Result = $"\"{fallback.Query}\" (recency={fallback.Recency})"
        });

        return fallback;
    }

    // ─────────────────────────────────────────────────────────────────
    // LLM Construction
    // ─────────────────────────────────────────────────────────────────

    private async Task<SearchQuery?> TryLlmConstructionAsync(
        SearchMode mode,
        string userMessage,
        EntityResolver.ResolvedEntity? entity,
        SearchSession session,
        IReadOnlyList<ChatMessage> recentHistory,
        CancellationToken ct)
    {
        var isMarketQuoteRequest = MarketQuoteHeuristics.IsMarketQuoteRequest(userMessage);

        var modeInstruction = mode switch
        {
            SearchMode.NewsAggregate =>
                "Mode: NEWS. Build a search query optimized for finding recent news articles. " +
                "Include temporal markers (latest, today, this week) when appropriate. " +
                "The query should return news articles, not encyclopedia pages.",

            SearchMode.WebFactFind =>
                isMarketQuoteRequest
                    ? "Mode: MARKET_QUOTE. Build a query for current market quote coverage. " +
                      "Focus on the latest index level / point move / percent move today. " +
                      "Prefer live market updates and major financial outlets."
                    : "Mode: FACTFIND. Build a search query optimized for finding factual, " +
                      "encyclopedic information. Prefer stable sources (Wikipedia, reference sites). " +
                      "Use the canonical entity name if provided.",

            _ => "Build a concise, effective search query."
        };

        var entityContext = entity is not null
            ? $"\nResolved entity: \"{entity.CanonicalName}\" ({entity.Type}, {entity.Disambiguation})"
            : "";

        var sessionContext = session.LastQuery is not null
            ? $"\nPrevious query: \"{session.LastQuery}\" (recency: {session.LastRecency})"
            : "";

        // Only feed recent user messages (not tool output) to avoid
        // salience pollution. This is the key fix for query drift.
        var recentUserMessages = recentHistory
            .Where(m => m.Role == "user")
            .TakeLast(3)
            .Select(m => m.Content ?? "")
            .ToList();

        var contextLines = recentUserMessages.Count > 1
            ? "\nRecent user messages:\n" +
              string.Join("\n", recentUserMessages.Select(m => $"  - {Truncate(m, 80)}"))
            : "";

        var systemPrompt =
            "You are a search query builder. Given a user message and context, " +
            "construct an effective search query.\n\n" +
            modeInstruction + entityContext + sessionContext + contextLines +
            "\n\nReturn ONLY a JSON object:\n" +
            "  { \"query\": \"your search query\", \"recency\": \"day|week|month|any\" }\n\n" +
            "Rules:\n" +
            "- Query must be 6-80 characters\n" +
            "- Use only words from the user's message, the entity name, or common search terms\n" +
            "- Do NOT invent names or facts\n" +
            "- Do NOT include URLs or domains\n" +
            "- Return ONLY the JSON. No explanation.";

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(systemPrompt),
            ChatMessage.User(userMessage)
        };

        try
        {
            var response = await _llm.ChatAsync(messages, tools: null, maxTokensOverride: 96, ct);
            var content  = StripCodeFences((response.Content ?? "").Trim());

            if (string.IsNullOrWhiteSpace(content))
                return null;

            var parsed  = JsonSerializer.Deserialize<JsonElement>(content);
            var query   = parsed.TryGetProperty("query", out var q) ? q.GetString() : null;
            var recency = parsed.TryGetProperty("recency", out var r) ? r.GetString() : "any";

            if (string.IsNullOrWhiteSpace(query))
                return null;

            recency = NormalizeRecency(recency);

            return new SearchQuery
            {
                Query   = query!.Trim(),
                Recency = recency
            };
        }
        catch
        {
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Validation
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates that the query contains only tokens from acceptable
    /// sources (user message, entity, glue words). Rejects hallucinated
    /// or drifted queries.
    /// </summary>
    private static bool ValidateQuery(
        string query,
        string userMessage,
        EntityResolver.ResolvedEntity? entity)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < MinQueryLength)
            return false;

        if (query.Length > 120)
            return false;

        // Build the allowed token pool
        var allowed = new HashSet<string>(AllowedGlueWords, StringComparer.OrdinalIgnoreCase);

        // Add user message tokens
        foreach (var token in Tokenize(userMessage))
            allowed.Add(token);

        // Add entity tokens
        if (entity is not null)
        {
            foreach (var token in Tokenize(entity.CanonicalName))
                allowed.Add(token);
            foreach (var token in Tokenize(entity.Disambiguation))
                allowed.Add(token);
        }

        // Check that every query token is in the allowed pool
        var queryTokens = Tokenize(query);
        var unknownCount = 0;

        foreach (var token in queryTokens)
        {
            if (!allowed.Contains(token))
                unknownCount++;
        }

        // Allow up to 1 unknown token (some flexibility for the LLM to
        // add useful context). Reject if more than 1 are foreign.
        return unknownCount <= 1;
    }

    // ─────────────────────────────────────────────────────────────────
    // Fallback Templates
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a deterministic fallback query when LLM construction
    /// fails or is rejected. Guaranteed to produce a usable query.
    /// </summary>
    internal static SearchQuery BuildFallbackQuery(
        SearchMode mode,
        string userMessage,
        EntityResolver.ResolvedEntity? entity,
        SearchSession session)
    {
        var topic = entity?.CanonicalName
            ?? ExtractTopicFromMessage(userMessage)
            ?? session.LastQuery
            ?? userMessage.Trim();

        // Truncate long topics
        if (topic.Length > 60)
            topic = topic[..60].Trim();

        return mode switch
        {
            SearchMode.NewsAggregate => new SearchQuery
            {
                Query       = BuildNewsFallbackQueryText(userMessage, topic, entity),
                Recency     = DetectRecencyFromMessage(userMessage),
                UsedFallback = true
            },

            SearchMode.WebFactFind => MarketQuoteHeuristics.IsMarketQuoteRequest(userMessage)
                ? new SearchQuery
                {
                    // For market quotes, always derive topic from the user message
                    // (not the noisy ExtractTopicFromMessage output).
                    Query        = BuildMarketQuoteFallbackQuery(userMessage),
                    Recency      = MarketQuoteHeuristics.PreferredRecency(userMessage),
                    UsedFallback = true
                }
                : new SearchQuery
                {
                    Query        = entity is not null ? $"{entity.CanonicalName} overview" : $"{topic}",
                    Recency      = "any",
                    UsedFallback = true
                },

            _ => new SearchQuery
            {
                Query       = topic,
                Recency     = "any",
                UsedFallback = true
            }
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the most topical portion of a user message by stripping
    /// conversational filler and request verbs.
    /// </summary>
    internal static string? ExtractTopicFromMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var normalized = CollapseWhitespace(message);
        var prefixTopic = ExtractTopicBeforeRequest(normalized);

        var cleaned = StripPrefaceBeforeRequest(normalized);
        cleaned = CleanTopicCandidate(cleaned);

        // If the tail-side topic is generic ("check on the news there"),
        // prefer a stronger topic that appeared before the request marker.
        if (LooksLikeGenericTopic(cleaned) && !string.IsNullOrWhiteSpace(prefixTopic))
            cleaned = prefixTopic!;

        if (cleaned.Length < 3)
            return null;

        return cleaned.Length >= 3 ? cleaned : null;
    }

    /// <summary>
    /// Detects recency from temporal markers in the user message.
    /// Returns the tightest matching window.
    /// </summary>
    internal static string DetectRecencyFromMessage(string message)
    {
        var lower = (message ?? "").ToLowerInvariant();

        if (lower.Contains("today") || lower.Contains("breaking") ||
            lower.Contains("right now") || lower.Contains("just happened"))
            return "day";

        if (lower.Contains("most recent") || lower.Contains("most recently") ||
            lower.Contains("past few hours") || lower.Contains("last few hours") ||
            lower.Contains("latest"))
        {
            return "day";
        }

        if (lower.Contains("this week") || lower.Contains("past week") ||
            lower.Contains("last week") ||
            lower.Contains("last few days"))
            return "week";

        if (lower.Contains("this month") || lower.Contains("past month") ||
            lower.Contains("recently"))
            return "month";

        // Default for news queries: day (most users want "today's news")
        return "day";
    }

    private static string NormalizeRecency(string? recency)
    {
        var r = (recency ?? "any").Trim().ToLowerInvariant();
        return r switch
        {
            "day" or "today" or "24h"    => "day",
            "week" or "7d"               => "week",
            "month" or "30d"             => "month",
            "any" or "all" or "none"     => "any",
            _ => "any"
        };
    }

    private static IReadOnlyList<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text.ToLowerInvariant()
            .Split(SplitChars, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1)
            .ToList();
    }

    private static string StripCodeFences(string content)
    {
        if (content.StartsWith("```"))
        {
            var endIdx = content.IndexOf('\n');
            if (endIdx > 0) content = content[(endIdx + 1)..];
        }
        if (content.EndsWith("```"))
            content = content[..^3];
        return content.Trim();
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "…";

    /// <summary>
    /// Cleans conversational filler from LLM-constructed queries.
    /// Example:
    ///   "Not much - hey can you pull up recent headlines from last week?"
    /// becomes:
    ///   "recent headlines from last week"
    /// </summary>
    private static string SanitizeConstructedQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "";

        var cleaned = query.Trim();

        // Strip common conversational prefaces.
        cleaned = ConversationPrefixRegex().Replace(cleaned, "");

        // Repeatedly strip lead request phrases until stable.
        for (var i = 0; i < 3; i++)
        {
            var next = StripLeadPhrasesRegex().Replace(cleaned, "").Trim();
            if (string.Equals(next, cleaned, StringComparison.Ordinal))
                break;
            cleaned = next;
        }

        // If the model echoed "can you ...", keep only the topical tail.
        var requestIdx = cleaned.IndexOf(" can you ", StringComparison.OrdinalIgnoreCase);
        if (requestIdx >= 0)
            cleaned = cleaned[(requestIdx + " can you ".Length)..];

        cleaned = cleaned
            .Trim()
            .Trim('-', ':', ';', ',', '.', '?', '!', '"', '\'');

        // Collapse whitespace to single spaces.
        cleaned = string.Join(' ', cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return cleaned;
    }

    private static string ResolveRecency(SearchMode mode, string userMessage, string llmRecency)
    {
        if (MarketQuoteHeuristics.IsMarketQuoteRequest(userMessage))
            return MarketQuoteHeuristics.PreferredRecency(userMessage);

        var inferred = DetectRecencyFromMessage(userMessage);
        var normalizedLlmRecency = NormalizeRecency(llmRecency);

        // Explicit temporal markers in the user message should win.
        if (HasExplicitRecencyMarker(userMessage))
            return inferred;

        // For news, keep "day" as default only when no explicit marker exists.
        if (mode == SearchMode.NewsAggregate && string.IsNullOrWhiteSpace(normalizedLlmRecency))
            return inferred;

        return normalizedLlmRecency;
    }

    private static string NormalizeNewsQueryIfNeeded(
        SearchMode mode,
        string query,
        string userMessage,
        EntityResolver.ResolvedEntity? entity)
    {
        if (mode != SearchMode.NewsAggregate)
            return query;

        var cleaned = CollapseWhitespace(query).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            return "top headlines";

        var tokens = Tokenize(cleaned);
        var nonNewsTokens = tokens
            .Where(t => !IsGenericNewsToken(t) && !ConversationalNoiseTokens.Contains(t))
            .ToList();

        // If the query is basically just "news/headlines/latest" plus chatter,
        // canonicalize it to a stable, high-signal search.
        if (nonNewsTokens.Count == 0)
            return "top headlines";

        // If user asked for generic headlines (no specific entity), avoid noisy wording.
        if (entity is null && LooksLikeGenericHeadlineRequest(userMessage) && nonNewsTokens.Count <= 2)
            return "top headlines";

        return cleaned;
    }

    private static string BuildNewsFallbackQueryText(
        string userMessage,
        string topic,
        EntityResolver.ResolvedEntity? entity)
    {
        if (entity is not null)
            return $"{entity.CanonicalName} latest news";

        if (LooksLikeGenericHeadlineRequest(userMessage))
            return "top headlines";

        return $"{topic} news latest";
    }

    private static string BuildMarketQuoteFallbackQuery(string rawTopic)
    {
        // Prefer the canonical instrument name over the raw topic, which
        // is often a noisy conversational fragment. "Dow Jones market
        // update today" is far more effective than "up the dow jones
        // for me today? whats it at live quote today points percent".
        var canonical = MarketQuoteHeuristics.ExtractCanonicalInstrument(rawTopic);
        var instrument = canonical ?? (string.IsNullOrWhiteSpace(rawTopic) ? "Dow Jones" : rawTopic.Trim());

        // Truncate if an uncleaned topic leaked through.
        if (instrument.Length > 30)
            instrument = instrument[..30].Trim();

        return $"{instrument} market update today";
    }

    private static bool LooksLikeGenericHeadlineRequest(string message)
    {
        var lower = (message ?? "").ToLowerInvariant();
        var hasHeadlineIntent =
            lower.Contains("headline") ||
            lower.Contains("headlines") ||
            lower.Contains("breaking news") ||
            lower.Contains("latest news") ||
            lower.Contains("what's happening") ||
            lower.Contains("whats happening") ||
            lower.Contains("news update");

        if (!hasHeadlineIntent)
            return false;

        // If there is no obvious entity marker, this is likely a generic briefing.
        return !lower.Contains("about ") && !lower.Contains(" on ") && !lower.Contains(" regarding ");
    }

    private static bool IsGenericNewsToken(string token) =>
        token is "news" or "headline" or "headlines" or "breaking" or "latest" or "recent" or "top" or "updates" or "update" or "this";

    private static bool HasExplicitRecencyMarker(string message)
    {
        var lower = (message ?? "").ToLowerInvariant();
        return lower.Contains("last week") || lower.Contains("this week") || lower.Contains("past week") ||
               lower.Contains("last month") || lower.Contains("this month") || lower.Contains("past month") ||
               lower.Contains("today") || lower.Contains("yesterday") || lower.Contains("right now");
    }

    private static string StripPrefaceBeforeRequest(string message)
    {
        // Handles "wassup ...? can you ...", keeping only the request segment.
        var lowered = message.ToLowerInvariant();
        ReadOnlySpan<string> markers =
        [
            " can you ", " could you ", " would you ", " will you ",
            " please ", " pull up ", " bring up ", " show me ",
            " get me ", " search for ", " look up ", " find "
        ];

        foreach (var marker in markers)
        {
            var idx = lowered.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
                return message[(idx + marker.Length)..].Trim();
        }

        return message;
    }

    private static string CollapseWhitespace(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string CleanTopicCandidate(string text)
    {
        var cleaned = text ?? "";
        cleaned = LeadingFillerRegex().Replace(cleaned, "").Trim();
        cleaned = LeadingRequestVerbRegex().Replace(cleaned, "").Trim();
        cleaned = StripLeadPhrasesRegex().Replace(cleaned, "").Trim();
        cleaned = cleaned.TrimEnd('?', '.', '!', ',');
        cleaned = TailPoliteRegex().Replace(cleaned, "").Trim();
        cleaned = cleaned.TrimEnd('?', '.', '!', ',');
        return cleaned;
    }

    private static string? ExtractTopicBeforeRequest(string message)
    {
        var lowered = message.ToLowerInvariant();
        var markers = new[]
        {
            " can you ", " could you ", " would you ", " will you ",
            " please ", " show me ", " get me ", " pull up ", " bring up ",
            " look up ", " search for ", " find "
        };

        var idx = -1;
        foreach (var marker in markers)
        {
            var markerIdx = lowered.IndexOf(marker, StringComparison.Ordinal);
            if (markerIdx < 0)
                continue;

            if (idx < 0 || markerIdx < idx)
                idx = markerIdx;
        }

        if (idx <= 0)
            return null;

        var prefix = message[..idx].Trim();
        var cleaned = CleanTopicCandidate(prefix);
        return cleaned.Length >= 3 ? cleaned : null;
    }

    private static bool LooksLikeGenericTopic(string cleaned)
    {
        var tokens = Tokenize(cleaned);
        if (tokens.Count == 0)
            return true;

        var genericTokens = 0;
        foreach (var token in tokens)
        {
            if (GenericTopicTokens.Contains(token))
                genericTokens++;
        }

        // Mostly generic words -> weak topic.
        return genericTokens >= tokens.Count - 1;
    }

    private static readonly char[] SplitChars =
        [' ', '-', '–', '—', ',', '.', ':', ';', '!', '?', '\'', '"', '(', ')', '[', ']', '/'];

    private static readonly HashSet<string> GenericTopicTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "check", "on", "the", "news", "there", "that", "this", "it",
        "about", "latest", "recent", "update", "updates", "please", "me"
    };

    [GeneratedRegex(
        @"^(?:can you |could you |please |hey |hi |yo |search for |look up |find |get me |show me |pull up |bring up |what(?:'s| is| are) )+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex StripLeadPhrasesRegex();

    [GeneratedRegex(
        @"^\s*(?:not much|i'?m good|im good|thanks|thank you|ok|okay|well|alright)[\s,\-:!]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ConversationPrefixRegex();

    [GeneratedRegex(
        @"^\s*(?:well[,.!?]?\s*)?(?:i\s+(?:wanted|want|need|was hoping|would like)\s+to\s+)+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LeadingFillerRegex();

    [GeneratedRegex(
        @"^\s*(?:check|look\s+up|look|find|get|show|pull\s+up|bring\s+up)\s+(?:on\s+)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LeadingRequestVerbRegex();

    [GeneratedRegex(
        @"(?:\s+for me)?(?:\s+please|\s+pls|\s+thanks|\s+thank you)+\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TailPoliteRegex();
}
