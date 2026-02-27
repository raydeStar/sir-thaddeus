using System.Text.Json;
using System.Text.Json.Serialization;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Orchestration;

/// <summary>
/// A fallback classifier that uses an LLM to generate a strict JSON IntentDecisionV2.
/// </summary>
public sealed class LlmIntentClassifier
{
    private readonly ILlmClient _llm;
    private readonly JsonSerializerOptions _jsonOptions;

    public LlmIntentClassifier(ILlmClient llm)
    {
        _llm = llm;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    private const string SystemPrompt = @"You are the intent router for the Sir Thaddeus AI agent.
Your ONLY job is to classify the user's message and extract required slots.
You must return a valid JSON object matching this schema:

{
  ""Intent"": ""(LookupFact | LookupNews | LookupDeepDive | MemoryWrite | FileTask | ScreenObserve | SystemExecute | ChatOnly | GeneralTool)"",
  ""Confidence"": 0.0 to 1.0,
  ""RequiresClarification"": true or false,
  ""ClarificationQuestion"": ""A question to ask the user if you are unsure"",
  ""Slots"": { ... specific slot object ... }
}

Slot schemas by intent:
- LookupFact, LookupNews, LookupDeepDive: { ""type"": ""search"", ""Query"": ""..."" }
- MemoryWrite: { ""type"": ""memory_write"", ""Fact"": ""..."" }
- FileTask, ScreenObserve: { ""type"": ""open_entity"", ""EntityType"": ""..."", ""EntityIdOrName"": ""..."" }

If you cannot extract the required slots, set RequiresClarification to true and provide a ClarificationQuestion.
Output ONLY JSON. No markdown formatting, no explanations.";

    public async Task<IntentDecisionV2> ClassifyAsync(string userMessage, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(SystemPrompt),
            ChatMessage.User(userMessage)
        };

        var response = await _llm.ChatAsync(messages, tools: null, cancellationToken: cancellationToken);
        var content = response.Content?.Trim() ?? "{}";

        // Strip markdown if the LLM ignored instructions
        if (content.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            content = content[7..];
            if (content.EndsWith("```")) content = content[..^3];
            content = content.Trim();
        }
        else if (content.StartsWith("```", StringComparison.Ordinal))
        {
            content = content[3..];
            if (content.EndsWith("```")) content = content[..^3];
            content = content.Trim();
        }

        try
        {
            var decision = JsonSerializer.Deserialize<IntentDecisionV2>(content, _jsonOptions);
            return decision ?? new IntentDecisionV2 { Intent = "GeneralTool", Confidence = 0.3 };
        }
        catch (JsonException)
        {
            // Fallback if LLM fails to output valid JSON
            return new IntentDecisionV2
            {
                Intent = "GeneralTool",
                Confidence = 0.1,
                RequiresClarification = true,
                ClarificationQuestion = "I didn't quite catch that. Could you rephrase what you need?"
            };
        }
    }
}
