using System.Text.Json;

namespace SirThaddeus.Agent.Search;

/// <summary>
/// Maps structured web tool errors into deterministic user-safe responses.
/// Keeps timeout and policy failures grounded so summarization never hallucinates.
/// </summary>
internal static class WebToolFailureMapper
{
    public static AgentResponse? TryBuildFailureResponse(
        string toolResult,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (!TryParseStructuredError(toolResult, out var code, out var message))
            return null;

        if (ContainsUnavailable(code) || ContainsUnavailable(message))
            return null;

        var text = BuildMessage(code, message);
        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = toolCallsMade.ToList(),
            LlmRoundTrips = 0
        };
    }

    private static string BuildMessage(string code, string message)
    {
        if (ContainsBudgetExceeded(code) || ContainsBudgetExceeded(message))
        {
            return "Web search hit the current per-turn tool budget before it could finish this request. " +
                   "Retry the request or raise the web tool budget in Settings.";
        }

        if (ContainsTimeout(code) || ContainsTimeout(message))
        {
            return "Web search hit a timeout before results were retrieved. " +
                   "Please retry in a moment or narrow the query.";
        }

        if (ContainsPolicyBlock(code) || ContainsPolicyBlock(message))
        {
            return "Web search was blocked by the current tool policy for this run.";
        }

        return "Web search failed before returning results. Please retry in a moment.";
    }

    internal static bool TryParseStructuredError(
        string payload,
        out string code,
        out string message)
    {
        code = "";
        message = "";
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        // Handle plain-text "Error: ..." messages (e.g., from tool stubs).
        if (payload.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            message = payload["Error:".Length..].Trim();
            return !string.IsNullOrWhiteSpace(message);
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("error", out var errorEl))
            {
                return false;
            }

            if (errorEl.ValueKind == JsonValueKind.String)
            {
                message = errorEl.GetString() ?? "";
                return !string.IsNullOrWhiteSpace(message);
            }

            if (errorEl.ValueKind != JsonValueKind.Object)
                return false;

            if (errorEl.TryGetProperty("code", out var codeEl) &&
                codeEl.ValueKind == JsonValueKind.String)
            {
                code = codeEl.GetString() ?? "";
            }

            if (errorEl.TryGetProperty("message", out var msgEl) &&
                msgEl.ValueKind == JsonValueKind.String)
            {
                message = msgEl.GetString() ?? "";
            }

            return !string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(message);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsBudgetExceeded(
        string payload,
        out string budgetName,
        out int limit)
    {
        budgetName = "";
        limit = 0;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (!doc.RootElement.TryGetProperty("error", out var errorEl) ||
                errorEl.ValueKind != JsonValueKind.String ||
                !string.Equals(errorEl.GetString(), "tool_budget_exceeded", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (doc.RootElement.TryGetProperty("budget", out var budgetEl) &&
                budgetEl.ValueKind == JsonValueKind.String)
            {
                budgetName = budgetEl.GetString() ?? "";
            }

            if (doc.RootElement.TryGetProperty("limit", out var limitEl) &&
                limitEl.TryGetInt32(out var parsedLimit))
            {
                limit = parsedLimit;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsTimeout(string value)
        => value.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("timed out", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsPolicyBlock(string value)
        => value.Contains("tool_not_allowed", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("tool_not_permitted", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("permission", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsUnavailable(string value)
        => value.Contains("tool_unavailable", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("unavailable", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsBudgetExceeded(string value)
        => value.Contains("tool_budget_exceeded", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("budget_exceeded", StringComparison.OrdinalIgnoreCase);
}
