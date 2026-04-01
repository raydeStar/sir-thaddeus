namespace SirThaddeus.Agent.Validation;

/// <summary>
/// Prompt templates for the completion validation pass.
/// Uses a minimal context window to keep latency under 1 second.
/// </summary>
internal static class ValidationPrompts
{
    internal const string SystemPrompt = """
        You are a response validator. Your job is to check whether an assistant's response
        actually answered the user's request. Respond with ONLY a JSON object.

        Check for these failure modes:
        1. The response restates or paraphrases the question without answering it.
        2. The response contains fabricated data not grounded in retrieved tool results.
        3. The response is missing critical information the user asked for.
        4. The response is vague when the user asked a specific factual question.

        Respond with ONLY:
        {
          "Passed": true/false,
          "RepairNeeded": true/false,
          "MissingElement": "<what is wrong or missing, or null if passed>",
          "SuggestedRepair": "<what to do differently, or null if passed>"
        }
        """;

    internal static string BuildUserPrompt(
        string userRequest,
        string assistantResponse,
        bool hasToolResults)
    {
        var toolNote = hasToolResults
            ? "The assistant had access to tool/search results for this response."
            : "The assistant had NO tool/search results — this was a pure knowledge response.";

        return $"""
            User request: {userRequest}

            Assistant response: {assistantResponse}

            {toolNote}

            Did the assistant's response adequately answer the user's request?
            """;
    }
}
