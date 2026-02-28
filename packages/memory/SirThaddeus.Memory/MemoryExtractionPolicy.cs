// SirThaddeus.Memory/MemoryExtractionPolicy.cs
namespace SirThaddeus.Memory;

/// <summary>
/// Defines rules for what should be extracted into long-term memory.
/// Used by the AutoMemoryExtractor and consolidation background jobs
/// to ensure safety contracts are upheld.
/// </summary>
public static class MemoryExtractionPolicy
{
    /// <summary>
    /// LLM prompt directives that enforce the memory write policy during passive auto-extraction.
    /// This ensures the model knows what to ignore.
    /// </summary>
    public const string ExtractionGuardrails = """
        MEMORY WRITE POLICY (CRITICAL):
        1. ONLY extract facts and preferences explicitly stated by the USER.
        2. DO NOT extract facts about the world, trivia, or general knowledge unless the user specifically asks you to remember them.
        3. DO NOT extract information from tool outputs (e.g., web search results, file reads) to avoid storing third-party PII or hallucinated facts.
        4. ONLY extract information that is persistent and relevant across sessions (e.g., names, relationships, project goals, tech stack preferences).
        5. DO NOT extract ephemeral state (e.g., "The user is currently debugging line 42").
        """;

    /// <summary>
    /// Checks if a text source is eligible for extraction based on length or content heuristics,
    /// before even spending tokens on the LLM.
    /// </summary>
    public static bool IsEligibleForExtraction(string role, string content)
    {
        // We primarily want to extract from user inputs.
        // We can extract from assistant responses only if they synthesize a user goal,
        // but typically passive extraction runs only on User messages.
        if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        // Trivial filter: extremely short messages rarely contain long-term facts.
        // E.g., "ok", "yes", "thanks". (Arbitrary threshold for cost savings).
        if (content.Trim().Length < 5)
        {
            return false;
        }

        return true;
    }
}
