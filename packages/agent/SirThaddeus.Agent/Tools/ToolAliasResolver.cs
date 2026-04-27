using System.Text;

namespace SirThaddeus.Agent.Tools;

/// <summary>
/// Resolves MCP tool name aliases (snake_case ↔ PascalCase) and executes
/// tool calls with automatic fallback to the alternate name when the
/// primary name returns an "unknown tool" error. Shared by pipeline
/// steps and utility handlers so both sides agree on which name wins.
/// </summary>
public sealed class ToolAliasResolver
{
    private readonly IMcpToolClient _mcp;

    public ToolAliasResolver(IMcpToolClient mcp)
    {
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
    }

    /// <summary>
    /// Calls a tool by primary name, falling back to alternate name if the
    /// primary returns an unknown-tool error or throws.
    /// </summary>
    public async Task<(string ToolName, string Result, bool Success)> CallWithAliasAsync(
        string primaryToolName,
        string alternateToolName,
        string argsJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mcp.CallToolAsync(primaryToolName, argsJson, cancellationToken);
            if (IsUnknownToolError(result, primaryToolName))
            {
                try
                {
                    var altResult = await _mcp.CallToolAsync(alternateToolName, argsJson, cancellationToken);
                    return (alternateToolName, altResult, !IsErrorResponse(altResult));
                }
                catch (Exception alternateError)
                {
                    var errorText = $"Error: {result}; fallback failed: {alternateError.Message}";
                    return (primaryToolName, errorText, false);
                }
            }

            return (primaryToolName, result, !IsErrorResponse(result));
        }
        catch (Exception primaryError)
        {
            try
            {
                var result = await _mcp.CallToolAsync(alternateToolName, argsJson, cancellationToken);
                return (alternateToolName, result, !IsErrorResponse(result));
            }
            catch (Exception alternateError)
            {
                var errorText = $"Error: {primaryError.Message}; fallback failed: {alternateError.Message}";
                return (primaryToolName, errorText, false);
            }
        }
    }

    /// <summary>
    /// Routes a utility tool call through the known alias table, falling
    /// back to auto-generated PascalCase if the tool name isn't recognized.
    /// </summary>
    public async Task<(string ToolName, string Result, bool Success)> CallUtilityWithAliasAsync(
        string toolName,
        string argsJson,
        CancellationToken cancellationToken)
    {
        return toolName.ToLowerInvariant() switch
        {
            ToolNames.HolidaysGet => await CallWithAliasAsync(
                ToolNames.HolidaysGet, ToolNames.HolidaysGetAlt, argsJson, cancellationToken),
            ToolNames.HolidaysNext => await CallWithAliasAsync(
                ToolNames.HolidaysNext, ToolNames.HolidaysNextAlt, argsJson, cancellationToken),
            ToolNames.HolidaysIsToday => await CallWithAliasAsync(
                ToolNames.HolidaysIsToday, ToolNames.HolidaysIsTodayAlt, argsJson, cancellationToken),
            ToolNames.FeedFetch => await CallWithAliasAsync(
                ToolNames.FeedFetch, ToolNames.FeedFetchAlt, argsJson, cancellationToken),
            ToolNames.StatusCheck => await CallWithAliasAsync(
                ToolNames.StatusCheck, ToolNames.StatusCheckAlt, argsJson, cancellationToken),
            ToolNames.ResolveTimezone => await CallWithAliasAsync(
                ToolNames.ResolveTimezone, ToolNames.ResolveTimezoneAlt, argsJson, cancellationToken),
            _ => await CallWithAliasAsync(
                toolName, ToPascalCaseAlias(toolName), argsJson, cancellationToken)
        };
    }

    /// <summary>
    /// Converts a snake_case tool name to its PascalCase equivalent.
    /// </summary>
    public static string ToPascalCaseAlias(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return toolName;

        var parts = toolName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0)
                continue;
            sb.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
                sb.Append(part[1..]);
        }

        return sb.Length == 0 ? toolName : sb.ToString();
    }

    internal static bool IsUnknownToolError(string payload, string requestedTool)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        if (!payload.Contains("Unknown tool", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(requestedTool))
            return true;

        if (payload.Contains(requestedTool, StringComparison.OrdinalIgnoreCase))
            return true;

        var pascalAlias = ToPascalCaseAlias(requestedTool);
        return !string.IsNullOrWhiteSpace(pascalAlias) &&
               payload.Contains(pascalAlias, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects "Error:" prefixed responses from AuditedMcpToolClient
    /// (permission denied, safe mode, budget exceeded, execution failure).
    /// </summary>
    internal static bool IsErrorResponse(string? result) =>
        result is not null &&
        (result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
         result.StartsWith("Tool error:", StringComparison.OrdinalIgnoreCase));
}
