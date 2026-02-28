using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static SirThaddeus.Agent.OrchestratorMessageHelpers;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.PostProcessing;
using SirThaddeus.Agent.ConversationSegmentation;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.ToolLoop;
using SirThaddeus.Agent.Tools;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine.Formatting;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{



    /// <summary>
    /// Static tool definition used solely for search query extraction.
    /// Kept minimal so LM Studio's grammar engine compiles quickly.
    /// </summary>
    private static readonly IReadOnlyList<ToolDefinition> SearchExtractionTools =
    [
        new ToolDefinition
        {
            Function = new FunctionDefinition
            {
                Name = "web_search",
                Description = "Search the web for current information, news, or real-time data.",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["query"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] =
                                "Concise 2-6 keyword search query. " +
                                "Topic keywords ONLY — never include greetings, " +
                                "filler, or the assistant's name."
                        },
                        ["recency"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["enum"] = new[] { "day", "week", "month", "any" },
                            ["description"] =
                                "How recent the results should be. " +
                                "'day' = today/latest/breaking, " +
                                "'week' = this week, " +
                                "'month' = this month, " +
                                "'any' = no time constraint."
                        }
                    },
                    ["required"] = new[] { "query", "recency" }
                }
            }
        }
    ];

    /// <summary>
    /// LLM-assisted utility routing fallback for flexible phrasing.
    /// Deterministic regex routing remains primary; this path only runs
    /// when direct utility matching fails.
    /// </summary>
    private async Task<UtilityRouter.UtilityResult?> TryInferUtilityRouteWithLlmAsync(
        string userMessage,
        CancellationToken cancellationToken)
    {
        if (!MightBeUtilityIntent(userMessage))
            return null;

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(
                "You are a utility-intent extractor.\n" +
                "Classify whether the user wants one of: weather, time, holiday, feed, status, calculator, conversion, letter_count, or none.\n" +
                "Return ONLY JSON with this schema:\n" +
                "{ \"category\": \"weather|time|holiday|feed|status|calculator|conversion|letter_count|none\", \"canonicalMessage\": \"...\", \"confidence\": 0.0 }\n" +
                "Rules:\n" +
                "- canonicalMessage must be a short plain-English request usable by a deterministic parser\n" +
                "- Do not invent locations, numbers, or units\n" +
                "- If uncertain, return category=none and confidence <= 0.5\n" +
                "- Return JSON only."),
            ChatMessage.User(userMessage)
        };

        try
        {
            var response = await _llm.ChatAsync(
                messages, tools: null, MaxTokensUtilityRouting, cancellationToken);

            var raw = StripCodeFenceWrapper((response.Content ?? "").Trim());
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var category = root.TryGetProperty("category", out var c)
                ? (c.GetString() ?? "").Trim().ToLowerInvariant()
                : "";
            var canonical = root.TryGetProperty("canonicalMessage", out var m)
                ? (m.GetString() ?? "").Trim()
                : "";
            var confidence = root.TryGetProperty("confidence", out var conf) &&
                             conf.TryGetDouble(out var parsedConfidence)
                ? parsedConfidence
                : 0.0;

            if (category == "none" || confidence < 0.65)
                return null;

            if (string.IsNullOrWhiteSpace(canonical))
                return null;

            if (!SharesMeaningfulToken(userMessage, canonical))
                return null;

            var routed = UtilityRouter.TryHandle(canonical, UserLocationHint, PreferredUnits);
            if (routed is null)
                return null;

            LogEvent("UTILITY_LLM_ROUTE",
                $"category={category}, confidence={confidence:F2}, canonical=\"{canonical}\"");
            return routed;
        }
        catch
        {
            return null;
        }
    }

}
