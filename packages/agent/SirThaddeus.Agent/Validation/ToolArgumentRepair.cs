using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Validation;

/// <summary>
/// Builds an actionable, schema-aware repair message when a tool call's
/// arguments are malformed, so a small model can re-formulate the call instead
/// of giving up or guessing. The message is emitted as a structured error
/// payload (the same shape real tools use) so it never breaks the harness's
/// structured-error contract. Pure logic — no LLM, no I/O, and no hardcoded
/// per-tool answers; everything comes from the tool's own declared schema.
/// </summary>
public static class ToolArgumentRepair
{
    /// <summary>
    /// True for issues that mean the tool definitely cannot run as called
    /// (unparseable JSON, non-object args, or a missing/empty required
    /// parameter) — as opposed to softer warnings like an unknown parameter,
    /// which many tools simply ignore. Pre-flight repair only fires on these,
    /// so a call that would have succeeded is never blocked.
    /// </summary>
    public static bool IsFatalIssue(string issue) =>
        issue.Contains("missing", StringComparison.OrdinalIgnoreCase)
        || issue.Contains("empty", StringComparison.OrdinalIgnoreCase)
        || issue.Contains("Invalid JSON", StringComparison.OrdinalIgnoreCase)
        || issue.Contains("must be a JSON object", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Structured error JSON that tells the model what was wrong and which
    /// parameters the tool accepts, then asks it to retry with corrected
    /// arguments (or answer without the tool).
    /// </summary>
    public static string BuildStructuredError(
        string toolName, ToolDefinition toolDef, IReadOnlyList<string> issues)
    {
        ArgumentNullException.ThrowIfNull(toolDef);

        var problems = issues is { Count: > 0 }
            ? string.Join("; ", issues)
            : "arguments did not match the tool's schema";
        var parameters = ExtractParameterNames(toolDef);
        var expects = parameters.Count > 0
            ? $" Valid parameters: {string.Join(", ", parameters)}."
            : string.Empty;

        var message =
            $"Invalid arguments for '{toolName}': {problems}.{expects} " +
            $"Call '{toolName}' again with corrected arguments, or answer without it " +
            "if you already have enough information.";

        return JsonSerializer.Serialize(new
        {
            error = new { code = "invalid_arguments", message, retriable = true }
        });
    }

    private static IReadOnlyList<string> ExtractParameterNames(ToolDefinition toolDef)
    {
        var rawParams = toolDef.Function?.Parameters;
        if (rawParams is null)
            return [];

        try
        {
            var element = rawParams is JsonElement je
                ? je
                : JsonSerializer.SerializeToElement(rawParams);
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("properties", out var props) &&
                props.ValueKind == JsonValueKind.Object)
            {
                return props.EnumerateObject().Select(p => p.Name).ToList();
            }
        }
        catch
        {
            // Unparseable schema — fall through to no parameter hint.
        }

        return [];
    }
}
