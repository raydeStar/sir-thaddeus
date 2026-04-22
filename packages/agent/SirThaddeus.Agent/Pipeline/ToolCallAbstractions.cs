namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Optional per-tool args preprocessor. Runs once per tool invocation
/// before the call reaches the MCP layer or any interceptor. Used by the
/// runtime to, for example, force a recency default on <c>web_search</c>
/// calls issued inside an automation run. Stateless across turns.
/// </summary>
public interface IToolArgsRewriter
{
    /// <summary>
    /// Returns the rewritten arguments JSON for the call. Implementations
    /// should return <paramref name="argumentsJson"/> unchanged when they
    /// don't apply.
    /// </summary>
    string Rewrite(TurnContext context, string toolName, string argumentsJson);
}

/// <summary>
/// Optional virtual-tool handler. Lets a runtime intercept specific
/// tool names (e.g. <c>propose_automation</c>) and produce a result
/// without touching the MCP server. The first interceptor that returns
/// a non-null outcome claims the call — later interceptors and the MCP
/// fallthrough are skipped.
/// </summary>
public interface IToolCallInterceptor
{
    /// <summary>
    /// Try to handle the call. Return null to pass through to the next
    /// handler or the MCP server.
    /// </summary>
    Task<ToolCallOutcome?> TryInterceptAsync(
        TurnContext context,
        string toolName,
        string argumentsJson,
        string activityId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result of a tool call — either from the MCP server, an interceptor, or
/// a permission-denied stub. Downstream the outcome is serialized into
/// the LLM history as the tool result and emitted as a
/// <c>chat.tool.completed</c> event.
/// </summary>
public sealed record ToolCallOutcome(string ResultText, bool Ok, string? Error);

/// <summary>
/// Classifies an MCP tool name into a coarse group ("Web", "Files",
/// "Screen", etc.) for UI grouping and iconography. Abstracted so the
/// core pipeline doesn't pull in the runtime-specific group policy.
/// </summary>
public interface IToolGroupClassifier
{
    /// <summary>Returns the group label for a tool. Must not throw.</summary>
    string Classify(string toolName);
}

/// <summary>
/// Fallback classifier that labels every tool as "Unknown". Used when the
/// pipeline runs without a runtime-supplied classifier (tests, harness,
/// minimal CLI). UIs can still render the chip; they just can't pick a
/// specific icon.
/// </summary>
public sealed class DefaultToolGroupClassifier : IToolGroupClassifier
{
    public static readonly DefaultToolGroupClassifier Instance = new();

    public string Classify(string toolName) => "Unknown";
}
