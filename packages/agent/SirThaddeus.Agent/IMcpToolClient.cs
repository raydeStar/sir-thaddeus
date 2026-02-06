namespace SirThaddeus.Agent;

/// <summary>
/// Abstraction for invoking tools on the MCP server.
/// The orchestrator uses this to execute tool calls requested by the LLM.
/// </summary>
public interface IMcpToolClient
{
    /// <summary>
    /// Calls a tool on the MCP server by name with JSON arguments.
    /// </summary>
    /// <param name="toolName">The MCP tool name (e.g. "BrowserNavigate").</param>
    /// <param name="argumentsJson">JSON-encoded arguments string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tool's text output.</returns>
    Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists available tools from the MCP server.
    /// Used to build the tool definition list sent to the LLM.
    /// </summary>
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Metadata about a tool available on the MCP server.
/// </summary>
public sealed record McpToolInfo
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>
    /// JSON Schema for the tool's input parameters.
    /// </summary>
    public required object InputSchema { get; init; }
}
