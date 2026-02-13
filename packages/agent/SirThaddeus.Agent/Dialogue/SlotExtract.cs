using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.Agent.Search;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Dialogue;

public sealed record ExtractedSlots
{
    public string Intent { get; init; } = "none";
    public string? Topic { get; init; }
    public string? LocationText { get; init; }
    public string? TimeScope { get; init; }
    public bool ExplicitLocationChange { get; init; }
    public bool RefersToPriorLocation { get; init; }
    public string RawMessage { get; init; } = "";
}

/// <summary>
/// Extracts intent and continuity slots from a single user turn.
/// LLM extraction is strict JSON with one retry; on failure, deterministic fallback is used.
/// </summary>
public sealed class SlotExtract
{
    private readonly ILlmClient _llm;
    private readonly IAuditLogger _audit;

    private const int MaxTokens = 180;

    private static readonly Regex ScopedLocationRegex = new(
        @"\b(?:in|at|for|near)\s+(?<location>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TemporalOnlyRegex = new(
        @"^(?:for\s+)?(?:today|tomorrow|tonight|now|right now|currently|this\s+(?:morning|afternoon|evening|week|weekend)|last\s+(?:week|month)|next\s+week|yesterday)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WordSplitRegex = new(
        @"[^a-z]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public SlotExtract(ILlmClient llm, IAuditLogger audit)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async Task<ExtractedSlots> RunAsync(
        string userMessage,
        DialogueState currentState,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return new ExtractedSlots { RawMessage = userMessage ?? "" };

        var heuristic = ExtractHeuristically(userMessage, currentState);
        if (ShouldUseHeuristicOnly(heuristic))
            return heuristic;

        var fromLlm = await TryExtractWithRetryAsync(userMessage, currentState, cancellationToken);
        if (fromLlm is null)
            return heuristic;

        return MergeWithFallback(fromLlm, heuristic, userMessage);
    }

    private async Task<ExtractedSlots?> TryExtractWithRetryAsync(
        string userMessage,
        DialogueState currentState,
        CancellationToken cancellationToken)
    {
        var first = await TryExtractOnceAsync(userMessage, currentState, strict: false, cancellationToken);
        if (first is not null)
            return first;

        return await TryExtractOnceAsync(userMessage, currentState, strict: true, cancellationToken);
    }

    private async Task<ExtractedSlots?> TryExtractOnceAsync(
        string userMessage,
        DialogueState currentState,
        bool strict,
        CancellationToken cancellationToken)
    {
        try
        {
            var system =
                "You extract continuity slots for a local assistant. " +
                "Return STRICT JSON only with keys: " +
                "intent, topic, locationText, timeScope, explicitLocationChange, refersToPriorLocation. " +
                "intent must be one of: weather,time,holiday,feed,status,calculator,conversion,fact,text,news,search,chat,none. " +
                "Use null for unknown values. No markdown.";

            if (strict)
                system += " Retry mode: output JSON object only. No prose, no code fences.";

            var memoryHint = string.IsNullOrWhiteSpace(currentState.LocationName)
                ? "none"
                : LocationContextHeuristics.IsClearlyNonPlace(currentState.LocationName)
                    ? "none"
                    : currentState.LocationName;

            var messages = new List<ChatMessage>
            {
                ChatMessage.System(system),
                ChatMessage.User(
                    $"currentLocation={memoryHint}\n" +
                    $"message={userMessage}")
            };

            var response = await _llm.ChatAsync(messages, tools: null, maxTokensOverride: MaxTokens, cancellationToken);
            var raw = response.Content;
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var cleaned = StripCodeFence(raw);
            if (!TryParse(cleaned, out var parsed))
                return null;

            return parsed;
        }
        catch (Exception ex)
        {
            _audit.Append(new AuditEvent
            {
                Actor = "agent",
                Action = "DIALOGUE_SLOT_EXTRACT_FAIL",
                Result = "error",
                Details = new Dictionary<string, object>
                {
                    ["strict"] = strict,
                    ["error"] = ex.Message
                }
            });
            return null;
        }
    }

    private static string StripCodeFence(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstBreak = trimmed.IndexOf('\n');
        if (firstBreak < 0)
            return trimmed.Trim('`', ' ');

        var inner = trimmed[(firstBreak + 1)..];
        var closing = inner.LastIndexOf("```", StringComparison.Ordinal);
        if (closing >= 0)
            inner = inner[..closing];

        return inner.Trim();
    }

    private static bool TryParse(string json, out ExtractedSlots slots)
    {
        slots = new ExtractedSlots();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var intent = ReadString(root, "intent")?.ToLowerInvariant() ?? "none";
            if (!IsAllowedIntent(intent))
                intent = "none";

            // Reject location values that are structurally impossible
            // geographic names — small LLMs frequently echo message
            // fragments into locationText by accident.
            var rawLocation = NormalizeOptionalValue(ReadString(root, "locationText"));
            if (LocationContextHeuristics.IsClearlyNonPlace(rawLocation))
                rawLocation = null;

            slots = new ExtractedSlots
            {
                Intent = intent,
                Topic = NormalizeOptionalValue(ReadString(root, "topic")),
                LocationText = rawLocation,
                TimeScope = NormalizeOptionalValue(ReadString(root, "timeScope")),
                ExplicitLocationChange = ReadBool(root, "explicitLocationChange"),
                RefersToPriorLocation = ReadBool(root, "refersToPriorLocation")
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAllowedIntent(string intent) => intent is
        "weather" or "time" or "holiday" or "feed" or "status" or
        "calculator" or "conversion" or "fact" or "text" or
        "news" or "search" or "chat" or "none";

    private static string? ReadString(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var node))
            return null;
        if (node.ValueKind == JsonValueKind.Null)
            return null;
        if (node.ValueKind != JsonValueKind.String)
            return null;
        return node.GetString();
    }

