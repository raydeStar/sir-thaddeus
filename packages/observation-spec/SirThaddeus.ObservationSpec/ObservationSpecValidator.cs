using System.Text.RegularExpressions;

namespace SirThaddeus.ObservationSpec;

/// <summary>
/// Validates Observation Specification documents.
/// Ensures all required fields are present and values are within acceptable ranges.
/// </summary>
public sealed partial class ObservationSpecValidator
{
    private static readonly HashSet<string> SupportedVersions = ["1.0"];
    private static readonly HashSet<string> SupportedMethods = ["GET", "HEAD", "OPTIONS"];
    private static readonly HashSet<string> SupportedNotifyChannels = ["local_notification", "push", "email"];
    private static readonly HashSet<string> SupportedScopes = ["visible_text", "full_html", "json_body"];

    // Minimum interval of 5 minutes to prevent abuse
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Validates an observation spec document.
    /// </summary>
    /// <param name="spec">The spec to validate.</param>
    /// <returns>Validation result with any errors found.</returns>
    public ValidationResult Validate(ObservationSpecDocument spec)
    {
        var errors = new List<string>();

        // Version validation
        if (!SupportedVersions.Contains(spec.Version))
        {
            errors.Add($"Unsupported version: '{spec.Version}'. Supported: {string.Join(", ", SupportedVersions)}");
        }

        // Target validation
        ValidateTarget(spec.Target, errors);

        // Check validation
        ValidateCheck(spec.Check, errors);

        // Schedule validation
        ValidateSchedule(spec.Schedule, errors);

        // Notify validation
        ValidateNotify(spec.Notify, errors);

        // Limits validation
        ValidateLimits(spec.Limits, errors);

        return new ValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors.AsReadOnly()
        };
    }

    private static void ValidateTarget(ObservationTarget target, List<string> errors)
    {
        // URL must be valid absolute URL
        if (string.IsNullOrWhiteSpace(target.Url))
        {
            errors.Add("Target URL is required.");
        }
        else if (!Uri.TryCreate(target.Url, UriKind.Absolute, out var uri))
        {
            errors.Add($"Invalid target URL format: '{target.Url}'");
        }
        else if (uri.Scheme != "https" && uri.Scheme != "http")
        {
            errors.Add($"Target URL must use http or https scheme. Found: '{uri.Scheme}'");
        }

        // Method must be safe (read-only)
        if (!SupportedMethods.Contains(target.Method.ToUpperInvariant()))
        {
            errors.Add($"Unsupported HTTP method: '{target.Method}'. Only read-only methods allowed: {string.Join(", ", SupportedMethods)}");
        }
    }

    private static void ValidateCheck(ObservationCheck check, List<string> errors)
    {
        // Value is required
        if (string.IsNullOrWhiteSpace(check.Value))
        {
            errors.Add("Check value is required.");
        }

        // Validate scope if provided
        if (!string.IsNullOrEmpty(check.Scope) && !SupportedScopes.Contains(check.Scope))
        {
            errors.Add($"Unsupported check scope: '{check.Scope}'. Supported: {string.Join(", ", SupportedScopes)}");
        }

        // Type-specific validation
        switch (check.Type)
        {
            case CheckType.RegexMatch:
                try
                {
                    _ = new Regex(check.Value, RegexOptions.None, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException ex)
                {
                    errors.Add($"Invalid regex pattern: {ex.Message}");
                }
                break;

            case CheckType.JsonPathEquals:
            case CheckType.JsonPathExists:
                if (string.IsNullOrWhiteSpace(check.Path))
                {
                    errors.Add($"JSON path is required for check type '{check.Type}'.");
                }
                break;
        }
    }

    private void ValidateSchedule(ObservationSchedule schedule, List<string> errors)
    {
        // Parse and validate interval
        var interval = ParseInterval(schedule.Interval);
        if (interval == null)
        {
            errors.Add($"Invalid interval format: '{schedule.Interval}'. Use formats like '30m', '1h', '6h'.");
        }
        else if (interval < MinInterval)
        {
            errors.Add($"Interval too short. Minimum is {MinInterval.TotalMinutes} minutes.");
        }
    }

    private static void ValidateNotify(ObservationNotify notify, List<string> errors)
    {
        if (notify.OnMatch.Count == 0)
        {
            errors.Add("At least one notification channel is required.");
        }

        foreach (var channel in notify.OnMatch)
        {
            if (!SupportedNotifyChannels.Contains(channel))
            {
                errors.Add($"Unsupported notification channel: '{channel}'. Supported: {string.Join(", ", SupportedNotifyChannels)}");
            }
        }
    }

    private static void ValidateLimits(ObservationLimits limits, List<string> errors)
    {
        if (limits.MaxChecks <= 0)
        {
            errors.Add("max_checks must be greater than 0.");
        }
        else if (limits.MaxChecks > 10000)
        {
            errors.Add("max_checks cannot exceed 10000.");
        }

        if (limits.ExpiresAt.HasValue && limits.ExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            errors.Add("expires_at must be in the future.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Interval Parsing
    // ─────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^(\d+)(m|h|d)$", RegexOptions.IgnoreCase)]
    private static partial Regex IntervalRegex();

    private static TimeSpan? ParseInterval(string interval)
    {
        var match = IntervalRegex().Match(interval);
        if (!match.Success)
            return null;

        var value = int.Parse(match.Groups[1].Value);
        var unit = match.Groups[2].Value.ToLowerInvariant();

        return unit switch
        {
            "m" => TimeSpan.FromMinutes(value),
            "h" => TimeSpan.FromHours(value),
            "d" => TimeSpan.FromDays(value),
            _ => null
        };
    }
}

/// <summary>
/// Result of validating an observation spec.
/// </summary>
public sealed record ValidationResult
{
    /// <summary>
    /// Whether the spec is valid.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// List of validation errors (empty if valid).
    /// </summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>
    /// Gets a formatted error message.
    /// </summary>
    public string GetErrorSummary() =>
        IsValid ? "No errors." : string.Join(Environment.NewLine, Errors.Select(e => $"• {e}"));
}
