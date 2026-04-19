namespace SirThaddeus.McpServer.Tools;

/// <summary>
/// Runtime guard that allows tools to be force-failed via environment variables.
/// Used during integration testing to simulate unavailable or erroring tools
/// without modifying tool logic. Set <c>ST_STUB_&lt;TOOL_NAME&gt;</c> to the
/// desired failure mode (e.g. "tool_unavailable", "timeout").
/// </summary>
internal static class ToolStubGuard
{
    /// <summary>
    /// If the tool is stubbed via environment variable, returns the error response.
    /// Returns <c>null</c> when the tool should execute normally.
    /// </summary>
    public static string? GetStubbedError(string toolName)
    {
        var envKey = $"ST_STUB_{NormalizeName(toolName)}";
        var stubValue = Environment.GetEnvironmentVariable(envKey);
        if (string.IsNullOrWhiteSpace(stubValue))
            return null;

        return stubValue.Trim().ToLowerInvariant() switch
        {
            "tool_unavailable" => $"Error: {toolName} is currently unavailable.",
            "timeout" => $"Error: {toolName} timed out.",
            "permission_denied" => $"Error: Access to {toolName} was denied.",
            _ => $"Error: {toolName} failed: {stubValue}"
        };
    }

    private static string NormalizeName(string toolName) =>
        (toolName ?? "").ToUpperInvariant().Replace('-', '_');
}
