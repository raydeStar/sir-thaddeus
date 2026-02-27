using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Orchestration;

public sealed class PlanValidator : IPlanValidator
{
    // High-risk domain mapping heuristic to detect when the LLM tries to jump domains 
    // without the user asking (e.g. asking to search, but LLM tries to write files).
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "web_search", "web_search_news", "read_website", "read_website_news",
        "places_lookup", "weather_lookup", "time_lookup", "math_eval", "unit_convert",
        "distance_lookup", "status_lookup", "holiday_lookup", "memory_retrieve", "file_read",
        "screen_observe"
    };

    public ValidationResult Validate(
        IntentDecisionV2 decision,
        IReadOnlyList<ProposedToolCall> proposedCalls,
        IReadOnlyList<ToolDefinition> allowedTools)
    {
        if (proposedCalls.Count == 0)
        {
            return new ValidationResult(true);
        }

        if (proposedCalls.Count > 5)
        {
            return new ValidationResult(
                false, 
                "budget_exceeded", 
                $"You requested {proposedCalls.Count} tool calls, but the maximum allowed in a single turn is 5. Please select the most important tools and try again.");
        }

        foreach (var call in proposedCalls)
        {
            // 1. Compatibility Check (Policy Mismatch)
            var allowedTool = allowedTools.FirstOrDefault(t => t.Function.Name.Equals(call.ToolName, StringComparison.OrdinalIgnoreCase));
            if (allowedTool == null)
            {
                var allowedNames = string.Join(", ", allowedTools.Select(t => t.Function.Name));
                return new ValidationResult(
                    false, 
                    "policy_mismatch",
                    $"Tool '{call.ToolName}' is not allowed for the current intent '{decision.Intent}'. Allowed tools are: [{allowedNames}]. Please revise your plan.");
            }

            // 2. Domain Jump Check
            // If the intent is strictly read-only or low risk, reject any tool not in the read-only allowlist.
            if (IsLowRiskIntent(decision.Intent) && !ReadOnlyTools.Contains(call.ToolName))
            {
                return new ValidationResult(
                    false,
                    "domain_jump",
                    $"Tool '{call.ToolName}' modifies state or is considered high-risk, but the user's intent '{decision.Intent}' implies a read-only request. Do not perform this action.");
            }

            // 3. Required Slots Check
            if (decision.Slots is IntentSlots.SearchSlots searchSlots && 
                call.ToolName.Contains("search", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var doc = JsonDocument.Parse(call.ArgumentsJson);
                    if (doc.RootElement.TryGetProperty("query", out var queryProp))
                    {
                        var toolQuery = queryProp.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(searchSlots.Query) && 
                            !toolQuery.Contains(searchSlots.Query, StringComparison.OrdinalIgnoreCase))
                        {
                            // Soft warning, we don't necessarily hard-fail here but it's a good place to enforce slot-mapping if we want to be strict.
                        }
                    }
                    else
                    {
                        return new ValidationResult(false, "missing_slot", "The search tool requires a 'query' parameter based on the user's intent, but it was missing.");
                    }
                }
                catch (JsonException)
                {
                    return new ValidationResult(false, "invalid_json", "Failed to parse tool arguments JSON. Please output valid JSON.");
                }
            }
        }

        return new ValidationResult(true);
    }

    private static bool IsLowRiskIntent(string intent)
    {
        // GeneralTool is intentionally excluded: it's the catch-all intent used
        // by the legacy v1 path and by the LLM classifier when no specific intent
        // is clear. Blocking non-read-only tools for GeneralTool would break
        // memory writes, file tasks, etc. on the v1 path.
        return intent is "LookupFact" or "LookupNews" or "LookupDeepDive" or "ChatOnly";
    }
}
