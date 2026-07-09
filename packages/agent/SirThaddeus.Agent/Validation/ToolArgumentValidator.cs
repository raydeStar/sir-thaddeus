using System.Text.Json;
using SirThaddeus.Agent.Orchestration;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Validation;

/// <summary>
/// Validates proposed tool call arguments against the tool's declared
/// parameter schema. Pure logic — no LLM, no I/O.
///
/// Checks performed:
///   • Arguments are valid JSON
///   • Required parameters (from tool schema) are present
///   • No unknown parameters (if schema declares properties)
///   • Parameter types roughly match (string/number/boolean/object/array)
///
/// This is a best-effort validator for small-model reliability, not a
/// full JSON Schema validator. It catches the most common LLM failures
/// (missing required params, wrong types, garbage JSON).
/// </summary>
public static class ToolArgumentValidator
{
    /// <summary>
    /// Validates a single proposed tool call's arguments against its definition.
    /// </summary>
    public static ToolArgumentValidationResult Validate(
        ProposedToolCall call,
        ToolDefinition toolDef)
    {
        ArgumentNullException.ThrowIfNull(call);
        return Validate(call.ArgumentsJson, toolDef);
    }

    /// <summary>
    /// Validates raw tool-call argument JSON against the tool's schema. Same
    /// checks as the <see cref="ProposedToolCall"/> overload; used by the tool
    /// loop to pre-validate arguments before dispatching to the MCP server.
    /// </summary>
    public static ToolArgumentValidationResult Validate(
        string argumentsJson,
        ToolDefinition toolDef)
    {
        ArgumentNullException.ThrowIfNull(toolDef);

        var issues = new List<string>();

        // Parse arguments JSON
        JsonElement argsRoot;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            argsRoot = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new ToolArgumentValidationResult
            {
                IsValid = false,
                Issues = [$"Invalid JSON arguments: {ex.Message}"]
            };
        }

        if (argsRoot.ValueKind != JsonValueKind.Object)
        {
            return new ToolArgumentValidationResult
            {
                IsValid = false,
                Issues = ["Arguments must be a JSON object"]
            };
        }

        // Extract schema info from tool definition
        // Parameters is typed as object — serialize to JSON then parse
        var rawParams = toolDef.Function.Parameters;
        if (rawParams is null)
            return new ToolArgumentValidationResult { IsValid = true };

        JsonElement parameters;
        try
        {
            var paramJson = rawParams is JsonElement je
                ? je
                : JsonSerializer.SerializeToElement(rawParams);
            parameters = paramJson;
        }
        catch
        {
            // Can't parse schema — accept anything
            return new ToolArgumentValidationResult { IsValid = true };
        }

        if (parameters.ValueKind == JsonValueKind.Undefined ||
            parameters.ValueKind == JsonValueKind.Null)
        {
            return new ToolArgumentValidationResult { IsValid = true };
        }

        // Check required parameters
        if (parameters.TryGetProperty("required", out var requiredProp) &&
            requiredProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var req in requiredProp.EnumerateArray())
            {
                var paramName = req.GetString();
                if (paramName is null) continue;

                if (!argsRoot.TryGetProperty(paramName, out var val) ||
                    val.ValueKind == JsonValueKind.Null)
                {
                    issues.Add($"Required parameter '{paramName}' is missing");
                }
                else if (val.ValueKind == JsonValueKind.String &&
                         string.IsNullOrWhiteSpace(val.GetString()))
                {
                    issues.Add($"Required parameter '{paramName}' is empty");
                }
            }
        }

        // Check for unknown parameters (warning only — don't hard-fail)
        if (parameters.TryGetProperty("properties", out var propertiesProp) &&
            propertiesProp.ValueKind == JsonValueKind.Object)
        {
            var knownParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in propertiesProp.EnumerateObject())
                knownParams.Add(prop.Name);

            foreach (var prop in argsRoot.EnumerateObject())
            {
                if (!knownParams.Contains(prop.Name))
                    issues.Add($"Unknown parameter '{prop.Name}' not in tool schema");
            }

            // Type checking for known parameters
            foreach (var prop in argsRoot.EnumerateObject())
            {
                if (!propertiesProp.TryGetProperty(prop.Name, out var schemaProp))
                    continue;

                if (!schemaProp.TryGetProperty("type", out var typeProp))
                    continue;

                var expectedType = typeProp.GetString();
                if (expectedType is not null && !IsTypeCompatible(prop.Value, expectedType))
                {
                    issues.Add($"Parameter '{prop.Name}' has type {prop.Value.ValueKind} but schema expects {expectedType}");
                }
            }
        }

        return new ToolArgumentValidationResult
        {
            IsValid = issues.Count == 0,
            Issues = issues
        };
    }

    private static bool IsTypeCompatible(JsonElement value, string expectedType) =>
        expectedType switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" or "integer" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true // Unknown type — accept
        };
}

/// <summary>
/// Result of validating a single tool call's arguments against its schema.
/// </summary>
public sealed record ToolArgumentValidationResult
{
    public bool IsValid { get; init; }
    public IReadOnlyList<string> Issues { get; init; } = [];
}
