using System.Text.Json.Serialization;

namespace SirThaddeus.ObservationSpec;

/// <summary>
/// Root object for an Observation Specification.
/// Describes what to watch, how to evaluate it, and what to do on match.
/// </summary>
public sealed record ObservationSpecDocument
{
    /// <summary>
    /// Spec version (e.g., "1.0").
    /// </summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>
    /// What to observe.
    /// </summary>
    [JsonPropertyName("target")]
    public required ObservationTarget Target { get; init; }

    /// <summary>
    /// How to evaluate the observed content.
    /// </summary>
    [JsonPropertyName("check")]
    public required ObservationCheck Check { get; init; }

    /// <summary>
    /// How often to check.
    /// </summary>
    [JsonPropertyName("schedule")]
    public required ObservationSchedule Schedule { get; init; }

    /// <summary>
    /// What happens on match.
    /// </summary>
    [JsonPropertyName("notify")]
    public required ObservationNotify Notify { get; init; }

    /// <summary>
    /// Safety and fairness bounds.
    /// </summary>
    [JsonPropertyName("limits")]
    public required ObservationLimits Limits { get; init; }

    /// <summary>
    /// Creates a template for a new observation spec.
    /// </summary>
    public static ObservationSpecDocument CreateTemplate() => new()
    {
        Version = "1.0",
        Target = new ObservationTarget
        {
            Type = TargetType.WebPage,
            Url = "https://example.com",
            Method = "GET"
        },
        Check = new ObservationCheck
        {
            Type = CheckType.TextContains,
            Value = "In Stock",
            Scope = "visible_text"
        },
        Schedule = new ObservationSchedule
        {
            Interval = "30m",
            Jitter = "±5m"
        },
        Notify = new ObservationNotify
        {
            OnMatch = ["local_notification"],
            Once = true
        },
        Limits = new ObservationLimits
        {
            MaxChecks = 500,
            ExpiresAt = DateTimeOffset.UtcNow.AddMonths(1)
        }
    };
}

/// <summary>
/// Describes what is being observed.
/// </summary>
public sealed record ObservationTarget
{
    /// <summary>
    /// Target type (web_page, api_endpoint).
    /// </summary>
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required TargetType Type { get; init; }

    /// <summary>
    /// URL to observe.
    /// </summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>
    /// HTTP method (GET, HEAD, etc.).
    /// </summary>
    [JsonPropertyName("method")]
    public string Method { get; init; } = "GET";
}

/// <summary>
/// Target type for observation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TargetType
{
    /// <summary>
    /// HTML web page fetch.
    /// </summary>
    [JsonPropertyName("web_page")]
    WebPage,

    /// <summary>
    /// JSON API endpoint.
    /// </summary>
    [JsonPropertyName("api_endpoint")]
    ApiEndpoint
}

/// <summary>
/// Describes what signal matters in the observation.
/// </summary>
public sealed record ObservationCheck
{
    /// <summary>
    /// Check type (text_contains, regex_match, etc.).
    /// </summary>
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required CheckType Type { get; init; }

    /// <summary>
    /// The value to check for.
    /// </summary>
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    /// <summary>
    /// Where to look (visible_text, full_html, json_body).
    /// </summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    /// <summary>
    /// JSON path for json_path_* check types.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

/// <summary>
/// Check type for observation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CheckType
{
    /// <summary>
    /// Text contains the specified value.
    /// </summary>
    [JsonPropertyName("text_contains")]
    TextContains,

    /// <summary>
    /// Text does not contain the specified value.
    /// </summary>
    [JsonPropertyName("text_not_contains")]
    TextNotContains,

    /// <summary>
    /// Text matches the specified regex.
    /// </summary>
    [JsonPropertyName("regex_match")]
    RegexMatch,

    /// <summary>
    /// JSON path equals the specified value.
    /// </summary>
    [JsonPropertyName("json_path_equals")]
    JsonPathEquals,

    /// <summary>
    /// JSON path exists in the response.
    /// </summary>
    [JsonPropertyName("json_path_exists")]
    JsonPathExists
}

/// <summary>
/// Describes how often the observation runs.
/// </summary>
public sealed record ObservationSchedule
{
    /// <summary>
    /// Interval between checks (e.g., "30m", "1h").
    /// </summary>
    [JsonPropertyName("interval")]
    public required string Interval { get; init; }

    /// <summary>
    /// Random jitter to avoid hammering (e.g., "±5m").
    /// </summary>
    [JsonPropertyName("jitter")]
    public string? Jitter { get; init; }
}

/// <summary>
/// Describes what happens on match.
/// </summary>
public sealed record ObservationNotify
{
    /// <summary>
    /// Notification channels to use on match.
    /// </summary>
    [JsonPropertyName("on_match")]
    public required IReadOnlyList<string> OnMatch { get; init; }

    /// <summary>
    /// Whether to notify only once (true) or on every match.
    /// </summary>
    [JsonPropertyName("once")]
    public bool Once { get; init; }
}

/// <summary>
/// Safety and fairness bounds.
/// </summary>
public sealed record ObservationLimits
{
    /// <summary>
    /// Maximum number of checks before auto-stop.
    /// </summary>
    [JsonPropertyName("max_checks")]
    public int MaxChecks { get; init; }

    /// <summary>
    /// Hard expiration date for the observation.
    /// </summary>
    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; init; }
}
