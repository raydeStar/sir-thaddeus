using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Deterministic query builder that transforms classified intents into
/// search queries or tool call plans. Strips conversational filler,
/// injects location/time context for web searches, and enforces bounded
/// query length.
/// </summary>
public sealed class PipelineQueryBuilder : IRequestQueryBuilder
{
    private const int MaxSearchQueryLength = 200;

    private static readonly Regex FillerRegex = new(
        @"\b(?:can\s+you|could\s+you|would\s+you|please|I\s+want\s+to\s+know|tell\s+me|I\s+need|help\s+me|just|actually|basically|literally)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MultiWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public Task<QueryBuilderResult> BuildAsync(
        ClassifierResult classified,
        QueryBuilderContext context,
        CancellationToken cancellationToken = default)
    {
        var queries = new List<BuiltQuery>(classified.ClassifiedIntents.Count);

        foreach (var intent in classified.ClassifiedIntents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            queries.Add(BuildForIntent(intent, context));
        }

        return Task.FromResult(new QueryBuilderResult { Queries = queries });
    }

    private static BuiltQuery BuildForIntent(ClassifiedIntent intent, QueryBuilderContext context)
    {
        return intent.MappedType switch
        {
            PipelineIntentType.Chat => BuildChatQuery(intent),
            PipelineIntentType.WebSearch => BuildSearchQuery(intent, context),
            PipelineIntentType.FileRead or PipelineIntentType.FileWrite => BuildFileQuery(intent, context),
            PipelineIntentType.CodeExecution => BuildToolQuery(intent),
            PipelineIntentType.McpCall => BuildToolQuery(intent),
            _ => BuildFallbackQuery(intent, context)
        };
    }

    private static BuiltQuery BuildChatQuery(ClassifiedIntent intent)
    {
        return new BuiltQuery
        {
            Source = intent,
            InlineAnswer = intent.Source.NormalizedRequest,
            RequiresExecution = false
        };
    }

    private static BuiltQuery BuildSearchQuery(ClassifiedIntent intent, QueryBuilderContext context)
    {
        var rawQuery = intent.Source.NormalizedRequest;
        var cleanQuery = StripFiller(rawQuery);
        cleanQuery = ResolveFollowUpQuery(cleanQuery, context);

        // Inject city context for location-sensitive queries only when
        // the query doesn't already contain a specific place name.
        if (!string.IsNullOrWhiteSpace(context.UserCity)
            && IsLocationSensitive(cleanQuery)
            && !HasExplicitLocation(cleanQuery))
        {
            cleanQuery = $"{cleanQuery} in {context.UserCity}";
        }

        // Enforce bounded query length
        if (cleanQuery.Length > MaxSearchQueryLength)
        {
            cleanQuery = cleanQuery[..MaxSearchQueryLength].TrimEnd();
        }

        return new BuiltQuery
        {
            Source = intent,
            SearchQuery = cleanQuery,
            RequiresExecution = true
        };
    }

    private static BuiltQuery BuildFileQuery(ClassifiedIntent intent, QueryBuilderContext context)
    {
        var tools = new List<PipelineToolCallRequest>();

        // Extract file path if present in the request
        var filePath = ExtractFilePath(intent.Source.NormalizedRequest, context.CurrentFilePath);
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var toolName = intent.MappedType == PipelineIntentType.FileWrite ? "file_write" : "file_read";
            tools.Add(new PipelineToolCallRequest
            {
                ToolName = toolName,
                Parameters = new Dictionary<string, object> { ["path"] = filePath }
            });
        }

