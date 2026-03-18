using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Search;

/// <summary>
/// Builds a best-effort answer when live web retrieval is unavailable.
/// Keeps uncertainty explicit and avoids fake citations.
/// </summary>
internal static partial class OfflineWebReasoningResponder
{
    private const int MaxTokensOfflineAnswer = 1024;
    private const string OfflineReasoningInstruction =
        "\n\nLive web tools are unavailable for this turn. " +
        "Answer using general knowledge and careful reasoning only. " +
        "Be explicit about uncertainty for time-sensitive facts. " +
        "Do not claim you just searched the web. " +
        "Do not invent citations, links, or exact current values.";

    public static async Task<AgentResponse> BuildAsync(
        ILlmClient llm,
        string systemPrompt,
        string userMessage,
        string memoryPackText,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        string failureReason,
        CancellationToken cancellationToken)
    {
        var prefix = BuildPrefix(failureReason);
        var messages = BuildMessages(systemPrompt, memoryPackText, history, userMessage, failureReason);

        try
        {
            var response = await llm.ChatAsync(
                messages,
                tools: null,
                maxTokensOverride: MaxTokensOfflineAnswer,
                cancellationToken);

            var answer = CleanModelText(response.Content ?? "");
            if (string.IsNullOrWhiteSpace(answer))
                answer = BuildDeterministicFallback(userMessage);
            answer = Truncate(answer, 1800);
            var finalText = Truncate($"{prefix}\n\n{answer}".Trim(), 2000);

            return new AgentResponse
            {
                Text = finalText,
                Success = true,
                ToolCallsMade = toolCallsMade.ToList(),
                LlmRoundTrips = 1
            };
        }
        catch
        {
            var finalText = Truncate(
                $"{prefix}\n\n{BuildDeterministicFallback(userMessage)}".Trim(),
                2000);

            return new AgentResponse
            {
                Text = finalText,
                Success = true,
                ToolCallsMade = toolCallsMade.ToList(),
                LlmRoundTrips = 0
            };
        }
    }

    private static List<ChatMessage> BuildMessages(
        string systemPrompt,
        string memoryPackText,
        IReadOnlyList<ChatMessage> history,
        string userMessage,
        string failureReason)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(
                systemPrompt +
                SearchOrchestrator.CombineMemoryAndInstruction(memoryPackText,
                    OfflineReasoningInstruction +
                    $"\n\nWeb lookup status: {failureReason}"))
        };

        // Keep only a short tail so local models do not run out of room.
        var historyTail = history
            .Where(m => m.Role is "user" or "assistant")
            .TakeLast(4)
            .ToList();
        messages.AddRange(historyTail);

        var tailEndsWithCurrentUserTurn =
            historyTail.Count > 0 &&
            historyTail[^1].Role == "user" &&
            string.Equals(historyTail[^1].Content, userMessage, StringComparison.Ordinal);

        if (!tailEndsWithCurrentUserTurn)
            messages.Add(ChatMessage.User(userMessage));

        return messages;
    }

    private static string BuildPrefix(string failureReason)
    {
        var reason = string.IsNullOrWhiteSpace(failureReason)
            ? "web lookup did not return usable results"
            : failureReason.Trim().TrimEnd('.');

        return $"Live web lookup is unavailable right now ({reason}). " +
               "Here is a best-effort answer from built-in reasoning:";
    }

    private static string BuildDeterministicFallback(string userMessage)
    {
        return "I cannot verify live web facts for this question right now, " +
               "so any answer may be incomplete or out of date. " +
               $"Based on general knowledge, the request is: \"{userMessage}\". " +
               "If you want, I can still reason through likely possibilities step by step.";
    }

    private static string CleanModelText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var cleaned = text
            .Replace("<|im_end|>", "", StringComparison.Ordinal)
            .Replace("<|endoftext|>", "", StringComparison.Ordinal)
            .Replace("[/INST]", "", StringComparison.Ordinal)
            .Replace("[INST]", "", StringComparison.Ordinal)
            .Replace("</s>", "", StringComparison.Ordinal)
            .Replace("<s>", "", StringComparison.Ordinal)
            .Trim();

        var stopMarkers = new[]
        {
            "\nUser:",
            "\nuser:",
            "\nHuman:",
            "\nhuman:",
            "\n### User",
            "\n### Human",
            "\n<|channel|>",
            "\n<|message|>",
            "\n<|constrain|>"
        };

        foreach (var marker in stopMarkers)
        {
            var idx = cleaned.IndexOf(marker, StringComparison.Ordinal);
            if (idx > 0)
                cleaned = cleaned[..idx];
        }

        return cleaned.Trim();
    }

    private static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxChars)
            return text;

        var window = text[..maxChars];
        var lastSentence = Math.Max(
            Math.Max(window.LastIndexOf(". ", StringComparison.Ordinal), window.LastIndexOf("? ", StringComparison.Ordinal)),
            window.LastIndexOf("! ", StringComparison.Ordinal));

        if (lastSentence > maxChars / 2)
            return text[..(lastSentence + 1)].Trim();

        var lastSpace = window.LastIndexOf(' ');
        if (lastSpace > maxChars / 2)
            return text[..lastSpace].Trim() + "...";

        return window.Trim() + "...";
    }
}
