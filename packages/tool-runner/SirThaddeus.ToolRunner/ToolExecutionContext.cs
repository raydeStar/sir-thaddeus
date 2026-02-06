using SirThaddeus.PermissionBroker;

namespace SirThaddeus.ToolRunner;

/// <summary>
/// Execution context passed to tools containing the call, token, and cancellation.
/// Tools can use this to enforce scope constraints.
/// </summary>
public sealed record ToolExecutionContext
{
    /// <summary>
    /// The tool call being executed.
    /// </summary>
    public required ToolCall Call { get; init; }

    /// <summary>
    /// The permission token authorizing this execution.
    /// </summary>
    public required PermissionToken Token { get; init; }

    /// <summary>
    /// Cancellation token for aborting execution.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    // ─────────────────────────────────────────────────────────────────
    // Scope Enforcement Helpers
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gets the scope from the token.
    /// </summary>
    public PermissionScope Scope => Token.Scope;

    /// <summary>
    /// Validates that a URL is allowed by the scope.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <returns>True if allowed; false otherwise.</returns>
    public bool IsUrlAllowed(Uri url)
    {
        if (string.IsNullOrEmpty(Scope.AllowedDomain))
            return true; // No domain restriction

        return url.Host.Equals(Scope.AllowedDomain, StringComparison.OrdinalIgnoreCase) ||
               url.Host.EndsWith("." + Scope.AllowedDomain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates that a file path is allowed by the scope.
    /// </summary>
    /// <param name="path">The file path to validate.</param>
    /// <returns>True if allowed; false otherwise.</returns>
    public bool IsPathAllowed(string path)
    {
        if (string.IsNullOrEmpty(Scope.PathPrefix))
            return true; // No path restriction

        var normalizedPath = Path.GetFullPath(path);
        var normalizedPrefix = Path.GetFullPath(Scope.PathPrefix);
        
        return normalizedPath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Throws if the URL is not allowed by the scope.
    /// </summary>
    public void EnforceUrlScope(Uri url)
    {
        if (!IsUrlAllowed(url))
        {
            throw new ScopeViolationException(
                $"URL '{url.Host}' is not allowed. Scope restricts to domain: {Scope.AllowedDomain}");
        }
    }

    /// <summary>
    /// Throws if the path is not allowed by the scope.
    /// </summary>
    public void EnforcePathScope(string path)
    {
        if (!IsPathAllowed(path))
        {
            throw new ScopeViolationException(
                $"Path '{path}' is not allowed. Scope restricts to prefix: {Scope.PathPrefix}");
        }
    }
}

/// <summary>
/// Exception thrown when a tool operation violates the permission scope.
/// </summary>
public sealed class ScopeViolationException : Exception
{
    public ScopeViolationException(string message) : base(message) { }
}
