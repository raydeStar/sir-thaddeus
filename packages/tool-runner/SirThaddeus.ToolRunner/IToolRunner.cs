namespace SirThaddeus.ToolRunner;

/// <summary>
/// Interface for executing tools with permission enforcement.
/// </summary>
public interface IToolRunner
{
    /// <summary>
    /// Attempts to execute a tool call with permission enforcement.
    /// </summary>
    /// <param name="call">The tool call to execute.</param>
    /// <param name="tokenId">
    /// Optional permission token ID. If provided, the token is validated.
    /// If not provided, the runner may request permission from the user.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for aborting execution.</param>
    /// <returns>The result of the tool execution.</returns>
    Task<ToolResult> ExecuteAsync(
        ToolCall call,
        string? tokenId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a tool with this runner.
    /// </summary>
    /// <param name="tool">The tool to register.</param>
    void RegisterTool(ITool tool);

    /// <summary>
    /// Gets all registered tool names.
    /// </summary>
    IReadOnlyList<string> RegisteredTools { get; }

    /// <summary>
    /// Checks if a tool is registered.
    /// </summary>
    bool HasTool(string name);
}
