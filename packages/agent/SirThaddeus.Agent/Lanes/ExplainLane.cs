using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Lanes;

public sealed record ExplainRequest
{
    public required string Topic { get; init; }

    public string Goal { get; init; } = "explain";

    public string? Context { get; init; }
}

public sealed class ExplainLane
{
    private readonly ILlmClient _llm;
    private const int ExtractionMaxTokens = 120;
    private const int ExplainMaxTokens = 350;
    private const int SearchFormatMaxTokens = 220;

    public ExplainLane(ILlmClient llm)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
    }

    public async Task<ExplainRequest?> ExtractRequestAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(ExtractionPrompt),
            ChatMessage.User(userMessage)
        };

        try
        {
            var response = await _llm.ChatAsync(
                messages,
                tools: null,
                ExtractionMaxTokens,
                cancellationToken);

            return ParseExplainRequest(response.Content);
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> ExplainAsync(
        string userMessage,
        ExplainRequest request,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(ExplainPrompt),
            ChatMessage.User(BuildExplainPrompt(userMessage, request))
        };

        try
        {
            var response = await _llm.ChatAsync(
                messages,
                tools: null,
                ExplainMaxTokens,
                cancellationToken);

            return response.Content?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task<string> FormatSearchSummaryAsync(
        string userMessage,
        ExplainRequest request,
        string searchSummary,
        string? systemPromptPrefix = null,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = string.IsNullOrWhiteSpace(systemPromptPrefix)
            ? SearchFormatPrompt
            : $"{systemPromptPrefix}\n\n{SearchFormatPrompt}";

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(systemPrompt),
            ChatMessage.User(BuildSearchFormatPrompt(userMessage, request, searchSummary))
        };

        try
        {
            var response = await _llm.ChatAsync(
                messages,
                tools: null,
                SearchFormatMaxTokens,
                cancellationToken);

            return response.Content?.Trim() ?? searchSummary;
        }
        catch
        {
            return searchSummary;
        }
    }

    public static string BuildSearchQuery(ExplainRequest request)
    {
        var query = request.Topic;
        if (!string.IsNullOrWhiteSpace(request.Context))
            query += $" {request.Context}";
        return query.Trim();
    }

    public static bool NeedsClarification(ExplainRequest? request)
    {
        return request is null ||
               string.IsNullOrWhiteSpace(request.Topic) ||
               string.Equals(request.Topic, "unknown", StringComparison.OrdinalIgnoreCase) ||
               IsReferentialTopic(request.Topic);
    }

    public static string BuildClarifyingQuestion(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant();

        if (lower.Contains("page", StringComparison.Ordinal) ||
            lower.Contains("pdf", StringComparison.Ordinal) ||
            lower.Contains("document", StringComparison.Ordinal))
        {
            return "Which page or document do you want me to explain? I want to make sure I use the right context.";
        }

        if (lower.Contains("this", StringComparison.Ordinal) ||
            lower.Contains("that", StringComparison.Ordinal) ||
            lower.Contains("it", StringComparison.Ordinal))
        {
            return "What specifically do you want me to explain? A topic, page, or document name will help me answer accurately.";
        }

        return "What would you like me to explain or summarize?";
    }

    internal static ExplainRequest? ParseExplainRequest(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        try
        {
            var json = content.Trim();
            if (json.StartsWith("```", StringComparison.Ordinal))
            {
                var start = json.IndexOf('{');
                var end = json.LastIndexOf('}');
                if (start >= 0 && end > start)
                    json = json[start..(end + 1)];
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var topic = GetStringProperty(root, "Topic") ?? GetStringProperty(root, "topic");
            if (string.IsNullOrWhiteSpace(topic))
                return null;

            var goal = NormalizeGoal(
                GetStringProperty(root, "Goal") ??
                GetStringProperty(root, "goal"));
            var context = GetStringProperty(root, "Context") ?? GetStringProperty(root, "context");

            return new ExplainRequest
            {
                Topic = topic,
                Goal = goal,
                Context = context
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string BuildExplainPrompt(string userMessage, ExplainRequest request)
    {
        return $"""
            User question: {userMessage}
            Topic: {request.Topic}
            Goal: {request.Goal}
            Context: {request.Context ?? "none"}

            Answer directly and clearly.
            """;
    }

    internal static string BuildSearchFormatPrompt(
        string userMessage,
        ExplainRequest request,
        string searchSummary)
    {
        return $"""
            User question: {userMessage}
            Topic: {request.Topic}
            Goal: {request.Goal}
            Context: {request.Context ?? "none"}

            Search summary:
            {searchSummary}

            Rewrite this as a concise explanation that still preserves source grounding.
            """;
    }

    internal static bool IsReferentialTopic(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return true;

        var normalized = topic.Trim().ToLowerInvariant();
        return normalized is "this" or "that" or "it" or "this page" or "that page" or "this pdf" or "that pdf" or "this document" or "that document";
    }

    private static string? GetStringProperty(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static string NormalizeGoal(string? goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
            return "explain";

        return goal.Trim().ToLowerInvariant() switch
        {
            "summary" => "summarize",
            "summarise" => "summarize",
            "summarize" => "summarize",
            _ => "explain"
        };
    }

    private const string ExtractionPrompt =
        "Extract the main topic, goal, and optional context from the user's request. " +
        "Goal must be either 'explain' or 'summarize'. " +
        "Respond with JSON only: {\"Topic\": \"...\", \"Goal\": \"explain|summarize\", \"Context\": \"...\" or null}. " +
        "Examples:\n" +
        "\"Explain how photosynthesis works\" -> {\"Topic\": \"photosynthesis\", \"Goal\": \"explain\", \"Context\": \"how it works\"}\n" +
        "\"Summarize the Rust ownership model\" -> {\"Topic\": \"Rust ownership model\", \"Goal\": \"summarize\", \"Context\": null}\n" +
        "If the topic is ambiguous, return {\"Topic\": \"unknown\", \"Goal\": \"explain\", \"Context\": null}.";

    private const string ExplainPrompt =
        "You are handling an Explain lane request. " +
        "Answer directly, clearly, and concisely. " +
        "If the goal is summarize, lead with the gist. " +
        "If the goal is explain, define the topic first, then why it matters. " +
        "Do not mention tools, limitations, or missing access unless the prompt explicitly requires it.";

    private const string SearchFormatPrompt =
        "You are formatting a web-grounded explanation. " +
        "Keep the answer concise, readable, and faithful to the provided summary. " +
        "Preserve source grounding or caveats already present in the search summary. " +
        "Do not invent facts and do not add a preamble.";
}
