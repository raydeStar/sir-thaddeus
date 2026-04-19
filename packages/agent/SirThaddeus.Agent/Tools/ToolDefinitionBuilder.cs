using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Tools;

/// <summary>
/// Builds LLM tool definitions from MCP tool manifests.
/// Schema normalization stays in-agent for now.
/// </summary>
public sealed class ToolDefinitionBuilder
{
    private readonly IMcpToolClient _mcp;
    private IReadOnlyList<McpToolInfo>? _cachedMcpTools;

    public ToolDefinitionBuilder(IMcpToolClient mcp)
    {
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
    }

    /// <summary>
    /// Clears the cached MCP tool list, forcing a fresh ListToolsAsync on the next build.
    /// Call after MCP reconnect or tool manifest changes.
    /// </summary>
    public void InvalidateCache() => _cachedMcpTools = null;

    public async Task<IReadOnlyList<ToolDefinition>> BuildAsync(
        bool memoryEnabled,
        bool panicModeEnabled,
        bool safeModeEnabled,
        Action<string, string>? logEvent,
        CancellationToken cancellationToken)
    {
        if (safeModeEnabled)
        {
            logEvent?.Invoke("AGENT_TOOLS_DISABLED", "Safe mode is active. Tool discovery skipped.");
            return [];
        }

        try
        {
            var mcpTools = _cachedMcpTools ?? await _mcp.ListToolsAsync(cancellationToken);
            _cachedMcpTools = mcpTools;

            // When memory is disabled, hide memory_* tools entirely
            // so the model cannot attempt memory writes.
            var filteredTools = memoryEnabled
                ? mcpTools
                : mcpTools.Where(t =>
                        !t.Name.StartsWith("memory_", StringComparison.OrdinalIgnoreCase) &&
                        !t.Name.StartsWith("Memory", StringComparison.Ordinal))
                    .ToList();

            if (panicModeEnabled)
            {
                filteredTools = filteredTools
                    .Where(t =>
                    {
                        var canonical = AuditedMcpToolClient.Canonicalize(t.Name);
                        var group = ToolGroupPolicy.ResolveGroup(canonical);
                        return !ToolGroupPolicy.SideEffectGroups.Contains(group);
                    })
                    .ToList();
            }

            var harnessAllowedTools = GetHarnessAllowedToolsOverride();
            if (harnessAllowedTools.Count > 0)
            {
                filteredTools = FilterHarnessAllowedTools(filteredTools, harnessAllowedTools).ToList();
                logEvent?.Invoke(
                    "AGENT_TOOLS_HARNESS_FILTER",
                    $"{filteredTools.Count} harness-allowed tool(s): {string.Join(", ", filteredTools.Select(t => t.Name))}");
            }

            var definitions = filteredTools.Select(t => new ToolDefinition
            {
                Function = new FunctionDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = SanitizeSchemaForLocalLlm(t.InputSchema)
                }
            }).ToList();

            logEvent?.Invoke(
                "AGENT_TOOLS_LOADED",
                $"{definitions.Count} tool(s): {string.Join(", ", definitions.Select(d => d.Function.Name))}");

            foreach (var def in definitions)
            {
                var schemaJson = JsonSerializer.Serialize(
                    def.Function.Parameters,
                    new JsonSerializerOptions { WriteIndented = false });
                logEvent?.Invoke("AGENT_TOOL_SCHEMA", $"{def.Function.Name}: {schemaJson}");
            }

            return definitions;
        }
        catch (Exception ex)
        {
            logEvent?.Invoke("AGENT_TOOLS_FAILED", $"MCP tool discovery failed: {ex.Message}");
            return [];
        }
    }

    internal static IReadOnlyList<McpToolInfo> FilterHarnessAllowedTools(
        IReadOnlyList<McpToolInfo> tools,
        IReadOnlyList<string> allowedToolNames)
    {
        if (tools.Count == 0 || allowedToolNames.Count == 0)
            return tools;

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var toolName in allowedToolNames)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                continue;

            allowed.Add(toolName.Trim());
            allowed.Add(AuditedMcpToolClient.Canonicalize(toolName));
        }

        return tools
            .Where(tool =>
                allowed.Contains(tool.Name) ||
                allowed.Contains(AuditedMcpToolClient.Canonicalize(tool.Name)))
            .ToList();
    }

    private static IReadOnlyList<string> GetHarnessAllowedToolsOverride()
    {
        var raw = Environment.GetEnvironmentVariable("ST_HARNESS_ALLOWED_TOOLS");
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static object SanitizeSchemaForLocalLlm(object rawSchema)
    {
        try
        {
            var json = JsonSerializer.Serialize(rawSchema);
            using var doc = JsonDocument.Parse(json);
            return CleanSchemaNode(doc.RootElement);
        }
        catch
        {
            return new { type = "object", properties = new { } };
        }
    }

    private static Dictionary<string, object> CleanSchemaNode(JsonElement node)
    {
        var result = new Dictionary<string, object>();

        if (TryResolveUnionType(node, out var resolvedType, out var resolvedNode))
        {
            result["type"] = resolvedType;
            if (resolvedNode.HasValue && resolvedNode.Value.ValueKind == JsonValueKind.Object)
            {
                var inner = CleanSchemaNode(resolvedNode.Value);
                foreach (var kv in inner)
                {
                    if (kv.Key != "type")
                        result[kv.Key] = kv.Value;
                }
            }
        }

        foreach (var prop in node.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "type":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        var types = prop.Value.EnumerateArray()
                            .Where(v => v.ValueKind == JsonValueKind.String)
                            .Select(v => v.GetString()!)
                            .Where(t => t != "null")
                            .ToList();
                        result["type"] = types.Count > 0 ? types[0] : "string";
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        result["type"] = prop.Value.GetString()!;
                    }
                    break;

                case "description":
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var desc = prop.Value.GetString()!
                            .Replace("(", "", StringComparison.Ordinal)
                            .Replace(")", "", StringComparison.Ordinal)
                            .Replace("[", "", StringComparison.Ordinal)
                            .Replace("]", "", StringComparison.Ordinal)
                            .Replace("{", "", StringComparison.Ordinal)
                            .Replace("}", "", StringComparison.Ordinal);
                        result["description"] = desc;
                    }
                    break;

                case "required":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        result["required"] = prop.Value.EnumerateArray()
                            .Where(v => v.ValueKind == JsonValueKind.String)
                            .Select(v => v.GetString()!)
                            .ToArray();
                    }
                    break;

                case "enum":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        result["enum"] = prop.Value.EnumerateArray()
                            .Select(v => v.ToString())
                            .ToArray();
                    }
                    break;

                case "properties":
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        var props = new Dictionary<string, object>();
                        foreach (var p in prop.Value.EnumerateObject())
                        {
                            if (p.Name.Equals("cancellationToken", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (p.Value.ValueKind == JsonValueKind.Object)
                                props[p.Name] = CleanSchemaNode(p.Value);
                        }

                        result["properties"] = props;
                    }
                    break;

                case "items":
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                        result["items"] = CleanSchemaNode(prop.Value);
                    break;
            }
        }

        if (!result.ContainsKey("type"))
            result["type"] = "object";

        return result;
    }

    private static bool TryResolveUnionType(
        JsonElement node,
        out string resolvedType,
        out JsonElement? resolvedNode)
    {
        resolvedType = "string";
        resolvedNode = null;

        foreach (var keyword in new[] { "anyOf", "oneOf" })
        {
            if (!node.TryGetProperty(keyword, out var unionArray) ||
                unionArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var alt in unionArray.EnumerateArray())
            {
                if (alt.ValueKind != JsonValueKind.Object)
                    continue;

                if (!alt.TryGetProperty("type", out var typeEl) ||
                    typeEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var typeName = typeEl.GetString();
                if (typeName == "null")
                    continue;

                resolvedType = typeName!;
                resolvedNode = alt;
                return true;
            }

            return true;
        }

        return false;
    }
}