        return new BuiltQuery
        {
            Source = intent,
            PlannedTools = tools,
            RequiresExecution = tools.Count > 0
        };
    }

    private static BuiltQuery BuildToolQuery(ClassifiedIntent intent)
    {
        return new BuiltQuery
        {
            Source = intent,
            RequiresExecution = true
        };
    }

    private static BuiltQuery BuildFallbackQuery(ClassifiedIntent intent, QueryBuilderContext context)
    {
        // Unknown intents: if they look like search queries, build a search query
        if (intent.RouterOutput?.NeedsSearch == true || intent.RouterOutput?.NeedsWeb == true)
        {
            return BuildSearchQuery(intent, context);
        }

        return new BuiltQuery
        {
            Source = intent,
            RequiresExecution = true
        };
    }

    internal static string StripFiller(string query)
    {
        var cleaned = FillerRegex.Replace(query, "");
        cleaned = MultiWhitespaceRegex.Replace(cleaned.Trim(), " ");
        return cleaned.Trim().TrimEnd('.', '!');
    }

    internal static string ResolveFollowUpQuery(string query, QueryBuilderContext context)
    {
        if (!LooksLikeVagueFollowUpQuery(query))
            return query;

        if (!string.IsNullOrWhiteSpace(context.FollowUpAnchor))
            return context.FollowUpAnchor.Trim();

        var assistantContext = context.RecentMessages
            .LastOrDefault(m => m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            .Content;

        var anchor = ExtractAssistantAnchor(assistantContext);
        return string.IsNullOrWhiteSpace(anchor) ? query : anchor;
    }

    internal static bool IsLocationSensitive(string query)
    {
        var lower = query.ToLowerInvariant();
        return lower.Contains("weather", StringComparison.Ordinal)
            || lower.Contains("restaurant", StringComparison.Ordinal)
            || lower.Contains("store", StringComparison.Ordinal)
            || lower.Contains("near me", StringComparison.Ordinal)
            || lower.Contains("local", StringComparison.Ordinal)
            || lower.Contains("nearby", StringComparison.Ordinal)
            || lower.Contains("directions", StringComparison.Ordinal)
            || lower.Contains("traffic", StringComparison.Ordinal);
    }

    /// <summary>
    /// Detects whether the query already contains an explicit place reference
    /// (e.g. "in Seattle", "for Denver", "around Chicago").
    /// Avoids appending the user's home city when the user already specified one.
    /// </summary>
    internal static bool HasExplicitLocation(string query)
    {
        // Matches "in <City>" / "for <City>" / "around <City>" / "near <City>" patterns
        // where City starts with an uppercase letter (proper noun heuristic).
        return ExplicitLocationRegex.IsMatch(query);
    }

    internal static bool LooksLikeVagueFollowUpQuery(string query)
    {
        var lower = (query ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        if (lower is "tell me more" or "more info" or "more information" or "go deeper" or "elaborate")
            return true;

        var hasFollowUpVerb = lower.Contains("more", StringComparison.Ordinal) ||
                              lower.Contains("detail", StringComparison.Ordinal) ||
                              lower.Contains("info", StringComparison.Ordinal) ||
                              lower.Contains("deeper", StringComparison.Ordinal) ||
                              lower.Contains("elaborate", StringComparison.Ordinal) ||
                              lower.Contains("pull up", StringComparison.Ordinal) ||
                              lower.Contains("bring up", StringComparison.Ordinal);

        var hasVagueReferent = Regex.IsMatch(
            lower,
            @"\b(?:that|this|it|one|story|article|topic|place|business|bakery|restaurant|shop|store|cafe|deli|florist)\b",
            RegexOptions.CultureInvariant);

        return hasFollowUpVerb && hasVagueReferent;
    }

    internal static string ExtractAssistantAnchor(string assistantContext)
    {
        if (string.IsNullOrWhiteSpace(assistantContext))
            return "";

        var normalized = assistantContext
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Trim();

        foreach (var rawLine in normalized.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = Regex.Replace(rawLine, @"^(?:[-*]\s+|\d+[.)]\s+)", string.Empty);

            var colonIndex = line.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < line.Length - 1)
            {
                var afterColon = TrimAssistantAnchorCandidate(line[(colonIndex + 1)..]);
                if (LooksLikeConcreteAnchor(afterColon))
                    return afterColon;
            }

            var candidate = TrimAssistantAnchorCandidate(line);
            if (LooksLikeConcreteAnchor(candidate))
                return candidate;
        }

        var sentenceEnd = normalized.IndexOfAny(['.', '!', '?', '\n']);
        var fallback = sentenceEnd > 0 ? normalized[..sentenceEnd].Trim() : normalized;
        return fallback.Length > MaxSearchQueryLength ? fallback[..MaxSearchQueryLength].Trim() : fallback;
    }

    private static string TrimAssistantAnchorCandidate(string text)
    {
        var candidate = text.Trim();
        foreach (var separator in new[] { " at ", " - ", " — ", " (", "," })
        {
            var index = candidate.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                candidate = candidate[..index].Trim();
                break;
            }
        }

        return candidate.Trim(' ', '.', '!', '?', '"');
    }

    private static bool LooksLikeConcreteAnchor(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length < 4)
            return false;

        if (Regex.IsMatch(candidate, @"^(?:here|here's|summary|bottom line|in short)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;

        return Regex.IsMatch(
                   candidate,
                   @"^[A-Z][A-Za-z0-9'&.-]+(?:\s+[A-Z][A-Za-z0-9'&.-]+){1,5}$",
                   RegexOptions.CultureInvariant) ||
               Regex.IsMatch(
                   candidate,
                   @"^[A-Z][A-Za-z0-9'&.-]+(?:\s+[A-Za-z0-9'&.-]+){0,4}\s+(?:Bakery|Pastry|Cafe|Restaurant|Deli|Florist|Shop|Store)\b",
                   RegexOptions.CultureInvariant);
    }

    private static readonly Regex ExplicitLocationRegex = new(
        @"\b(?:in|for|around|near)\s+[A-Z][a-zA-Z]+(?:\s+[A-Z][a-zA-Z]+)*",
        RegexOptions.Compiled);

    internal static string ExtractFilePath(string text, string fallbackPath)
    {
        // Look for quoted paths
        var quotedMatch = Regex.Match(text, @"""([^""]+)""");
        if (quotedMatch.Success)
            return quotedMatch.Groups[1].Value;

        // Look for paths with extensions
        var pathMatch = Regex.Match(text, @"[\w./\\]+\.\w{1,10}\b");
        if (pathMatch.Success)
            return pathMatch.Value;

        return fallbackPath;
    }
}
