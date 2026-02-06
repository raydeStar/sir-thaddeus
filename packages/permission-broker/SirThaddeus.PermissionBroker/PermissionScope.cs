namespace SirThaddeus.PermissionBroker;

/// <summary>
/// Defines the scope/constraints for a permission token.
/// Scopes limit what the permission can be used for.
/// </summary>
public sealed record PermissionScope
{
    /// <summary>
    /// For ScreenRead: which window(s) can be read.
    /// </summary>
    public string? Window { get; init; }

    /// <summary>
    /// For ScreenRead: maximum number of frames/captures.
    /// </summary>
    public int? MaxFrames { get; init; }

    /// <summary>
    /// For BrowserControl: allowed domain(s).
    /// </summary>
    public string? AllowedDomain { get; init; }

    /// <summary>
    /// For FileAccess: allowed path prefix.
    /// </summary>
    public string? PathPrefix { get; init; }

    /// <summary>
    /// Creates an empty (unrestricted within capability) scope.
    /// </summary>
    public static PermissionScope Empty => new();

    /// <summary>
    /// Creates a scope limited to the active window.
    /// </summary>
    public static PermissionScope ActiveWindow => new() { Window = "active", MaxFrames = 1 };

    /// <summary>
    /// Creates a scope limited to the specified domain.
    /// </summary>
    public static PermissionScope ForDomain(string domain) => new() { AllowedDomain = domain };

    /// <summary>
    /// Creates a scope limited to the specified path prefix.
    /// </summary>
    public static PermissionScope ForPath(string pathPrefix) => new() { PathPrefix = pathPrefix };

    /// <summary>
    /// Gets a human-readable summary of the scope.
    /// </summary>
    public string ToSummary()
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(Window))
            parts.Add($"Window: {Window}");
        if (MaxFrames.HasValue)
            parts.Add($"Max frames: {MaxFrames}");
        if (!string.IsNullOrEmpty(AllowedDomain))
            parts.Add($"Domain: {AllowedDomain}");
        if (!string.IsNullOrEmpty(PathPrefix))
            parts.Add($"Path: {PathPrefix}");

        return parts.Count > 0 ? string.Join(", ", parts) : "No restrictions";
    }
}
