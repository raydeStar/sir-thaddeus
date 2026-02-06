using SirThaddeus.PermissionBroker;

namespace SirThaddeus.ToolRunner;

/// <summary>
/// Interface for an individual tool that can be executed.
/// </summary>
public interface ITool
{
    /// <summary>
    /// The unique name of this tool.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Human-readable description of what this tool does.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// The capability required to execute this tool.
    /// </summary>
    Capability RequiredCapability { get; }

    /// <summary>
    /// Executes the tool with the given execution context.
    /// </summary>
    /// <param name="context">Execution context containing call, token, and cancellation.</param>
    /// <returns>The output of the tool execution.</returns>
    Task<object?> ExecuteAsync(ToolExecutionContext context);
}
