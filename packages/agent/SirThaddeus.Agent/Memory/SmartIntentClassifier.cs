using System.Text.Json;
using System.Text.Json.Serialization;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Memory;

public enum MemoryIntentDecision
{
    Inject,
    Suppress,
    Unsure
}

public interface ISmartIntentClassifier
{
    Task<MemoryIntentDecision> ClassifyAsync(string userMessage, CancellationToken ct = default);
}

public sealed class SmartIntentClassifier : ISmartIntentClassifier
{
    private readonly ILlmClient _llm;

    private static readonly string SystemPrompt = """
        You are a memory gating classifier. Your job is to decide whether retrieving the user's personal memories and profile will improve the quality of the answer to their request.

        RULES:
        1. If the user asks about themself (e.g., "what's my name", "what do I like"), or uses personal pronouns indicating they want personalized advice, output "Inject".
        2. If the user asks a purely technical, factual, or general question where personal preferences are irrelevant (e.g., "how to fix this bug", "what is the capital of France"), output "Suppress".
        3. If you do not have a strong signal either way, output "Unsure".
        4. Would personalization improve answer quality?
        
        Respond ONLY with a JSON object in this exact format:
        {
            "decision": "Inject" | "Suppress" | "Unsure"
        }
        """;

    public SmartIntentClassifier(ILlmClient llm)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
    }

    public async Task<MemoryIntentDecision> ClassifyAsync(string userMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return MemoryIntentDecision.Unsure;

        try
        {
            var result = await _llm.ChatAsync(
                new[]
                {
                    ChatMessage.System(SystemPrompt),
                    ChatMessage.User(userMessage)
                },
                tools: null,
                maxTokensOverride: 20,
                cancellationToken: ct);

            if (string.IsNullOrWhiteSpace(result.Content))
                return MemoryIntentDecision.Unsure;

            var cleaned = result.Content.Trim();
            if (cleaned.StartsWith("```json"))
            {
                cleaned = cleaned.Substring(7);
                if (cleaned.EndsWith("```"))
                    cleaned = cleaned.Substring(0, cleaned.Length - 3);
            }

            var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.TryGetProperty("decision", out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var val = prop.GetString();
                if (Enum.TryParse<MemoryIntentDecision>(val, ignoreCase: true, out var dec))
                    return dec;
            }
        }
        catch
        {
            // Fallback gracefully to unsure on any parsing/LLM errors
        }

        return MemoryIntentDecision.Unsure;
    }
}
