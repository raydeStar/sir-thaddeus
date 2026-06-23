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

        var code = stubValue.Trim().ToLowerInvariant() switch
        {
            "tool_unavailable" or "unavailable" => "tool_unavailable",
            "timeout" or "timed_out" => "timeout",
            "permission_denied" or "permission" => "permission_denied",
            "policy_denied" or "tool_not_allowed" => "tool_not_allowed",
            _ => stubValue.Trim().ToLowerInvariant().Replace(' ', '_')
        };

        var message = code switch
        {
            "tool_unavailable" => $"{toolName} is currently unavailable.",
            "timeout" => $"{toolName} timed out.",
            "permission_denied" => $"Access to {toolName} was denied.",
            "tool_not_allowed" => $"{toolName} is not allowed for this run.",
            _ => $"{toolName} failed: {stubValue.Trim()}"
        };

        return $$"""{"error":{"code":"{{code}}","message":"{{EscapeJson(message)}}"},"tool":"{{EscapeJson(toolName)}}","stub":true}""";
    }

    private static string NormalizeName(string toolName) =>
        (toolName ?? "").ToUpperInvariant().Replace('-', '_');

    private static string EscapeJson(string value) =>
        (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
}