    private static bool ReadBool(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var node))
            return false;
        return node.ValueKind == JsonValueKind.True;
    }

    private static ExtractedSlots ExtractHeuristically(string userMessage, DialogueState currentState)
    {
        var trimmed = userMessage.Trim();
        var lower = trimmed.ToLowerInvariant();

        var utility = UtilityRouter.TryHandle(trimmed);
        var intent = utility?.Category switch
        {
            "weather" => "weather",
            "time" => "time",
            "holiday" => "holiday",
            "feed" => "feed",
            "status" => "status",
            "calculator" => "calculator",
            "conversion" => "conversion",
            "fact" => "fact",
            "text" => "text",
            _ => SearchModeRouter.LooksLikeNewsIntent(lower) ? "news" : "none"
        };

        var location = TryExtractLocation(trimmed);
        var explicitLocationChange =
            !string.IsNullOrWhiteSpace(location) &&
            !TemporalOnlyRegex.IsMatch(location);

        var refersToPrior =
            lower.Contains(" there", StringComparison.Ordinal) ||
            lower.StartsWith("there", StringComparison.Ordinal) ||
            lower.Contains("same place", StringComparison.Ordinal) ||
            lower.Contains("that city", StringComparison.Ordinal) ||
            lower.Contains("that location", StringComparison.Ordinal);

        var timeScope = ExtractTimeScope(lower);

        // Keep topic null when intent is unknown so merge logic can
        // carry forward the last canonical topic deterministically.
        var topic = intent == "none"
            ? null
            : TrimOrNull(intent);

        return new ExtractedSlots
        {
            Intent = intent,
            Topic = topic,
            LocationText = NormalizeOptionalValue(location),
            TimeScope = NormalizeOptionalValue(timeScope),
            ExplicitLocationChange = explicitLocationChange,
            RefersToPriorLocation = refersToPrior ||
                (string.IsNullOrWhiteSpace(location) &&
                 !string.IsNullOrWhiteSpace(currentState.LocationName) &&
                 !LocationContextHeuristics.IsClearlyNonPlace(currentState.LocationName) &&
                 !string.IsNullOrWhiteSpace(timeScope)),
            RawMessage = trimmed
        };
    }

    private static string? TryExtractLocation(string message)
    {
        var match = ScopedLocationRegex.Match(message);
        if (!match.Success)
            return null;

        var location = match.Groups["location"].Value
            .Trim()
            .TrimEnd('.', ',', '!', '?');

        return string.IsNullOrWhiteSpace(location) ? null : location;
    }

    private static string? ExtractTimeScope(string lower)
    {
        var words = WordSplitRegex.Split(lower)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim())
            .ToArray();

        static bool HasWord(string[] source, string word)
            => source.Any(w => w.Equals(word, StringComparison.Ordinal));

        static bool HasPair(string[] source, string first, string second)
        {
            for (var i = 0; i < source.Length - 1; i++)
            {
                if (source[i].Equals(first, StringComparison.Ordinal) &&
                    source[i + 1].Equals(second, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        if (HasWord(words, "today"))
            return "today";
        if (HasWord(words, "tomorrow"))
            return "tomorrow";
        if (HasWord(words, "tonight"))
            return "tonight";
        if (HasPair(words, "this", "week"))
            return "this week";
        if (HasPair(words, "last", "week"))
            return "last week";
        if (HasPair(words, "next", "week"))
            return "next week";
        if (HasWord(words, "now") || HasWord(words, "currently"))
            return "now";

        return null;
    }

    private static ExtractedSlots MergeWithFallback(
        ExtractedSlots primary,
        ExtractedSlots fallback,
        string rawMessage)
    {
        return new ExtractedSlots
        {
            Intent = IsAllowedIntent(primary.Intent) ? primary.Intent : fallback.Intent,
            Topic = NormalizeOptionalValue(primary.Topic) ?? fallback.Topic,
            LocationText = NormalizeOptionalValue(primary.LocationText) ?? fallback.LocationText,
            TimeScope = NormalizeOptionalValue(primary.TimeScope) ?? fallback.TimeScope,
            ExplicitLocationChange = primary.ExplicitLocationChange || fallback.ExplicitLocationChange,
            RefersToPriorLocation = primary.RefersToPriorLocation || fallback.RefersToPriorLocation,
            RawMessage = rawMessage.Trim()
        };
    }

    private static string? TrimOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        var trimmed = TrimOrNull(value);
        if (trimmed is null)
            return null;

        var token = trimmed.Trim().TrimEnd('.', ',', '!', '?');
        return IsNoneLikeLiteral(token) ? null : trimmed;
    }

    private static bool IsNoneLikeLiteral(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        return value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("null", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("na", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("(none)", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldUseHeuristicOnly(ExtractedSlots slots)
    {
        if (!string.Equals(slots.Intent, "none", StringComparison.OrdinalIgnoreCase))
            return true;

        if (slots.RefersToPriorLocation)
            return true;

        return !string.IsNullOrWhiteSpace(slots.TimeScope);
    }
}
