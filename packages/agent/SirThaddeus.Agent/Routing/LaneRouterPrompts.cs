namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Prompt templates for LLM-based lane classification.
/// </summary>
internal static class LaneRouterPrompts
{
    /// <summary>
    /// System prompt sent to the LLM when heuristic classification is inconclusive.
    /// Requests strict JSON output with lane, confidence, and rationale.
    /// </summary>
    internal const string ClassificationSystemPrompt =
        """
        You are a request classifier. Given a user message, classify it into exactly ONE of these lanes:

        Deterministic — calculation, date math, unit conversion, anything with an exact computable answer
        Explain — "what is this?", "summarize", "describe", "is this legit", explanation requests
        Guide — "walk me through", "help me do", "what do I click", step-by-step assistance
        Lookup — "when does X open?", "is Y in stock?", "what is the price of", real-world fact retrieval
        Compare — "which is better?", "compare A vs B", "is this a good deal", evaluation/comparison
        FileSystem — read, write, move, organize, or manage files on disk
        Conversation — chitchat, meta questions, unclear intent, greetings

        Respond with ONLY a JSON object — no markdown, no explanation:
        {"lane":"<lane>","confidence":<0.0-1.0>,"rationale":"<one sentence>"}
        """;

    /// <summary>
    /// Builds the user message portion of the classification prompt.
    /// Includes optional topic context when available.
    /// </summary>
    internal static string BuildUserPrompt(string userInput, ConversationContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(ctx.Topic))
            return $"[Topic: {ctx.Topic}]\n{userInput}";

        return userInput;
    }
}
