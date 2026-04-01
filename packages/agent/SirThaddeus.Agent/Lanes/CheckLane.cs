using System.Diagnostics;
using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Lanes;

/// <summary>
/// Extracted entity/attribute pair from a user query.
/// </summary>
public sealed record EntityExtraction
{
    /// <summary>The entity being asked about (e.g., "Target").</summary>
    public required string Entity { get; init; }

    /// <summary>The attribute being queried (e.g., "opening hours").</summary>
    public required string Attribute { get; init; }

    /// <summary>Optional qualifier (e.g., "on Sundays", "near me").</summary>
    public string? Qualifier { get; init; }
}

/// <summary>
/// Result of a Check Lane execution.
/// </summary>
public sealed record CheckLaneResult
{
    /// <summary>True if the lane produced a valid answer.</summary>
    public required bool Answered { get; init; }

    /// <summary>The formatted response text (answer + source + optional caveat).</summary>
    public string? ResponseText { get; init; }

    /// <summary>True if a clarifying question was asked instead of an answer.</summary>
    public bool AskedClarification { get; init; }

    /// <summary>Wall-clock milliseconds from start to finish.</summary>
    public double ElapsedMs { get; init; }

    /// <summary>The entity extraction (null if entity couldn't be extracted).</summary>
    public EntityExtraction? Extraction { get; init; }
}

/// <summary>
/// Fast-path executor for the Lookup lane. Extracts entity + attribute,
/// runs a focused web search, and returns a sourced answer with optional
/// confidence caveat. Target: &lt; 3 seconds end-to-end.
/// </summary>
public sealed class CheckLane
{
    private readonly ILlmClient _llm;
    private const int ExtractionMaxTokens = 100;
    private const int FormatMaxTokens = 200;

    public CheckLane(ILlmClient llm)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
    }

    /// <summary>
    /// Extracts entity, attribute, and qualifier from the user's query.
    /// Returns null if the query doesn't contain an identifiable entity.
    /// </summary>
    public async Task<EntityExtraction?> ExtractEntityAsync(
        string userMessage, CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(EntityExtractionPrompt),
            ChatMessage.User(userMessage)
        };

        try
        {
            var response = await _llm.ChatAsync(
                messages, tools: null, ExtractionMaxTokens, cancellationToken);

            return ParseEntityExtraction(response.Content);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Formats a final Check Lane response from search results.
    /// Returns a single-sentence answer with source citation and optional caveat.
    /// </summary>
    public async Task<string> FormatResponseAsync(
        string userMessage,
        EntityExtraction extraction,
        string searchResultsSummary,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildFormatPrompt(userMessage, extraction, searchResultsSummary);

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(FormatResponsePrompt),
            ChatMessage.User(prompt)
        };

        try
        {
            var response = await _llm.ChatAsync(
                messages, tools: null, FormatMaxTokens, cancellationToken);

            return response.Content?.Trim() ?? searchResultsSummary;
        }
        catch
        {
            // Fallback: return the raw search summary.
            return searchResultsSummary;
        }
    }

    /// <summary>
    /// Builds the search query from the entity extraction.
    /// </summary>
    public static string BuildSearchQuery(EntityExtraction extraction)
    {
        var query = $"{extraction.Entity} {extraction.Attribute}";
        if (!string.IsNullOrWhiteSpace(extraction.Qualifier))
            query += $" {extraction.Qualifier}";
        return query.Trim();
    }

    /// <summary>
    /// Determines if the user's query needs a clarifying question
    /// because the entity is missing or unclear.
    /// </summary>
    public static bool NeedsClarification(EntityExtraction? extraction)
    {
        return extraction is null ||
               string.IsNullOrWhiteSpace(extraction.Entity) ||
               string.Equals(extraction.Entity, "unknown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Generates a clarifying question when the entity can't be determined.
    /// </summary>
    public static string BuildClarifyingQuestion(string userMessage)
    {
        // Detect common ambiguous referents.
        var lower = userMessage.ToLowerInvariant();

        if (lower.Contains("that place") || lower.Contains("the place") ||
            lower.Contains("that store") || lower.Contains("the store"))
            return "Which place are you referring to? I want to make sure I look up the right one.";

        if (lower.Contains("that thing") || lower.Contains("the thing") ||
            lower.Contains("that product") || lower.Contains("the product"))
            return "Which product are you asking about? I want to give you the right answer.";

        return "Could you be more specific about what you'd like me to look up? " +
               "I want to make sure I find the right information for you.";
    }

    // ── Entity extraction parsing ────────────────────────────────────

    internal static EntityExtraction? ParseEntityExtraction(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        try
        {
            var json = content.Trim();

            // Strip markdown code fences.
            if (json.StartsWith("```", StringComparison.Ordinal))
            {
                var start = json.IndexOf('{');
                var end = json.LastIndexOf('}');
                if (start >= 0 && end > start)
                    json = json[start..(end + 1)];
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var entity = GetStringProperty(root, "Entity") ??
                         GetStringProperty(root, "entity");
            var attribute = GetStringProperty(root, "Attribute") ??
                            GetStringProperty(root, "attribute");

            if (string.IsNullOrWhiteSpace(entity) || string.IsNullOrWhiteSpace(attribute))
                return null;

            var qualifier = GetStringProperty(root, "Qualifier") ??
                            GetStringProperty(root, "qualifier");

            return new EntityExtraction
            {
                Entity = entity,
                Attribute = attribute,
                Qualifier = qualifier
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetStringProperty(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    // ── Prompts ──────────────────────────────────────────────────────

    private const string EntityExtractionPrompt =
        "Extract the entity, attribute, and optional qualifier from the user's question. " +
        "Respond with JSON only: {\"Entity\": \"...\", \"Attribute\": \"...\", \"Qualifier\": \"...\" or null}. " +
        "Examples:\n" +
        "\"When does Target open on Sundays?\" → {\"Entity\": \"Target\", \"Attribute\": \"opening hours\", \"Qualifier\": \"on Sundays\"}\n" +
        "\"What's the return policy at Costco?\" → {\"Entity\": \"Costco\", \"Attribute\": \"return policy\", \"Qualifier\": null}\n" +
        "\"How much does a Big Mac cost?\" → {\"Entity\": \"Big Mac\", \"Attribute\": \"price\", \"Qualifier\": null}\n" +
        "If the entity is ambiguous or missing, return {\"Entity\": \"unknown\", \"Attribute\": \"unknown\", \"Qualifier\": null}.";

    private const string FormatResponsePrompt =
        "You are formatting a quick fact-check answer. Rules:\n" +
        "1. Single sentence answer with the key fact.\n" +
        "2. Add 'Source: [source name]' at the end.\n" +
        "3. If the data is conflicting or uncertain, add a brief caveat.\n" +
        "4. If the data mentions dates, note 'As of [date]' when available.\n" +
        "5. If the answer varies by location/time/context, mention that briefly.\n" +
        "Do NOT add any preamble. Just the formatted answer.";

    internal static string BuildFormatPrompt(
        string userMessage,
        EntityExtraction extraction,
        string searchResults)
    {
        return $"""
            User question: {userMessage}
            Entity: {extraction.Entity}
            Attribute: {extraction.Attribute}
            Qualifier: {extraction.Qualifier ?? "none"}

            Search results summary:
            {searchResults}

            Format this into a concise, sourced answer.
            """;
    }
}
